namespace RelayCove.Core;

public sealed record ChannelSettingsSnapshotRequest(CredentialEnvelope Credentials, ChannelSettingsLimits Limits);
