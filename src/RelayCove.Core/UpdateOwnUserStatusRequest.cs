namespace RelayCove.Core;

public sealed record UpdateOwnUserStatusRequest(
    CredentialEnvelope Credentials,
    UserStatusContent Status);
