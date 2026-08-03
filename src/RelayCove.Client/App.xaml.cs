using System.Windows;
using Microsoft.Extensions.Logging;
using RelayCove.Client.Notifications;

namespace RelayCove.Client;

public partial class App : Application
{
    private ILoggerFactory? loggerFactory;
    private WindowsClientNotificationHost? notificationHost;
    private int lifecycleStopping;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        loggerFactory = LoggerFactory.Create(builder => builder.AddDebug());
        notificationHost = new WindowsClientNotificationHost(
            WindowsAppSdkNotificationManager.Shared,
            ActivateNotificationTarget,
            loggerFactory.CreateLogger<WindowsClientNotificationHost>());

        MainWindow = new MainWindow();
        MainWindow.Closed += OnMainWindowClosed;
        MainWindow.Show();
        await Task.Factory.StartNew(
            notificationHost.TryStart,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        loggerFactory?.Dispose();
        base.OnExit(e);
    }

    private void ActivateNotificationTarget(ClientNotificationActivationTarget target)
    {
        _ = target;
        if (Volatile.Read(ref lifecycleStopping) != 0)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (Volatile.Read(ref lifecycleStopping) != 0)
            {
                return;
            }

            var window = MainWindow;
            if (window is null)
            {
                window = new MainWindow();
                MainWindow = window;
            }

            if (!window.IsVisible)
            {
                window.Show();
            }

            if (window.WindowState == WindowState.Minimized)
            {
                window.WindowState = WindowState.Normal;
            }

            window.Activate();
        });
    }

    private async void OnMainWindowClosed(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref lifecycleStopping, 1) != 0)
        {
            return;
        }

        if (sender is MainWindow window)
        {
            window.Closed -= OnMainWindowClosed;
        }

        var host = notificationHost;
        if (host is not null)
        {
            await Task.Factory.StartNew(
                host.Dispose,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        Shutdown();
    }
}
