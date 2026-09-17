namespace RelayCove.App.Services;

public sealed record DownloadHistoryEntry(
    Guid Id,
    string FileName,
    string FilePath,
    long Length,
    DateTimeOffset CompletedAt,
    string? AttachmentKey = null)
{
    public override string ToString() =>
        $"DownloadHistoryEntry {{ Id = {Id}, FileName = [redacted], FilePath = [redacted], Length = {Length}, CompletedAt = {CompletedAt:O} }}";
}
