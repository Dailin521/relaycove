using Microsoft.EntityFrameworkCore;
using RelayCove.Server.Data;
using RelayCove.Shared.Messages;

namespace RelayCove.Server.Services;

public sealed class MessageQueryService(RelayCoveDbContext dbContext)
{
    public async Task<MessageOperationResult<MessageHistoryResponse>> GetHistoryAsync(
        Guid actorUserId,
        Guid conversationId,
        long? beforeMessageId,
        int limit,
        CancellationToken cancellationToken)
    {
        if (actorUserId == Guid.Empty ||
            conversationId == Guid.Empty ||
            beforeMessageId is <= 0 ||
            limit is < 1 or > 100)
        {
            return new MessageOperationResult<MessageHistoryResponse>(MessageOperationStatus.InvalidRequest);
        }

        var candidates = dbContext.Messages
            .AsNoTracking()
            .Where(message =>
                message.ConversationId == conversationId &&
                (!beforeMessageId.HasValue || message.Id < beforeMessageId.Value))
            .OrderByDescending(message => message.Id)
            .Take(limit + 1);
        var rows = await (
                from conversation in ConversationAccessQuery.VisibleTo(dbContext, actorUserId)
                where conversation.Id == conversationId
                join message in candidates
                    on conversation.Id equals message.ConversationId into messageGroup
                from message in messageGroup.DefaultIfEmpty()
                join mention in dbContext.MessageMentions.AsNoTracking()
                    on message.Id equals mention.MessageId into mentionGroup
                from mention in mentionGroup.DefaultIfEmpty()
                orderby message.Id descending, mention.MentionedUserId
                select new MessageHistoryProjection(
                    message == null ? null : (long?)message.Id,
                    message == null ? null : (Guid?)message.ClientMessageId,
                    message == null ? null : (Guid?)message.ConversationId,
                    message == null ? null : (Guid?)message.SenderId,
                    message == null ? null : message.Sender.DisplayName,
                    message == null ? null : (MessageType?)message.Type,
                    message == null ? null : message.Content,
                    message == null ? null : message.ReplyToMessageId,
                    message == null ? null : (DateTime?)message.CreatedAt,
                    mention == null ? null : (Guid?)mention.MentionedUserId))
            .ToArrayAsync(cancellationToken);
        if (rows.Length == 0)
        {
            return new MessageOperationResult<MessageHistoryResponse>(MessageOperationStatus.AccessRevoked);
        }

        var projectedMessages = rows
            .Where(row => row.Id.HasValue)
            .GroupBy(row => row.Id!.Value)
            .Select(group =>
            {
                var message = group.First();
                return new MessageDto(
                message.Id!.Value,
                message.ClientMessageId!.Value,
                message.ConversationId!.Value,
                message.SenderId!.Value,
                message.SenderDisplayName!,
                message.Type!.Value,
                message.Content,
                message.ReplyToMessageId,
                Array.Empty<AttachmentDto>(),
                group.Where(row => row.MentionedUserId.HasValue)
                    .Select(row => row.MentionedUserId!.Value)
                    .ToArray(),
                new DateTimeOffset(message.CreatedAt!.Value));
            })
            .OrderByDescending(message => message.Id)
            .ToArray();
        var hasMore = projectedMessages.Length > limit;
        var messages = projectedMessages
            .Take(limit)
            .OrderBy(message => message.Id)
            .ToArray();
        return new MessageOperationResult<MessageHistoryResponse>(
            MessageOperationStatus.Success,
            new MessageHistoryResponse(
                messages,
                hasMore ? messages.Min(message => message.Id) : null,
                hasMore));
    }

