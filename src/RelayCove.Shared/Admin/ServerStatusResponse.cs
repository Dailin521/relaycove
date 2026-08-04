namespace RelayCove.Shared.Admin;

public sealed record ServerStatusResponse(
    string Version,
    DateTimeOffset StartedAt,
    long UptimeSeconds,
    int OnlineConnectionCount,
    long DatabaseBytes,
    long AttachmentBytes,
    long EffectiveUploadLimitBytes,
    string? LastErrorCategory,
    DateTimeOffset? LastErrorAt);
