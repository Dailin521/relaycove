namespace RelayCove.Client.Attachments;

internal enum ClientClipboardImageReadStatus
{
    Success,
    NoImage,
    TextPreferred,
    RepeatedImagePaste,
    ClipboardUnavailable,
    InvalidImage,
}
