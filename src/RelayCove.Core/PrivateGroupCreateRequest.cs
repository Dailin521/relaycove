namespace RelayCove.Core;

public sealed record PrivateGroupCreateRequest(
    CredentialEnvelope Credentials,
    PrivateGroupCreateOptions Options)
{
    public override string ToString() => "PrivateGroupCreateRequest { Credentials = [redacted], Options = [redacted] }";
}
