namespace RelayCove.Core;

public sealed record SubscribeChannelRequest(CredentialEnvelope Credentials, ChannelSummary Channel);
