namespace RelayCove.Core;

public sealed record UnsubscribeChannelRequest(
    CredentialEnvelope Credentials,
    string ChannelName);
