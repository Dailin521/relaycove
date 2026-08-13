namespace RelayCove.App.Services;

public sealed class SelectedAttachmentFile
{
    private readonly Func<CancellationToken, Task<Stream>> _openReadAsync;

    public SelectedAttachmentFile(
        string fileName,
        string? contentType,
        long length,
        Func<CancellationToken, Task<Stream>> openReadAsync,
        string? localPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(openReadAsync);
        FileName = fileName;
        ContentType = contentType;
        Length = length;
        LocalPath = localPath;
        _openReadAsync = openReadAsync;
    }

    public string FileName { get; }
    public string? ContentType { get; }
    public long Length { get; }
    public string? LocalPath { get; }
    public Task<Stream> OpenReadAsync(CancellationToken cancellationToken = default) =>
        _openReadAsync(cancellationToken);

    public override string ToString() =>
        $"SelectedAttachmentFile {{ FileName = [redacted], ContentType = {ContentType ?? "unknown"}, Length = {Length}, LocalPath = [redacted] }}";
}
