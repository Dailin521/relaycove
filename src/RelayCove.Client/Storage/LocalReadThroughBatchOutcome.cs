namespace RelayCove.Client.Storage;

internal sealed record LocalReadThroughBatchOutcome(
    LocalCacheOperationStatus Status,
    IReadOnlyList<LocalReadThroughUploadTarget> Targets,
    Guid? ContinuationConversationId,
    long SnapshotRevision)
{
    public static LocalReadThroughBatchOutcome Failure(LocalCacheOperationStatus status) =>
        new(status, Array.Empty<LocalReadThroughUploadTarget>(), null, 0);

    public override string ToString() =>
        $"{nameof(LocalReadThroughBatchOutcome)} {{ Status = {Status}, " +
        "Targets = [REDACTED], ContinuationConversationId = [REDACTED], " +
        "SnapshotRevision = [REDACTED] }";
}
