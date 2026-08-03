using System.Data;
using Microsoft.EntityFrameworkCore;
using RelayCove.Server.Data;
using RelayCove.Server.Data.Entities;
using RelayCove.Shared.Conversations;
using RelayCove.Shared.Messages;

namespace RelayCove.Server.Services;

public sealed class MessageReadService(
    RelayCoveDbContext dbContext,
    ServerClock clock,
    ILogger<MessageReadService> logger)
{
    public async Task<MessageOperationResult<ConversationReadReceipt>> MarkReadAsync(
        Guid actorUserId,
        Guid conversationId,
        long messageId,
        CancellationToken cancellationToken)
    {
        if (actorUserId == Guid.Empty || conversationId == Guid.Empty || messageId <= 0)
        {
            return new MessageOperationResult<ConversationReadReceipt>(MessageOperationStatus.InvalidRequest);
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var conversation = await ConversationAccessQuery
            .VisibleTo(dbContext, actorUserId)
            .AsTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.Id == conversationId,
                cancellationToken);
        if (conversation is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            logger.LogInformation(
                "Read-through by {ActorUserId} for {ConversationId} was denied before target lookup.",
                actorUserId,
                conversationId);
            return new MessageOperationResult<ConversationReadReceipt>(MessageOperationStatus.AccessRevoked);
        }

        var targetExists = await dbContext.Messages.AnyAsync(
            message => message.Id == messageId && message.ConversationId == conversation.Id,
            cancellationToken);
        if (!targetExists)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new MessageOperationResult<ConversationReadReceipt>(MessageOperationStatus.ReadTargetInvalid);
        }

        var member = await dbContext.ConversationMembers.SingleOrDefaultAsync(
            candidate =>
                candidate.ConversationId == conversation.Id &&
                candidate.UserId == actorUserId,
            cancellationToken);
        var createdPublicState = false;
        if (member is null)
        {
            if (conversation.Type != ConversationType.PublicChannel)
            {
                throw new InvalidOperationException(
                    $"Authorized non-public conversation {conversation.Id:D} has no actor membership.");
            }

            member = new ConversationMember(
                conversation.Id,
                actorUserId,
                ConversationMemberRole.Member,
                clock.UtcNow,
                lastReadMessageId: messageId);
            dbContext.ConversationMembers.Add(member);
            createdPublicState = true;
        }
        else if (messageId > member.LastReadMessageId)
        {
            member.AdvanceLastReadMessageId(messageId);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation(
            "Read-through by {ActorUserId} for {ConversationId} requested {MessageId} and confirmed {LastReadMessageId}; createdPublicState={CreatedPublicState}.",
            actorUserId,
            conversation.Id,
            messageId,
            member.LastReadMessageId,
            createdPublicState);
        return new MessageOperationResult<ConversationReadReceipt>(
            MessageOperationStatus.Success,
            new ConversationReadReceipt(conversation.Id, member.LastReadMessageId));
    }
}
