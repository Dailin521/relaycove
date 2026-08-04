using RelayCove.Shared.Realtime;

namespace RelayCove.Server.Realtime;

internal interface IAccountAccessRevokedTransport
{
    Task SendAsync(
        string recipientUserId,
        AccountAccessRevokedEvent accountAccessRevoked,
        CancellationToken cancellationToken);
}
