using System.IO;
using RelayCove.Client.Storage;

namespace RelayCove.Client.Sync;

/// <summary>
/// Describes one locally reopenable attachment upload backed by a caller-owned source.
/// </summary>
public sealed class ClientAttachmentUploadSource
{
    internal const long MinimumSizeBytes = 1;
    internal const long MaximumSizeBytes = ClientAttachmentMetadataPolicy.AbsoluteMaximumAttachmentSize;
    private readonly Func<CancellationToken, ValueTask<Stream>> openReadAsync;

    public ClientAttachmentUploadSource(
        string originalFileName,
        string contentType,
        long size,
        Func<CancellationToken, ValueTask<Stream>> openReadAsync)
    {
        if (!ClientAttachmentMetadataPolicy.IsValidOriginalFileName(originalFileName))
        {
            throw new ArgumentException("The attachment file name is invalid.", nameof(originalFileName));
        }

        if (!ClientAttachmentMetadataPolicy.TryCanonicalizeContentType(
                contentType,
                out var canonicalContentType) ||
            !string.Equals(contentType, canonicalContentType, StringComparison.Ordinal))
        {
            throw new ArgumentException("The attachment content type is invalid.", nameof(contentType));
        }

        if (size is < MinimumSizeBytes or > MaximumSizeBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(size));
        }

        this.openReadAsync = openReadAsync ?? throw new ArgumentNullException(nameof(openReadAsync));
        OriginalFileName = originalFileName;
        ContentType = canonicalContentType;
        Size = size;
    }

    public string OriginalFileName { get; }

    public string ContentType { get; }

    public long Size { get; }

    public override string ToString() =>
        $"{nameof(ClientAttachmentUploadSource)} {{ OriginalFileName = [REDACTED], " +
        "ContentType = [REDACTED], Size = [REDACTED], OpenReadAsync = [REDACTED] }";

    internal ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken) =>
        openReadAsync(cancellationToken);
}
