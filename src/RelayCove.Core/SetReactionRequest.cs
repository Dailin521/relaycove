namespace RelayCove.Core;

public sealed record SetReactionRequest(
    CredentialEnvelope Credentials,
    long MessageId,
    EmojiReactionIdentity Reaction,
    bool Add);
