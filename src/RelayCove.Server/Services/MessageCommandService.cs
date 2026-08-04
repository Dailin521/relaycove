using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RelayCove.Server.Data;
using RelayCove.Server.Data.Entities;
using RelayCove.Shared.Conversations;
using RelayCove.Shared.Messages;

namespace RelayCove.Server.Services;

public sealed class MessageCommandService(
    RelayCoveDbContext dbContext,
    AttachmentStoragePaths storagePaths,
    ServerClock clock,
    ILogger<MessageCommandService> logger)
{
    public async Task<MessageOperationResult<MessageDto>> SendAsync(
        Guid actorUserId,
        SendMessageRequest request,
        CancellationToken cancellationToken)
    {
        if (actorUserId == Guid.Empty ||
            request.ClientMessageId == Guid.Empty ||
            request.ConversationId == Guid.Empty ||
            request.AttachmentIds is null ||
            request.MentionUserIds is null ||
            !Enum.IsDefined(request.Type))
        {
            return new MessageOperationResult<MessageDto>(MessageOperationStatus.InvalidRequest);
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var conversation = await ConversationAccessQuery
            .VisibleTo(dbContext, actorUserId)
            .AsTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.Id == request.ConversationId,
                cancellationToken);
        if (conversation is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            logger.LogInformation(
                "Message send by {ActorUserId} to {ConversationId} was denied before idempotency lookup.",
                actorUserId,
                request.ConversationId);
            return new MessageOperationResult<MessageDto>(MessageOperationStatus.AccessRevoked);
        }

        if (request.Type == MessageType.System)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new MessageOperationResult<MessageDto>(MessageOperationStatus.MessageTypeUnsupported);
        }

        if (request.Type == MessageType.Text && request.AttachmentIds.Count != 0 ||
            request.Type is MessageType.Image or MessageType.File &&
            (request.AttachmentIds.Count is < 1 or > Message.MaximumAttachmentCount ||
             request.AttachmentIds.Any(attachmentId => attachmentId == Guid.Empty) ||
             request.AttachmentIds.Distinct().Count() != request.AttachmentIds.Count))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new MessageOperationResult<MessageDto>(MessageOperationStatus.AttachmentInvalid);
        }

        if (request.ReplyToMessageId is long replyToMessageId &&
            !await dbContext.Messages.AnyAsync(
                message => message.Id == replyToMessageId && message.ConversationId == conversation.Id,
                cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new MessageOperationResult<MessageDto>(MessageOperationStatus.ReplyInvalid);
        }

        var mentionUserIds = request.MentionUserIds.Order().ToArray();
        if (!await AreMentionsAccessibleAsync(conversation, mentionUserIds, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new MessageOperationResult<MessageDto>(MessageOperationStatus.MentionInvalid);
        }

        var attachmentIds = request.AttachmentIds.Order().ToArray();
        var attachments = attachmentIds.Length == 0
            ? []
            : await dbContext.Attachments
                .AsNoTracking()
                .Where(attachment =>
                    attachmentIds.Contains(attachment.Id) &&
                    attachment.UploaderUserId == actorUserId)
                .OrderBy(attachment => attachment.Id)
                .ToArrayAsync(cancellationToken);
        if (attachments.Length != attachmentIds.Length ||
            request.Type == MessageType.Image &&
            attachments.Any(attachment =>
                !attachment.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new MessageOperationResult<MessageDto>(MessageOperationStatus.AttachmentInvalid);
        }

        foreach (var attachment in attachments)
        {
            var storedPath = storagePaths.GetStoredFilePath(attachment.StoredFileName);
            if (!File.Exists(storedPath))
            {
                throw new InvalidOperationException("An attachment metadata row has no physical file.");
            }
        }

        Message message;
        try
        {
            message = new Message(
                request.ClientMessageId,
                request.ConversationId,
                actorUserId,
                request.Type,
                request.Content,
                request.ReplyToMessageId,
                clock.UtcNow);
            foreach (var mentionedUserId in mentionUserIds)
            {
                message.AddMention(mentionedUserId);
            }
        }
        catch (ArgumentException)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new MessageOperationResult<MessageDto>(MessageOperationStatus.InvalidRequest);
        }

        dbContext.Messages.Add(message);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsTargetIdempotencyConflict(exception))
        {
            dbContext.ChangeTracker.Clear();
            var existing = await dbContext.Messages
                .AsNoTracking()
                .Include(candidate => candidate.Sender)
                .Include(candidate => candidate.Mentions)
                .Include(candidate => candidate.Attachments)
                .SingleOrDefaultAsync(
                    candidate =>
                        candidate.SenderId == actorUserId &&
                        candidate.ClientMessageId == request.ClientMessageId,
                    cancellationToken)
                ?? throw new InvalidOperationException(
                    "The idempotency constraint was reported without a matching message row.",
                    exception);
            if (!PayloadMatches(existing, request, mentionUserIds, attachmentIds))
            {
                await transaction.RollbackAsync(cancellationToken);
                logger.LogWarning(
                    "User {ActorUserId} reused client message {ClientMessageId} with a different payload.",
                    actorUserId,
                    request.ClientMessageId);
                return new MessageOperationResult<MessageDto>(MessageOperationStatus.IdempotencyKeyReuse);
            }

            await transaction.CommitAsync(cancellationToken);
            logger.LogInformation(
                "User {ActorUserId} replayed message {MessageId} with client key {ClientMessageId} in {ConversationId}.",
                actorUserId,
                existing.Id,
                request.ClientMessageId,
                existing.ConversationId);
            return new MessageOperationResult<MessageDto>(
                MessageOperationStatus.Replay,
                MessageDtoFactory.Create(existing, existing.Sender.DisplayName));
        }

        if (attachmentIds.Length > 0)
        {
            var attachedCount = await dbContext.Attachments
                .Where(attachment =>
                    attachmentIds.Contains(attachment.Id) &&
                    attachment.UploaderUserId == actorUserId &&
                    attachment.MessageId == null)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(
                        attachment => attachment.MessageId,
                        message.Id),
                    cancellationToken);
            if (attachedCount != attachmentIds.Length)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return new MessageOperationResult<MessageDto>(MessageOperationStatus.AttachmentInvalid);
            }
        }

        conversation.Touch(message.CreatedAt);
        await dbContext.SaveChangesAsync(cancellationToken);
        var senderDisplayName = await dbContext.Users
            .Where(user => user.Id == actorUserId)
            .Select(user => user.DisplayName)
            .SingleAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation(
            "User {ActorUserId} created message {MessageId} with client key {ClientMessageId} in {ConversationId}.",
            actorUserId,
            message.Id,
            request.ClientMessageId,
            conversation.Id);
        return new MessageOperationResult<MessageDto>(
            MessageOperationStatus.Created,
            MessageDtoFactory.Create(message, senderDisplayName, attachments));
    }

    private async Task<bool> AreMentionsAccessibleAsync(
        Conversation conversation,
        IReadOnlyCollection<Guid> mentionUserIds,
        CancellationToken cancellationToken)
    {
        if (mentionUserIds.Count == 0)
        {
            return true;
        }

        var accessibleCount = await dbContext.Users.CountAsync(
            user =>
                mentionUserIds.Contains(user.Id) &&
                !user.IsDisabled &&
                (conversation.Type == ConversationType.PublicChannel ||
                 dbContext.ConversationMembers.Any(member =>
                     member.ConversationId == conversation.Id && member.UserId == user.Id)),
            cancellationToken);
        return accessibleCount == mentionUserIds.Count;
    }

    private static bool PayloadMatches(
        Message existing,
        SendMessageRequest request,
        IReadOnlyList<Guid> mentionUserIds,
        IReadOnlyList<Guid> attachmentIds) =>
        existing.ConversationId == request.ConversationId &&
        existing.Type == request.Type &&
        string.Equals(existing.Content, request.Content, StringComparison.Ordinal) &&
        existing.ReplyToMessageId == request.ReplyToMessageId &&
        existing.Attachments
            .Select(attachment => attachment.Id)
            .Order()
            .SequenceEqual(attachmentIds) &&
        existing.Mentions
            .Select(mention => mention.MentionedUserId)
            .Order()
            .SequenceEqual(mentionUserIds);

    private static bool IsTargetIdempotencyConflict(DbUpdateException exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqliteException
                {
                    SqliteErrorCode: 19,
                    SqliteExtendedErrorCode: 2067,
                } sqliteException &&
                sqliteException.Message.Contains(
                    "Messages.SenderId, Messages.ClientMessageId",
                    StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
