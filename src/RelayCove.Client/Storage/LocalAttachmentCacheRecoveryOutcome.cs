namespace RelayCove.Client.Storage;

internal sealed record LocalAttachmentCacheRecoveryOutcome(
    LocalCacheOperationStatus Status,
    IReadOnlyList<LocalAttachmentDownloadRecord> DownloadedAttachments)
{
    public static LocalAttachmentCacheRecoveryOutcome Failure(
        LocalCacheOperationStatus status) =>
        new(status, Array.Empty<LocalAttachmentDownloadRecord>());

    public override string ToString() =>
        $"{nameof(LocalAttachmentCacheRecoveryOutcome)} {{ Status = {Status}, " +
        "DownloadedAttachments = [REDACTED] }";
}
