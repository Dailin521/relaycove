namespace RelayCove.Core;

public sealed record SetChannelPersonalSettingRequest(CredentialEnvelope Credentials, long ChannelId, ChannelPersonalSettingChange Change)
{
    public override string ToString() => "SetChannelPersonalSettingRequest { Credentials = [redacted], Change = [redacted] }";
}
