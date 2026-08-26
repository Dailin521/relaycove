namespace RelayCove.Core;

public sealed record SetPresenceEnabledRequest(
    CredentialEnvelope Credentials,
    bool IsEnabled);
