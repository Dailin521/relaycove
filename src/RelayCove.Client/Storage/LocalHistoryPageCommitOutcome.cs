using RelayCove.Shared.Messages;

namespace RelayCove.Client.Storage;

public sealed record LocalHistoryPageCommitOutcome(
    LocalCacheOperationStatus Status,
    IReadOnlyList<IncomingMessageMergeResult> MergeResults)
{
    public static LocalHistoryPageCommitOutcome Failure(
        LocalCacheOperationStatus status) =>
        new(status, Array.Empty<IncomingMessageMergeResult>());

    public override string ToString() =>
        $"{nameof(LocalHistoryPageCommitOutcome)} {{ Status = {Status}, " +
        "MergeResults = [REDACTED] }}";
}
