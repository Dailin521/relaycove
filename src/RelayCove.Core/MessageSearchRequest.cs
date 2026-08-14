namespace RelayCove.Core;

public sealed record MessageSearchRequest(
    CredentialEnvelope Credentials,
    string Query,
    long? BeforeMessageId,
    int Limit);
