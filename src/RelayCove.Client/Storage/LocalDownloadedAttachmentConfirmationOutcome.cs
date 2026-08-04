namespace RelayCove.Client.Storage;

internal enum LocalDownloadedAttachmentConfirmationResult
{
    Confirmed = 1,
    NotDownloaded = 2,
    AttachmentUnavailable = 3,
    Changed = 4,
}

internal sealed record LocalDownloadedAttachmentConfirmationOutcome(
    LocalCacheOperationStatus Status,
    LocalDownloadedAttachmentConfirmationResult? Result)
{
    internal static LocalDownloadedAttachmentConfirmationOutcome Failure(
        LocalCacheOperationStatus status) =>
        new(status, Result: null);

    public override string ToString() =>
        $"{nameof(LocalDownloadedAttachmentConfirmationOutcome)} {{ Status = {Status}, " +
        $"Result = {Result} }}";
}
