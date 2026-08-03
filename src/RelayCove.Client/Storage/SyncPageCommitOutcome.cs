using RelayCove.Shared.Messages;

namespace RelayCove.Client.Storage;

public sealed record SyncPageCommitOutcome(
    LocalCacheOperationStatus Status,
    IReadOnlyList<IncomingMessageMergeResult> MergeResults,
    long? CommittedCursor,
    IReadOnlyList<long> NotificationCandidateMessageIds)
{
    public override string ToString() =>
        $"{nameof(SyncPageCommitOutcome)} {{ Status = {Status}, " +
        "MergeResults = [REDACTED], CommittedCursor = [REDACTED], " +
        "NotificationCandidateMessageIds = [REDACTED] }";
}
