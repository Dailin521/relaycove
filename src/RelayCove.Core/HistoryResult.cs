namespace RelayCove.Core;

public sealed record HistoryResult(
    IReadOnlyList<ChatMessage> Messages,
    bool FoundOldest,
    bool FoundNewest,
    bool FoundAnchor = true);
