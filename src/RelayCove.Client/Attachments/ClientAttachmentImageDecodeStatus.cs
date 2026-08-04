namespace RelayCove.Client.Attachments;

internal enum ClientAttachmentImageDecodeStatus
{
    Success = 1,
    InvalidInput = 2,
    UnsupportedFormat = 3,
    UnsupportedCodec = 4,
    SourceTooLarge = 5,
    OutputTooLarge = 6,
    DecodeFailed = 7,
}
