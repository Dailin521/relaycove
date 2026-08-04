namespace RelayCove.Client.Attachments;

internal readonly record struct ClientAttachmentImageSafeSize(int PixelWidth, int PixelHeight)
{
    public long PixelCount => checked((long)PixelWidth * PixelHeight);
}
