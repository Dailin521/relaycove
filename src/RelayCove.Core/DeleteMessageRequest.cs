namespace RelayCove.Core;

public sealed record DeleteMessageRequest(CredentialEnvelope Credentials, long MessageId);
