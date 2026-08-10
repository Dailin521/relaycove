namespace RelayCove.Core;

public sealed record AuthenticationResult(CredentialEnvelope Credentials, UserProfile User);
