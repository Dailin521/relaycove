namespace RelayCove.Client.Auth;

internal sealed class ClientCredentialReadOutcome
{
    private ClientCredentialReadOutcome(
        ClientCredentialReadStatus status,
        StoredClientCredential? credential)
    {
        Status = status;
        Credential = credential;
    }

    public ClientCredentialReadStatus Status { get; }

    public StoredClientCredential? Credential { get; }

    public override string ToString() =>
        $"{nameof(ClientCredentialReadOutcome)} {{ Status = {Status}, " +
        "Credential = [REDACTED] }";

    internal static ClientCredentialReadOutcome Loaded(StoredClientCredential credential) =>
        new(ClientCredentialReadStatus.Loaded, credential);

    internal static ClientCredentialReadOutcome Failure(ClientCredentialReadStatus status) =>
        new(status, credential: null);
}
