namespace RelayCove.Client.Notifications;

internal sealed record ClientNotificationSettingsSnapshot(
    ClientNotificationPlatformAvailability PlatformAvailability,
    bool IsDoNotDisturbEnabled)
{
    public static ClientNotificationSettingsSnapshot Enabled { get; } =
        new(ClientNotificationPlatformAvailability.Available, IsDoNotDisturbEnabled: false);

    public static ClientNotificationSettingsSnapshot Unavailable { get; } =
        new(ClientNotificationPlatformAvailability.Unavailable, IsDoNotDisturbEnabled: false);
}
