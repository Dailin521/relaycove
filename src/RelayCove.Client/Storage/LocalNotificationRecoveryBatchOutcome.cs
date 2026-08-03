namespace RelayCove.Client.Storage;

internal sealed record LocalNotificationRecoveryBatchOutcome(
    LocalCacheOperationStatus Status,
    IReadOnlyList<long> MessageIds,
    bool HasMore)
{
    public static LocalNotificationRecoveryBatchOutcome Failure(
        LocalCacheOperationStatus status) =>
        new(status, Array.Empty<long>(), HasMore: false);

    public override string ToString() =>
        $"{nameof(LocalNotificationRecoveryBatchOutcome)} {{ Status = {Status}, " +
        "MessageIds = [REDACTED], " +
        $"HasMore = {HasMore} }}";
}
