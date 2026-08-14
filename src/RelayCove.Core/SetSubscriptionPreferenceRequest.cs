namespace RelayCove.Core;

public sealed record SetSubscriptionPreferenceRequest(CredentialEnvelope Credentials, long ChannelId, SubscriptionPreference Preference, bool Value);
