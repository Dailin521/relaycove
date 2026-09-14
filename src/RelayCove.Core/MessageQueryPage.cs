namespace RelayCove.Core;

/// <summary>
/// A transient, server-authoritative page from a message narrow.
/// Search highlighting fields are intentionally not part of this domain type.
/// </summary>
public sealed record MessageQueryPage(
    IReadOnlyList<ChatMessage> Messages,
    bool FoundOldest,
    bool FoundNewest,
    bool FoundAnchor)
{
    // Preserve the server cursor when ClientSession filters unsupported conversations.
    public long? OldestFetchedMessageId { get; } = Messages.MinBy(message => message.Id)?.Id;
}
