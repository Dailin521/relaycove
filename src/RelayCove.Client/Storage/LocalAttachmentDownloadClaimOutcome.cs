namespace RelayCove.Client.Storage;

internal sealed record LocalAttachmentDownloadClaimOutcome(
    LocalCacheOperationStatus Status,
    LocalAttachmentDownloadClaimResult? Result,
    LocalAttachmentDownloadRecord? Record)
{
    public static LocalAttachmentDownloadClaimOutcome Failure(
        LocalCacheOperationStatus status) =>
        new(status, Result: null, Record: null);

    public override string ToString() =>
        $"{nameof(LocalAttachmentDownloadClaimOutcome)} {{ Status = {Status}, " +
        $"Result = {Result}, Record = [REDACTED] }}";
}
