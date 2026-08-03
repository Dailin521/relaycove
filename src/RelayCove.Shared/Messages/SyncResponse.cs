namespace RelayCove.Shared.Messages;

public sealed record SyncResponse(
    IReadOnlyList<MessageDto> Messages,
    long NextCursor,
    long SnapshotUpperBound,
    bool HasMore)
{
    public override string ToString() =>
        $"{nameof(SyncResponse)} {{ Messages = [REDACTED], NextCursor = [REDACTED], " +
        $"SnapshotUpperBound = [REDACTED], HasMore = {HasMore} }}";
}
