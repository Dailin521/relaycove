namespace RelayCove.Core;

public sealed record UpdateOwnPresenceRequest(
    CredentialEnvelope Credentials,
    UserPresenceStatus Status);
