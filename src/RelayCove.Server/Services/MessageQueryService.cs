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
}
