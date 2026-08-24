namespace RelayCove.App.Services;

public sealed record NotificationPreferences(
    bool SystemNotificationsEnabled = true,
    bool TaskbarFlashEnabled = true,
    bool TaskbarBadgeEnabled = true,
    bool ShowMessagePreview = true,
    bool DoNotDisturb = false);
