namespace RelayCove.Client.Sync;

public interface IClientAuthenticationSession
{
    ValueTask<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default);

    Task<bool> TryRefreshAccessTokenAsync(
        string rejectedAccessToken,
        CancellationToken cancellationToken = default);
}
