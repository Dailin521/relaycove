namespace RelayCove.App.Services;

public sealed class SelectedAttachmentFile
{
    private readonly Func<CancellationToken, Task<Stream>> _openReadAsync;
    private readonly Func<Stream>? _openPreviewStream;

    public SelectedAttachmentFile(
        string fileName,
        string? contentType,
        long length,
        Func<CancellationToken, Task<Stream>> openReadAsync,
        string? localPath = null,
        Func<Stream>? openPreviewStream = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(openReadAsync);
        FileName = fileName;
        ContentType = contentType;
        Length = length;
        LocalPath = localPath;
        _openPreviewStream = openPreviewStream;
        _openReadAsync = openReadAsync;
    }

    public string FileName { get; }
    public string? ContentType { get; }
    public long Length { get; }
    public string? LocalPath { get; }
    public bool HasPreview => _openPreviewStream is not null;
    public Task<Stream> OpenReadAsync(CancellationToken cancellationToken = default) =>
        _openReadAsync(cancellationToken);
    public Stream OpenPreviewStream() =>
        _openPreviewStream?.Invoke() ?? throw new InvalidOperationException("This attachment has no preview.");

    public override string ToString() =>
        $"SelectedAttachmentFile {{ FileName = [redacted], ContentType = {ContentType ?? "unknown"}, Length = {Length}, LocalPath = [redacted] }}";
}
