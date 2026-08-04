using System.Windows.Media.Imaging;

namespace RelayCove.Client.Attachments;

internal sealed record ClientAttachmentImageDecodeResult(
    ClientAttachmentImageDecodeStatus Status,
    BitmapSource? Image,
    bool WasDownsampled,
    ClientAttachmentImageSafeSize? SafeSize)
{
    public static ClientAttachmentImageDecodeResult Success(
        BitmapSource image,
        bool wasDownsampled,
        ClientAttachmentImageSafeSize safeSize) =>
        new(ClientAttachmentImageDecodeStatus.Success, image, wasDownsampled, safeSize);

    public static ClientAttachmentImageDecodeResult Failure(
        ClientAttachmentImageDecodeStatus status) =>
        new(status, Image: null, WasDownsampled: false, SafeSize: null);

    public override string ToString() =>
        $"{nameof(ClientAttachmentImageDecodeResult)} {{ Status = {Status}, " +
        "Image = [REDACTED], WasDownsampled = [REDACTED], SafeSize = [REDACTED] }";
}
