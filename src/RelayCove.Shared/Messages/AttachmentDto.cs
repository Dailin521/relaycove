namespace RelayCove.Shared.Messages;

public sealed record AttachmentDto(
    Guid Id,
    string OriginalFileName,
    string ContentType,
    long Size,
    string DownloadUrl,
    string? ThumbnailUrl)
{
    public override string ToString() =>
        $"{nameof(AttachmentDto)} {{ Id = [REDACTED], OriginalFileName = [REDACTED], " +
        "ContentType = [REDACTED], Size = [REDACTED], DownloadUrl = [REDACTED], " +
        "ThumbnailUrl = [REDACTED] }";
}
