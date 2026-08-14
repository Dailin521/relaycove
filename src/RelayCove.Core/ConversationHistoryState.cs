namespace RelayCove.Core;

public sealed record ConversationHistoryState(
    ConversationKey? Conversation,
    long Generation,
    bool IsLoading,
    bool FoundOldest,
    bool HasOlderInCache,
    long? OldestLoadedMessageId,
    string? Error)
{
    public static ConversationHistoryState Empty { get; } = new(null, 0, false, false, false, null, null);
}
