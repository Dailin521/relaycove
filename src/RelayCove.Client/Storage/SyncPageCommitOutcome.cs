using RelayCove.Shared.Messages;

namespace RelayCove.Client.Storage;

public sealed record SyncPageCommitOutcome(
    LocalCacheOperationStatus Status,
    IReadOnlyList<IncomingMessageMergeResult> MergeResults,
    long? CommittedCursor)
{
    public override string ToString() =>
        $"{nameof(SyncPageCommitOutcome)} {{ Status = {Status}, " +
        "MergeResults = [REDACTED], CommittedCursor = [REDACTED] }";
}
