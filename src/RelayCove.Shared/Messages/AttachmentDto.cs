namespace RelayCove.Shared.Messages;

public sealed record AttachmentDto(
    Guid Id,
    string OriginalFileName,
    string ContentType,
    long Size,
    string DownloadUrl,
    string? ThumbnailUrl);
