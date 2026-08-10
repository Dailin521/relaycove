namespace RelayCove.Core;

public sealed class HistoryRequest
{
    public HistoryRequest(
        CredentialEnvelope credentials,
        ConversationKey conversation,
        long? anchorMessageId = null,
        bool includeAnchor = true,
        int limit = 50)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(conversation);
        if (anchorMessageId is <= 0) throw new ArgumentOutOfRangeException(nameof(anchorMessageId));
        if (limit is < 1 or > 50) throw new ArgumentOutOfRangeException(nameof(limit));

        Credentials = credentials;
        Conversation = conversation;
        AnchorMessageId = anchorMessageId;
        IncludeAnchor = includeAnchor;
        Limit = limit;
    }

    public CredentialEnvelope Credentials { get; }
    public ConversationKey Conversation { get; }
    public long? AnchorMessageId { get; }
    public bool IncludeAnchor { get; }
    public int Limit { get; }

    public override string ToString() =>
        $"HistoryRequest {{ Credentials = [redacted], Conversation = [redacted], AnchorMessageId = [redacted], IncludeAnchor = {IncludeAnchor}, Limit = {Limit} }}";
}
