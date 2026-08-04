namespace RelayCove.Server.Options;

public sealed class UploadOptions
{
    public const string SectionName = "Uploads";
    public const long DefaultMaximumFileBytes = 25L * 1024 * 1024;
    public const long AbsoluteMaximumFileBytes = 100L * 1024 * 1024;
    public const long MultipartOverheadBytes = 64L * 1024;
    public const int DefaultUnboundRetentionHours = 24;
    public const int MaximumUnboundRetentionHours = 168;

    public long MaximumFileBytes { get; init; } = DefaultMaximumFileBytes;

    public int PermitLimit { get; init; } = 10;

    public int RateLimitWindowSeconds { get; init; } = 60;

    public int UnboundRetentionHours { get; init; } = DefaultUnboundRetentionHours;
}
