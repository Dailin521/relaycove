namespace RelayCove.Core;

public interface ICredentialVault
{
    Task<CredentialEnvelope?> GetAsync(CancellationToken cancellationToken = default);
    Task SetAsync(CredentialEnvelope credentials, CancellationToken cancellationToken = default);
    Task RemoveAsync(CancellationToken cancellationToken = default);
}
