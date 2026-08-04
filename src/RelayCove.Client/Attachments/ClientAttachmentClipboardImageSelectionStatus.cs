namespace RelayCove.Client.Attachments;

internal enum ClientAttachmentClipboardImageSelectionStatus
{
    Success,
    NoImage,
    InvalidImage,
    TooManyFiles,
    AggregateMemoryTooLarge,
    RawPixelsTooLarge,
    OutputTooLarge,
    Canceled,
    EncodingFailed,
}
