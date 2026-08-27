namespace RelayCove.App.Services;

public sealed record DownloadHistoryEntry(
    Guid Id,
    string FileName,
    string FilePath,
    long Length,
    DateTimeOffset CompletedAt)
{
    public override string ToString() =>
        $"DownloadHistoryEntry {{ Id = {Id}, FileName = [redacted], FilePath = [redacted], Length = {Length}, CompletedAt = {CompletedAt:O} }}";
}
