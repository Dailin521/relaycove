namespace RelayCove.Client.Storage;

internal enum LocalAttachmentDownloadState
{
    NotDownloaded = 0,
    Downloading = 1,
    Downloaded = 2,
    Failed = 3,
}
