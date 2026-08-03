namespace RelayCove.Client.Storage;

internal sealed record LocalNotificationCandidateBatchOutcome(
    LocalCacheOperationStatus Status,
    IReadOnlyList<LocalNotificationCandidate> Candidates,
    int HandledWithoutPlatformCount)
{
    public static LocalNotificationCandidateBatchOutcome Failure(
        LocalCacheOperationStatus status) =>
        new(status, Array.Empty<LocalNotificationCandidate>(), 0);

    public override string ToString() =>
        $"{nameof(LocalNotificationCandidateBatchOutcome)} {{ Status = {Status}, " +
        "Candidates = [REDACTED], " +
        $"HandledWithoutPlatformCount = {HandledWithoutPlatformCount} }}";
}
