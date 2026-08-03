namespace RelayCove.Client.Storage;

internal sealed record LocalConversationListReadOutcome(
    LocalCacheOperationStatus Status,
    IReadOnlyList<LocalConversationListItem> Conversations,
    int TotalUnreadCount,
    long Revision)
{
    public static LocalConversationListReadOutcome Failure(
        LocalCacheOperationStatus status,
        long revision) =>
        new(status, Array.Empty<LocalConversationListItem>(), 0, revision);

    public override string ToString() =>
        $"{nameof(LocalConversationListReadOutcome)} {{ Status = {Status}, " +
        $"ConversationCount = {Conversations.Count}, " +
        $"TotalUnreadCount = {TotalUnreadCount}, Revision = {Revision} }}";
}
