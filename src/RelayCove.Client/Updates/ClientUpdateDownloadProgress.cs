using RelayCove.Shared.Updates;

namespace RelayCove.Client.Updates;

internal sealed record ClientUpdateDownloadProgress
{
    public ClientUpdateDownloadProgress(long bytesWritten, long totalBytes)
    {
        if (totalBytes is < 1 or > UpdateConstants.MaximumArtifactBytes ||
            bytesWritten < 0 || bytesWritten > totalBytes)
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
        $"{nameof(ClientUpdateDownloadProgress)} {{ Percent = {Percent} }}";
}
