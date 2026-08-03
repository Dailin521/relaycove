using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace RelayCove.Client.Notifications;

internal sealed class WindowsAppSdkNotificationManager : IWindowsAppNotificationManager
{
    internal const string ColdActivationCommandLineMarker =
        "----AppNotificationActivated:";
    private readonly Lazy<AppNotificationManager> manager;
    private int registered;

    private WindowsAppSdkNotificationManager()
    {
        manager = new Lazy<AppNotificationManager>(
            CreateManager,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public static WindowsAppSdkNotificationManager Shared { get; } = new();

    internal static bool RequiresRegistrationBeforeActivationRead(
        IReadOnlyList<string>? commandLineArguments = null) =>
        (commandLineArguments ?? Environment.GetCommandLineArgs())
            .Skip(1)
            .Contains(ColdActivationCommandLineMarker, StringComparer.Ordinal);

    public event Action<string>? NotificationInvoked;

    public bool IsRegistered => Volatile.Read(ref registered) != 0;

    public void SetRegistrationReady(bool isReady) =>
        Volatile.Write(ref registered, isReady ? 1 : 0);

    public WindowsClientNotificationSetting Setting =>
        manager.Value.Setting == AppNotificationSetting.Enabled
            ? WindowsClientNotificationSetting.Enabled
            : WindowsClientNotificationSetting.Disabled;

    public bool IsSupported() => AppNotificationManager.IsSupported();

    public void Register() => manager.Value.Register();

    public void Unregister()
    {
        Volatile.Write(ref registered, 0);
        manager.Value.Unregister();
    }

    public Task ShowAsync(
        WindowsClientNotification notification,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);
        return Task.Factory.StartNew(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var appNotification = BuildNotification(notification);
                cancellationToken.ThrowIfCancellationRequested();
                manager.Value.Show(appNotification);
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    public Task RemoveByGroupAsync(string group, CancellationToken cancellationToken) =>
        Task.Factory.StartNew(
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return manager.Value
                        .RemoveByGroupAsync(group)
                        .AsTask(cancellationToken);
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default)
            .Unwrap();

    public Task RemoveByTagAndGroupAsync(
        string tag,
        string group,
        CancellationToken cancellationToken) =>
        Task.Factory.StartNew(
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return manager.Value
                        .RemoveByTagAndGroupAsync(tag, group)
                        .AsTask(cancellationToken);
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default)
            .Unwrap();

    internal static AppNotification BuildNotification(
        WindowsClientNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        var builder = new AppNotificationBuilder();
        foreach (var argument in notification.ActivationArguments)
        {
            builder.AddArgument(argument.Key, argument.Value);
        }

        var appNotification = builder
            .AddText(notification.Title)
            .AddText(notification.Body)
            // Per-message batches can contain several Toasts. Their built-in audio
            // would bypass the per-round attention gate, so the gated MessageBeep is
            // the sole sound source.
            .MuteAudio()
            .BuildNotification();
        appNotification.Tag = notification.Tag;
        appNotification.Group = notification.Group;
        appNotification.Expiration = notification.Expiration;
        appNotification.ExpiresOnReboot = notification.ExpiresOnReboot;
        return appNotification;
    }

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
