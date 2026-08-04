using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RelayCove.Server.Data;
using RelayCove.Shared.Conversations;
using RelayCove.Shared.Messages;

namespace RelayCove.Server.Services;

public sealed class MessageSyncService(RelayCoveDbContext dbContext)
{
    public async Task<SyncOperationResult> GetPageAsync(
        Guid actorUserId,
        long cursor,
        long? snapshotUpperBound,
        int limit,
        CancellationToken cancellationToken)
    {
        if (actorUserId == Guid.Empty ||
            cursor < 0 ||
            snapshotUpperBound is < 0 ||
            snapshotUpperBound.HasValue && snapshotUpperBound.Value < cursor ||
            limit is < 1 or > SyncRequestValidator.MaximumLimit)
        {
            return new SyncOperationResult(SyncOperationStatus.InvalidRequest);
        }

        await dbContext.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var connection = (SqliteConnection)dbContext.Database.GetDbConnection();
            // The provider exposes the deferred choice only on this synchronous BEGIN API.
            // BEGIN DEFERRED takes no write reservation; all actual data reads below remain asynchronous.
            await using var sqliteTransaction = connection.BeginTransaction(
                IsolationLevel.Serializable,
                deferred: true);
            await using var transaction = await dbContext.Database.UseTransactionAsync(
                    sqliteTransaction,
                    cancellationToken)
                ?? throw new InvalidOperationException("The SQLite read transaction could not be attached.");
            var state = await dbContext.Users
                .AsNoTracking()
                .Where(user => user.Id == actorUserId && !user.IsDisabled)
                .Select(_ => new SyncSnapshotState(
                    dbContext.Messages
                        .Select(message => (long?)message.Id)
                        .Max() ?? 0L))
                .SingleOrDefaultAsync(cancellationToken);
            if (state is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new SyncOperationResult(SyncOperationStatus.AuthenticationUnavailable);
            }

            if (cursor > state.CurrentMaximumMessageId ||
                snapshotUpperBound > state.CurrentMaximumMessageId)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new SyncOperationResult(SyncOperationStatus.CursorInvalid);
            }

            var effectiveUpperBound = snapshotUpperBound ?? state.CurrentMaximumMessageId;
            var candidates = dbContext.Messages
                .AsNoTracking()
                .Where(message =>
                    message.Id > cursor &&
                    message.Id <= effectiveUpperBound &&
                    !message.Conversation.IsDeleted &&
                    (message.Conversation.Type == ConversationType.PublicChannel ||
                     message.Conversation.Type == ConversationType.PrivateChannel &&
                     message.Conversation.Members.Any(member =>
                         member.UserId == actorUserId &&
                         message.Id > member.LastReadMessageId) ||
                     message.Conversation.Type == ConversationType.Direct &&
                     message.Conversation.Members.Any(member => member.UserId == actorUserId)))
                .OrderBy(message => message.Id)
                .Take(limit + 1);
            var rows = await (
                    from message in candidates
                    join attachment in dbContext.Attachments.AsNoTracking()
                        on message.Id equals attachment.MessageId into attachmentGroup
                    from attachment in attachmentGroup.DefaultIfEmpty()
                    join mention in dbContext.MessageMentions.AsNoTracking()
                        on message.Id equals mention.MessageId into mentionGroup
                    from mention in mentionGroup.DefaultIfEmpty()
                    orderby message.Id, mention.MentionedUserId
                    select new SyncMessageProjection(
                        message.Id,
                        message.ClientMessageId,
                        message.ConversationId,
                        message.SenderId,
                        message.Sender.DisplayName,
                        message.Type,
                        message.Content,
                        message.ReplyToMessageId,
                        message.CreatedAt,
                        attachment == null ? null : (Guid?)attachment.Id,
                        attachment == null ? null : attachment.OriginalFileName,
                        attachment == null ? null : attachment.ContentType,
                        attachment == null ? null : (long?)attachment.Size,
                        mention == null ? null : (Guid?)mention.MentionedUserId))
                .ToArrayAsync(cancellationToken);
            var projectedMessages = rows
                .GroupBy(row => row.Id)
                .Select(group =>
                {
                    var message = group.First();
                    return new MessageDto(
                        message.Id,
                        message.ClientMessageId,
                        message.ConversationId,
                        message.SenderId,
                        message.SenderDisplayName,
                        message.Type,
                        message.Content,
                        message.ReplyToMessageId,
                        group
                            .Where(row => row.AttachmentId.HasValue)
                            .GroupBy(row => row.AttachmentId!.Value)
                            .OrderBy(attachmentGroup => attachmentGroup.Key)
                            .Select(attachmentGroup =>
                            {
                                var attachment = attachmentGroup.First();
                                return AttachmentDtoFactory.Create(
                                    attachment.AttachmentId!.Value,
                                    attachment.AttachmentOriginalFileName!,
                                    attachment.AttachmentContentType!,
                                    attachment.AttachmentSize!.Value);
                            })
                            .ToArray(),
                        group.Where(row => row.MentionedUserId.HasValue)
                            .Select(row => row.MentionedUserId!.Value)
                            .Distinct()
                            .Order()
                            .ToArray(),
                        new DateTimeOffset(message.CreatedAt));
                })
                .OrderBy(message => message.Id)
                .ToArray();
            var hasMore = projectedMessages.Length > limit;
            var messages = projectedMessages.Take(limit).ToArray();
            var nextCursor = hasMore ? messages[^1].Id : effectiveUpperBound;
            var response = new SyncResponse(messages, nextCursor, effectiveUpperBound, hasMore);
            await transaction.CommitAsync(cancellationToken);
            return new SyncOperationResult(SyncOperationStatus.Success, response);
        }
        finally
        {
            await dbContext.Database.CloseConnectionAsync();
        }
    }

    private sealed record SyncSnapshotState(long CurrentMaximumMessageId);

    private sealed record SyncMessageProjection(
        long Id,
        Guid ClientMessageId,
        Guid ConversationId,
        Guid SenderId,
        string SenderDisplayName,
        MessageType Type,
        string? Content,
        long? ReplyToMessageId,
        DateTime CreatedAt,
        Guid? AttachmentId,
        string? AttachmentOriginalFileName,
        string? AttachmentContentType,
        long? AttachmentSize,
        Guid? MentionedUserId);
}
