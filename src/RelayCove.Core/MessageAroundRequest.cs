namespace RelayCove.Core;

public sealed record MessageAroundRequest(
    CredentialEnvelope Credentials,
    ConversationKey Conversation,
    long MessageId,
    int BeforeCount,
    int AfterCount);
