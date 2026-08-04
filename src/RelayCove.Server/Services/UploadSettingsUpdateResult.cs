namespace RelayCove.Server.Services;

public sealed record UploadSettingsUpdateResult(
    UploadSettingsUpdateStatus Status,
    long EffectiveMaximumFileBytes = 0);
