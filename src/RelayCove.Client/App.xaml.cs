using System.Windows;
using Microsoft.Extensions.Logging;
using RelayCove.Client.Notifications;

namespace RelayCove.Client;

public partial class App : Application
{
    private ILoggerFactory? loggerFactory;
    private WindowsClientNotificationHost? notificationHost;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        loggerFactory = LoggerFactory.Create(builder => builder.AddDebug());
        notificationHost = new WindowsClientNotificationHost(
            WindowsAppSdkNotificationManager.Shared,
            ActivateNotificationTarget,
            loggerFactory.CreateLogger<WindowsClientNotificationHost>());
        notificationHost.TryStart();

        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        notificationHost?.Dispose();
        loggerFactory?.Dispose();
        base.OnExit(e);
    }

    private void ActivateNotificationTarget(ClientNotificationActivationTarget target)
    {
        _ = target;
        Dispatcher.BeginInvoke(() =>
        {
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
}
