namespace RelayCove.Client.Storage;

internal sealed record LocalAuthoritativeConversationSnapshotOutcome(
    LocalCacheOperationStatus Status,
    IReadOnlyList<Guid> RevokedConversationIds)
{
    public static LocalAuthoritativeConversationSnapshotOutcome Failure(
        LocalCacheOperationStatus status) =>
        new(status, Array.Empty<Guid>());

    public override string ToString() =>
        $"{nameof(LocalAuthoritativeConversationSnapshotOutcome)} {{ Status = {Status}, " +
        "RevokedConversationIds = [REDACTED] }";
}
