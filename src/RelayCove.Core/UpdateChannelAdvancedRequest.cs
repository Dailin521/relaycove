namespace RelayCove.Core;

public sealed record UpdateChannelAdvancedRequest(CredentialEnvelope Credentials, long ChannelId, ChannelAdvancedSettingsChange Change)
{
    public override string ToString() => "UpdateChannelAdvancedRequest { Credentials = [redacted], Change = [redacted] }";
}
