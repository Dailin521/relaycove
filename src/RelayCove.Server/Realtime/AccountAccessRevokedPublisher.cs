using RelayCove.Shared.Realtime;

namespace RelayCove.Server.Realtime;

internal sealed class AccountAccessRevokedPublisher(
    IAccountAccessRevokedTransport transport,
    ILogger<AccountAccessRevokedPublisher> logger)
{
    public async Task TryPublishAsync(Guid targetUserId, long minimumAccessTokenVersion)
    {
        try
        {
            await transport.SendAsync(
                targetUserId.ToString("D"),
                new AccountAccessRevokedEvent(minimumAccessTokenVersion),
                CancellationToken.None);
            logger.LogInformation(
                "Published realtime account access revocation for target {TargetUserId}; minimumAccessTokenVersion={MinimumAccessTokenVersion}.",
                targetUserId,
                minimumAccessTokenVersion);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Realtime account access revocation failed for target {TargetUserId}.", targetUserId);
        }
    }
}
