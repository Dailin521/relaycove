namespace RelayCove.Client.Storage;

internal enum LocalDownloadedAttachmentReadResult
{
    Downloaded = 1,
    NotDownloaded = 2,
    AttachmentUnavailable = 3,
}

internal sealed record LocalDownloadedAttachmentReadOutcome(
    LocalCacheOperationStatus Status,
    LocalDownloadedAttachmentReadResult? Result,
    LocalAttachmentDownloadRecord? Record)
{
    internal static LocalDownloadedAttachmentReadOutcome Failure(
        LocalCacheOperationStatus status) =>
        new(status, Result: null, Record: null);

    public override string ToString() =>
        $"{nameof(LocalDownloadedAttachmentReadOutcome)} {{ Status = {Status}, " +
        $"Result = {Result}, Record = [REDACTED] }}";
}
