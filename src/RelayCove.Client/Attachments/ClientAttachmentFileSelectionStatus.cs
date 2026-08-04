namespace RelayCove.Client.Attachments;

internal enum ClientAttachmentFileSelectionStatus
{
    Success,
    NoFilesSelected,
    TooManyFiles,
    DuplicateFile,
    InvalidPath,
    FileNotFound,
    FileUnavailable,
    InvalidFileName,
    EmptyFile,
    FileTooLarge,
    Canceled,
}
