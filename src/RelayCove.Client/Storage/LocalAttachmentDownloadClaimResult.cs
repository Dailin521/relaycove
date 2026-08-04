namespace RelayCove.Client.Storage;

internal enum LocalAttachmentDownloadClaimResult
{
    Claimed = 1,
    InProgress = 2,
    AlreadyDownloaded = 3,
    AttachmentUnavailable = 4,
}
