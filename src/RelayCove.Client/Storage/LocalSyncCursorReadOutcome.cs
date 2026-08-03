namespace RelayCove.Client.Storage;

public sealed record LocalSyncCursorReadOutcome(
    LocalCacheOperationStatus Status,
    long? Cursor)
{
    public override string ToString() =>
        $"{nameof(LocalSyncCursorReadOutcome)} {{ Status = {Status}, Cursor = [REDACTED] }}";
}
