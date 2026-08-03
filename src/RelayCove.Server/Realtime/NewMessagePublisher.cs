using Microsoft.EntityFrameworkCore;
using RelayCove.Server.Data;
using RelayCove.Shared.Conversations;
using RelayCove.Shared.Messages;

namespace RelayCove.Server.Realtime;

internal sealed class NewMessagePublisher(
    RelayCoveDbContext dbContext,
    INewMessageTransport transport,
    ILogger<NewMessagePublisher> logger)
{
    public async Task TryPublishAsync(MessageDto message)
    {
        IReadOnlyList<string> recipientUserIds = [];
        try
        {
            var recipientIds = await dbContext.Users
                .AsNoTracking()
                .Where(user =>
                    !user.IsDisabled &&
                    dbContext.Conversations.Any(conversation =>
                        conversation.Id == message.ConversationId &&
                        !conversation.IsDeleted &&
                        (conversation.Type == ConversationType.PublicChannel ||
                         conversation.Members.Any(member => member.UserId == user.Id))))
                .Select(user => user.Id)
                .ToArrayAsync(CancellationToken.None);
            recipientUserIds = recipientIds
                .Select(userId => userId.ToString("D"))
                .ToArray();
            await transport.SendAsync(recipientUserIds, message, CancellationToken.None);
            logger.LogInformation(
                "Published realtime message {MessageId} in {ConversationId} to {RecipientCount} users.",
                message.Id,
                message.ConversationId,
                recipientUserIds.Count);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Realtime delivery failed for message {MessageId} in {ConversationId} after resolving {RecipientCount} users.",
                message.Id,
                message.ConversationId,
                recipientUserIds.Count);
        }
    }
}
