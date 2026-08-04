using Microsoft.AspNetCore.SignalR;
using RelayCove.Server.Hubs;
using RelayCove.Shared.Realtime;

namespace RelayCove.Server.Realtime;

internal sealed class SignalRAccountAccessRevokedTransport(
    IHubContext<ChatHub, IChatClient> hubContext) : IAccountAccessRevokedTransport
{
    public async Task SendAsync(
        string recipientUserId,
        AccountAccessRevokedEvent accountAccessRevoked,
        CancellationToken cancellationToken)
    {
        await hubContext.Clients
            .User(recipientUserId)
            .AccountAccessRevoked(accountAccessRevoked)
            .WaitAsync(cancellationToken);
    }
}
