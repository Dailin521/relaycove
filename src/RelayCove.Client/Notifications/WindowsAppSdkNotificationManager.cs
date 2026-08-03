using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace RelayCove.Client.Notifications;

internal sealed class WindowsAppSdkNotificationManager : IWindowsAppNotificationManager
{
    private readonly AppNotificationManager manager = AppNotificationManager.Default;

    private WindowsAppSdkNotificationManager()
    {
        manager.NotificationInvoked += OnNotificationInvoked;
    }

    public static WindowsAppSdkNotificationManager Shared { get; } = new();

    public event Action<string>? NotificationInvoked;

    public WindowsClientNotificationSetting Setting =>
        manager.Setting == AppNotificationSetting.Enabled
            ? WindowsClientNotificationSetting.Enabled
            : WindowsClientNotificationSetting.Disabled;

    public bool IsSupported() => AppNotificationManager.IsSupported();

    public void Register() => manager.Register();

    public string? GetCurrentActivationArgument()
    {
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        return activation.Kind == ExtendedActivationKind.AppNotification &&
            activation.Data is AppNotificationActivatedEventArgs notificationActivation
                ? notificationActivation.Argument
                : null;
    }

    public void Unregister() => manager.Unregister();

    public Task ShowAsync(
        WindowsClientNotification notification,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);
        return Task.Factory.StartNew(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var builder = new AppNotificationBuilder();
                foreach (var argument in notification.ActivationArguments)
                {
                    builder.AddArgument(argument.Key, argument.Value);
                }

                var appNotification = builder
                    .AddText(notification.Title)
                    .AddText(notification.Body)
                    .BuildNotification();
                appNotification.Tag = notification.Tag;
                appNotification.Group = notification.Group;
                appNotification.Expiration = notification.Expiration;
                appNotification.ExpiresOnReboot = notification.ExpiresOnReboot;
                cancellationToken.ThrowIfCancellationRequested();
                manager.Show(appNotification);
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    public Task RemoveByGroupAsync(string group, CancellationToken cancellationToken) =>
        manager.RemoveByGroupAsync(group).AsTask(cancellationToken);

    public Task RemoveByTagAndGroupAsync(
        string tag,
        string group,
        CancellationToken cancellationToken) =>
        manager.RemoveByTagAndGroupAsync(tag, group).AsTask(cancellationToken);

    private void OnNotificationInvoked(
        AppNotificationManager sender,
        AppNotificationActivatedEventArgs args) =>
        NotificationInvoked?.Invoke(args.Argument);
}
