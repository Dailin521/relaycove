namespace RelayCove.Core;

public sealed record UpdateOwnNameRequest(CredentialEnvelope Credentials, string FullName);
