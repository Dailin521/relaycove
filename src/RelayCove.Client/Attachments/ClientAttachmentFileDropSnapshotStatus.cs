namespace RelayCove.Client.Attachments;

internal enum ClientAttachmentFileDropSnapshotStatus
{
    Success,
    FileDropFormatNotPresent,
    InvalidFileDropData,
    NoFilesSelected,
    TooManyFiles,
}
