using RelayCove.Client.Auth;

namespace RelayCove.Client.Accounts;

internal interface IClientAccountRuntimeFactory
{
    Task<IClientAccountRuntime> CreateAsync(
        ClientAuthenticationSession authenticationSession,
        CancellationToken cancellationToken = default);
}