    public async Task<MessageOperationResult<MessageAroundResponse>> GetAroundAsync(
        Guid actorUserId,
        Guid conversationId,
        long messageId,
        int before,
        int after,
        CancellationToken cancellationToken)
    {
        if (actorUserId == Guid.Empty ||
            conversationId == Guid.Empty ||
            messageId <= 0 ||
            before is < 0 or > MessageRequestValidator.MaximumAroundSideCount ||
            after is < 0 or > MessageRequestValidator.MaximumAroundSideCount)
        {
            return new MessageOperationResult<MessageAroundResponse>(MessageOperationStatus.InvalidRequest);
        }

        var targetExists = await ConversationAccessQuery
            .VisibleTo(dbContext, actorUserId)
            .Where(conversation => conversation.Id == conversationId)
            .Select(conversation =>
                (bool?)conversation.Messages.Any(candidate => candidate.Id == messageId))
            .SingleOrDefaultAsync(cancellationToken);
        if (targetExists is null)
        {
            return new MessageOperationResult<MessageAroundResponse>(MessageOperationStatus.AccessRevoked);
        }

        if (!targetExists.Value)
        {
            return new MessageOperationResult<MessageAroundResponse>(MessageOperationStatus.MessageTargetInvalid);
        }

        var visibleMessages =
            from conversation in ConversationAccessQuery.VisibleTo(dbContext, actorUserId)
            where conversation.Id == conversationId
            from message in conversation.Messages
            select message;
        var beforeCandidates = visibleMessages
            .Where(candidate => candidate.Id < messageId)
            .OrderByDescending(candidate => candidate.Id)
            .Take(before + 1);
        var targetCandidate = visibleMessages.Where(candidate => candidate.Id == messageId);
        var afterCandidates = visibleMessages
            .Where(candidate => candidate.Id > messageId)
            .OrderBy(candidate => candidate.Id)
            .Take(after + 1);
        var candidates = beforeCandidates
            .Concat(targetCandidate)
            .Concat(afterCandidates);
        var rows = await (
                from message in candidates
                join mention in dbContext.MessageMentions.AsNoTracking()
                    on message.Id equals mention.MessageId into mentionGroup
                from mention in mentionGroup.DefaultIfEmpty()
                select new MessageAroundProjection(
                    message.Id,
                    message.ClientMessageId,
                    message.ConversationId,
                    message.SenderId,
                    message.Sender.DisplayName,
                    message.Type,
                    message.Content,
                    message.ReplyToMessageId,
                    message.CreatedAt,
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
                    Array.Empty<AttachmentDto>(),
                    group.Where(row => row.MentionedUserId.HasValue)
                        .Select(row => row.MentionedUserId!.Value)
                        .Order()
                        .ToArray(),
                    new DateTimeOffset(message.CreatedAt));
            })
            .ToArray();
        var target = projectedMessages.SingleOrDefault(message => message.Id == messageId);
        if (target is null)
        {
            return new MessageOperationResult<MessageAroundResponse>(MessageOperationStatus.AccessRevoked);
        }

        var allBefore = projectedMessages
            .Where(message => message.Id < messageId)
            .OrderByDescending(message => message.Id)
            .ToArray();
        var allAfter = projectedMessages
            .Where(message => message.Id > messageId)
            .OrderBy(message => message.Id)
            .ToArray();
        var messages = allBefore
            .Take(before)
            .Append(target)
            .Concat(allAfter.Take(after))
            .OrderBy(message => message.Id)
            .ToArray();
        return new MessageOperationResult<MessageAroundResponse>(
            MessageOperationStatus.Success,
            new MessageAroundResponse(
                messages,
                target.Id,
                allBefore.Length > before,
                allAfter.Length > after));
    }

    private sealed record MessageHistoryProjection(
        long? Id,
        Guid? ClientMessageId,
        Guid? ConversationId,
        Guid? SenderId,
        string? SenderDisplayName,
        MessageType? Type,
        string? Content,
        long? ReplyToMessageId,
        DateTime? CreatedAt,
        Guid? MentionedUserId);

    private sealed record MessageAroundProjection(
        long Id,
        Guid ClientMessageId,
        Guid ConversationId,
        Guid SenderId,
        string SenderDisplayName,
        MessageType Type,
        string? Content,
        long? ReplyToMessageId,
        DateTime CreatedAt,
        Guid? MentionedUserId);
}
