namespace RelayCove.Client.Attachments;

internal enum ClientAttachmentDownloadPhase
{
    Idle = 0,
    Downloading = 1,
    Canceling = 2,
    Downloaded = 3,
    Failed = 4,
}
