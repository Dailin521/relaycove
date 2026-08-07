using Microsoft.EntityFrameworkCore;
using RelayCove.Server.Data;
using RelayCove.Shared.Conversations;

namespace RelayCove.Server.Realtime;

internal sealed class ConversationAccessGrantedPublisher(
    RelayCoveDbContext dbContext,
    IConversationAccessGrantedTransport transport,
    ILogger<ConversationAccessGrantedPublisher> logger)
{
    public async Task TryPublishAsync(Guid conversationId)
    {
        IReadOnlyList<NewMessageRecipient> recipients = [];
        try
        {
            recipients = await dbContext.Users
                .AsNoTracking()
                .Where(user =>
                    !user.IsDisabled &&
                    user.RetiredAt == null &&
                    dbContext.Conversations.Any(conversation =>
                        conversation.Id == conversationId &&
                        !conversation.IsDeleted &&
                        (conversation.Type == ConversationType.PublicChannel ||
                         conversation.Members.Any(member => member.UserId == user.Id))))
                .Select(user => new NewMessageRecipient(user.Id, user.AccessTokenVersion))
                .ToArrayAsync(CancellationToken.None);
            await transport.SendAsync(recipients, conversationId, CancellationToken.None);
            logger.LogInformation(
                "Published realtime conversation access grant for {ConversationId} to {RecipientCount} users.",
                conversationId,
                recipients.Count);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Realtime conversation access grant failed for {ConversationId} after resolving {RecipientCount} users.",
                conversationId,
                recipients.Count);
        }
    }
}
