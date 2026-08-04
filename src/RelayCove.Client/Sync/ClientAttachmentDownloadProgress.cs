using RelayCove.Client.Storage;

namespace RelayCove.Client.Sync;

internal sealed record ClientAttachmentDownloadProgress
{
    public ClientAttachmentDownloadProgress(long bytesWritten, long totalBytes)
    {
        if (totalBytes < 1 ||
            totalBytes > ClientAttachmentMetadataPolicy.AbsoluteMaximumAttachmentSize ||
            bytesWritten < 0 ||
            bytesWritten > totalBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(bytesWritten));
        }

        BytesWritten = bytesWritten;
        TotalBytes = totalBytes;
        Percent = (int)((bytesWritten * 100) / totalBytes);
    }

    public long BytesWritten { get; }

    public long TotalBytes { get; }

    public int Percent { get; }

    public override string ToString() =>
        $"{nameof(ClientAttachmentDownloadProgress)} {{ Percent = {Percent} }}";
}
