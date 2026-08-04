using System.IO;
using System.Windows.Media.Imaging;
using RelayCove.Client.Attachments;

namespace RelayCove.Client.Sync;

internal enum ClientAttachmentImageLoadStatus
{
    Ready = 1,
    InProgress = 2,
    NotDownloaded = 3,
    AttachmentUnavailable = 4,
    AccessRevoked = 5,
    Stale = 6,
    ValidationFailed = 7,
    UnsupportedFormat = 8,
    SourceTooLarge = 9,
    OutputTooLarge = 10,
    DecodeFailed = 11,
    TimedOut = 12,
    Canceled = 13,
    TransientFailure = 14,
    LocalCacheFailure = 15,
}

internal delegate ClientAttachmentImageLoadStatus ClientAttachmentImageCommit();

internal delegate Task<ClientAttachmentImageDecodeResult> ClientAttachmentImageDecodeAsync(
    Stream stream,
    ClientAttachmentImageRendition rendition,
    CancellationToken cancellationToken);

internal sealed record ClientAttachmentImageLoadOutcome(
    ClientAttachmentImageLoadStatus Status,
    BitmapSource? Image,
    bool WasDownsampled,
    ClientAttachmentImageSafeSize? SafeSize)
{
    internal static ClientAttachmentImageLoadOutcome Ready(
        ClientAttachmentImageDecodeResult decoded)
    {
        ArgumentNullException.ThrowIfNull(decoded);
        if (decoded.Status != ClientAttachmentImageDecodeStatus.Success ||
            decoded.Image is null ||
            !decoded.Image.IsFrozen ||
            decoded.SafeSize is null)
        {
            throw new ArgumentException(
                "A successful frozen decode result is required.",
                nameof(decoded));
        }

        return new ClientAttachmentImageLoadOutcome(
            ClientAttachmentImageLoadStatus.Ready,
            decoded.Image,
            decoded.WasDownsampled,
            decoded.SafeSize);
    }

    internal static ClientAttachmentImageLoadOutcome Failure(
        ClientAttachmentImageLoadStatus status)
    {
        if (status == ClientAttachmentImageLoadStatus.Ready)
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        return new ClientAttachmentImageLoadOutcome(
            status,
            Image: null,
            WasDownsampled: false,
            SafeSize: null);
    }

    public override string ToString() =>
        $"{nameof(ClientAttachmentImageLoadOutcome)} {{ Status = {Status}, " +
        "Image = [REDACTED], WasDownsampled = [REDACTED], SafeSize = [REDACTED] }}";
}
