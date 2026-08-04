namespace RelayCove.Client.Attachments;

internal static class ClientAttachmentImageDecodePolicy
{
    internal const long MaximumInputBytes = 25L * 1024 * 1024;
    internal const int MaximumSourceEdge = 16_384;
    internal const long MaximumSourcePixels = 16_777_216;
    internal const int MaximumPngPixelsPerInputByte = 256;
    internal const int ThumbnailMaximumEdge = 320;
    internal const int ViewerMaximumEdge = 2_560;
    internal const long MaximumOutputPixels = 6_553_600;
    internal const long MaximumOutputBytes = 25L * 1024 * 1024;

    internal static bool IsSourceWithinBudget(uint width, uint height)
    {
        if (width is 0 or > MaximumSourceEdge || height is 0 or > MaximumSourceEdge)
        {
            return false;
        }

        return checked((long)width * height) <= MaximumSourcePixels;
    }

    internal static bool IsPngCompressionWithinBudget(
        long sourcePixels,
        long inputBytes) =>
        sourcePixels > 0 &&
        inputBytes > 0 &&
        sourcePixels <= checked(inputBytes * MaximumPngPixelsPerInputByte);

    internal static ClientAttachmentImageSafeSize GetTargetSize(
        uint sourceWidth,
        uint sourceHeight,
        ClientAttachmentImageRendition rendition)
    {
        var maximumEdge = rendition switch
        {
            ClientAttachmentImageRendition.Thumbnail => ThumbnailMaximumEdge,
            ClientAttachmentImageRendition.Viewer => ViewerMaximumEdge,
            _ => throw new ArgumentOutOfRangeException(nameof(rendition)),
        };

        var longestEdge = Math.Max(sourceWidth, sourceHeight);
        var targetWidth = sourceWidth;
        var targetHeight = sourceHeight;
        if (longestEdge > maximumEdge)
        {
            targetWidth = Math.Max(1, checked(sourceWidth * (uint)maximumEdge / longestEdge));
            targetHeight = Math.Max(1, checked(sourceHeight * (uint)maximumEdge / longestEdge));
        }

        return new ClientAttachmentImageSafeSize(
            checked((int)targetWidth),
            checked((int)targetHeight));
    }

    internal static bool IsOutputWithinBudget(ClientAttachmentImageSafeSize size)
    {
        if (size.PixelWidth <= 0 || size.PixelHeight <= 0)
        {
            return false;
        }

        var pixelCount = size.PixelCount;
        return pixelCount <= MaximumOutputPixels && checked(pixelCount * 4) <= MaximumOutputBytes;
    }
}
