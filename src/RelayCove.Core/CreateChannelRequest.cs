namespace RelayCove.Core;

public sealed record CreateChannelRequest(CredentialEnvelope Credentials, ChannelCreateOptions Options)
{
    public override string ToString() => "CreateChannelRequest { Credentials = [redacted], Options = [redacted] }";
}
