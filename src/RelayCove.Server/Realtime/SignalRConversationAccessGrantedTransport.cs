using Microsoft.AspNetCore.SignalR;
using RelayCove.Server.Hubs;

namespace RelayCove.Server.Realtime;

internal sealed class SignalRConversationAccessGrantedTransport(
    IHubContext<ChatHub, IChatClient> hubContext) : IConversationAccessGrantedTransport
{
    public async Task SendAsync(
        IReadOnlyList<NewMessageRecipient> recipients,
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        if (recipients.Count == 0)
        {
            return;
        }

        await hubContext.Clients
            .Groups(recipients.Select(recipient => AccountHubGroup.For(
                recipient.UserId,
                recipient.AccessTokenVersion)))
            .ConversationAccessGranted(conversationId)
            .WaitAsync(cancellationToken);
    }
}
