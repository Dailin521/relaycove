using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace RelayCove.Client.Notifications;

internal sealed class WindowsAppSdkNotificationManager : IWindowsAppNotificationManager
{
    private readonly Lazy<AppNotificationManager> manager;

    private WindowsAppSdkNotificationManager()
    {
        manager = new Lazy<AppNotificationManager>(
            CreateManager,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public static WindowsAppSdkNotificationManager Shared { get; } = new();

    public event Action<string>? NotificationInvoked;

    public WindowsClientNotificationSetting Setting =>
        manager.Value.Setting == AppNotificationSetting.Enabled
            ? WindowsClientNotificationSetting.Enabled
            : WindowsClientNotificationSetting.Disabled;

    public bool IsSupported() => AppNotificationManager.IsSupported();

    public void Register() => manager.Value.Register();

    public string? GetCurrentActivationArgument()
    {
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        return activation.Kind == ExtendedActivationKind.AppNotification &&
            activation.Data is AppNotificationActivatedEventArgs notificationActivation
                ? notificationActivation.Argument
                : null;
    }

    public void Unregister() => manager.Value.Unregister();

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
                manager.Value.Show(appNotification);
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    public Task RemoveByGroupAsync(string group, CancellationToken cancellationToken) =>
        manager.Value.RemoveByGroupAsync(group).AsTask(cancellationToken);

    public Task RemoveByTagAndGroupAsync(
        string tag,
        string group,
        CancellationToken cancellationToken) =>
        manager.Value.RemoveByTagAndGroupAsync(tag, group).AsTask(cancellationToken);

    private AppNotificationManager CreateManager()
    {
        var nativeManager = AppNotificationManager.Default;
        nativeManager.NotificationInvoked += OnNotificationInvoked;
        return nativeManager;
    }

    private void OnNotificationInvoked(
        AppNotificationManager sender,
        AppNotificationActivatedEventArgs args) =>
        NotificationInvoked?.Invoke(args.Argument);
}
