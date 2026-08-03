namespace RelayCove.Client.Notifications;

internal sealed record ClientNotificationPlatformResult(
    ClientNotificationPlatformStatus Status)
{
    public static ClientNotificationPlatformResult Accepted { get; } =
        new(ClientNotificationPlatformStatus.Accepted);

    public static ClientNotificationPlatformResult TransientFailure { get; } =
        new(ClientNotificationPlatformStatus.TransientFailure);

    public static ClientNotificationPlatformResult PermanentlyUnavailable { get; } =
        new(ClientNotificationPlatformStatus.PermanentlyUnavailable);
}
