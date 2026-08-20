namespace RelayCove.Core;

public sealed record ChannelMembersRequest(CredentialEnvelope Credentials, long ChannelId);
