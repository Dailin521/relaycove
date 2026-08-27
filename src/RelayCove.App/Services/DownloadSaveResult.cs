namespace RelayCove.App.Services;

public sealed record DownloadSaveResult(bool Saved, string? FilePath)
{
    public static DownloadSaveResult Cancelled { get; } = new(false, null);
}
