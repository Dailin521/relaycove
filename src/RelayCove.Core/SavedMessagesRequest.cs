namespace RelayCove.Core;

public sealed record SavedMessagesRequest(
    CredentialEnvelope Credentials,
    long? BeforeMessageId,
    int Limit);
