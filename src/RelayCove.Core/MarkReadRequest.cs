namespace RelayCove.Core;

public sealed class MarkReadRequest
{
    public MarkReadRequest(
        CredentialEnvelope credentials,
        ConversationKey conversation,
        long? anchorMessageId = null,
        int limit = 50)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(conversation);
        if (anchorMessageId is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(anchorMessageId));
        }

        if (limit is < 1 or > 50)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        Credentials = credentials;
        Conversation = conversation;
        AnchorMessageId = anchorMessageId;
        Limit = limit;
    }

    public CredentialEnvelope Credentials { get; }
    public ConversationKey Conversation { get; }
    public long? AnchorMessageId { get; }
    public int Limit { get; }

    public override string ToString() =>
        $"MarkReadRequest {{ Credentials = [redacted], Conversation = [redacted], AnchorMessageId = [redacted], Limit = {Limit} }}";
}
