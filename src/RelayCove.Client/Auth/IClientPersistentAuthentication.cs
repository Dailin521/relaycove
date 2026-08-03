using RelayCove.Shared.Auth;

namespace RelayCove.Client.Auth;

internal interface IClientPersistentAuthentication
{
    Task<PersistentClientAuthenticationOutcome> LoginAsync(
        Uri serverBaseUri,
        LoginRequest request,
        CancellationToken cancellationToken = default);

    Task<PersistentClientAuthenticationOutcome> RestoreAsync(
        CancellationToken cancellationToken = default);
}
