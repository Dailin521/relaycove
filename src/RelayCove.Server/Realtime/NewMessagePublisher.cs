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
        IReadOnlyList<NewMessageRecipient> recipients = [];
        try
        {
            recipients = await dbContext.Users
                .AsNoTracking()
                .Where(user =>
                    !user.IsDisabled &&
                    dbContext.Conversations.Any(conversation =>
                        conversation.Id == message.ConversationId &&
                        !conversation.IsDeleted &&
                        (conversation.Type == ConversationType.PublicChannel ||
                         conversation.Members.Any(member => member.UserId == user.Id))))
                .Select(user => new NewMessageRecipient(user.Id, user.AccessTokenVersion))
                .ToArrayAsync(CancellationToken.None);
            await transport.SendAsync(recipients, message, CancellationToken.None);
            logger.LogInformation(
                "Published realtime message {MessageId} in {ConversationId} to {RecipientCount} users.",
                message.Id,
                message.ConversationId,
                recipients.Count);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Realtime delivery failed for message {MessageId} in {ConversationId} after resolving {RecipientCount} users.",
                message.Id,
                message.ConversationId,
                recipients.Count);
        }
    }
}
