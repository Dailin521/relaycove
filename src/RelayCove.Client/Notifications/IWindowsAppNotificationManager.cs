namespace RelayCove.Client.Notifications;

internal interface IWindowsAppNotificationManager
{
    event Action<string>? NotificationInvoked;

    bool IsSupported();

    WindowsClientNotificationSetting Setting { get; }

    void Register();

    string? GetCurrentActivationArgument();

    void Unregister();

    Task ShowAsync(
        WindowsClientNotification notification,
        CancellationToken cancellationToken);

    Task RemoveByGroupAsync(string group, CancellationToken cancellationToken);

    Task RemoveByTagAndGroupAsync(
        string tag,
        string group,
        CancellationToken cancellationToken);
}
