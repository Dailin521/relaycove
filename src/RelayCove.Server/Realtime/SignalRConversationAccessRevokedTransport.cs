using Microsoft.AspNetCore.SignalR;
using RelayCove.Server.Hubs;

namespace RelayCove.Server.Realtime;

internal sealed class SignalRConversationAccessRevokedTransport(
    IHubContext<ChatHub, IChatClient> hubContext) : IConversationAccessRevokedTransport
{
    public async Task SendAsync(
        string recipientUserId,
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        await hubContext.Clients
            .User(recipientUserId)
            .ConversationAccessRevoked(conversationId)
            .WaitAsync(cancellationToken);
    }
}
