using System.Windows;
using Microsoft.Extensions.Logging;
using RelayCove.Client.Activation;
using RelayCove.Client.Notifications;

namespace RelayCove.Client;

public partial class App : Application
{
    private ILoggerFactory? loggerFactory;
    private WindowsSingleInstanceHost? singleInstanceHost;
    private ClientNotificationActivationRouter? notificationActivationRouter;
    private ClientActivationDispatcher? activationDispatcher;
    private WindowsClientNotificationHost? notificationHost;
    private int lifecycleStopping;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        loggerFactory = LoggerFactory.Create(builder => builder.AddDebug());
        notificationActivationRouter = new ClientNotificationActivationRouter(
            NavigateNotificationTarget,
            loggerFactory.CreateLogger<ClientNotificationActivationRouter>(),
            windowActivationSink: RestoreMainWindow);
        activationDispatcher = new ClientActivationDispatcher(
            notificationActivationRouter,
            RestoreMainWindow,
            loggerFactory.CreateLogger<ClientActivationDispatcher>());
        singleInstanceHost = new WindowsSingleInstanceHost(
            new WindowsAppSdkInstanceProvider(
                loggerFactory.CreateLogger<WindowsAppSdkInstanceProvider>()),
            QueueProcessActivation,
            loggerFactory.CreateLogger<WindowsSingleInstanceHost>());

        if (WindowsAppSdkNotificationManager.RequiresRegistrationBeforeActivationRead())
        {
            notificationHost = CreateNotificationHost();
            var coldRegistrationReady = await RunNotificationHostStartAsync(notificationHost);
            if (!coldRegistrationReady)
            {
                Interlocked.Exchange(ref lifecycleStopping, 1);
                notificationHost.Dispose();
                activationDispatcher.Dispose();
                notificationActivationRouter.Dispose();
                singleInstanceHost.Dispose();
                loggerFactory.CreateLogger<App>().LogError(
                    "Cold Windows notification activation registration failed closed.");
                Shutdown();
                return;
            }
        }

        var instanceStatus = await singleInstanceHost.StartAsync();
        if (instanceStatus != WindowsSingleInstanceStartStatus.Primary)
        {
            Interlocked.Exchange(ref lifecycleStopping, 1);
            notificationHost?.DetachForProcessExit();
            activationDispatcher.Dispose();
            notificationActivationRouter.Dispose();
            singleInstanceHost.Dispose();
            Shutdown();
            return;
        }

        EnsureMainWindow();
        if (notificationHost is null)
        {
            notificationHost = CreateNotificationHost();
            _ = await RunNotificationHostStartAsync(notificationHost);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Interlocked.Exchange(ref lifecycleStopping, 1);
        activationDispatcher?.Dispose();
        notificationActivationRouter?.Dispose();
        notificationHost?.DetachForProcessExit();
        // Release the AppInstance key only after this process can no longer receive
        // native notification callbacks; a successor may register immediately.
        singleInstanceHost?.Dispose();
        loggerFactory?.Dispose();
        base.OnExit(e);
    }

    private static void NavigateNotificationTarget(
        ClientNotificationActivationTarget target)
    {
        // Stage 8 binds this validated target to the conversation/overview navigation shell.
        _ = target;
    }

    private WindowsClientNotificationHost CreateNotificationHost() =>
        new(
            WindowsAppSdkNotificationManager.Shared,
            QueueNotificationActivation,
            loggerFactory!.CreateLogger<WindowsClientNotificationHost>());

    private static Task<bool> RunNotificationHostStartAsync(
        WindowsClientNotificationHost host) =>
        Task.Factory.StartNew(
            host.TryStart,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

    private void RestoreMainWindow()
    {
        if (Volatile.Read(ref lifecycleStopping) != 0)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(RestoreMainWindow);
            return;
        }

        var window = EnsureMainWindow();

        if (!window.IsVisible)
        {
            window.Show();
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
    }

    private void QueueProcessActivation(WindowsProcessActivation activation)
    {
        if (Volatile.Read(ref lifecycleStopping) != 0)
        {
            return;
        }

        Dispatcher.BeginInvoke(() => activationDispatcher?.Dispatch(activation));
    }

    private void QueueNotificationActivation(ClientNotificationActivationTarget target)
    {
        if (Volatile.Read(ref lifecycleStopping) != 0)
        {
            return;
        }

        Dispatcher.BeginInvoke(() => activationDispatcher?.Dispatch(target));
    }

    private MainWindow EnsureMainWindow()
    {
        if (MainWindow is MainWindow existing)
        {
            return existing;
        }

        var window = new MainWindow();
        window.Closed += OnMainWindowClosed;
        MainWindow = window;
        window.Show();
        return window;
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
        await ClientApplicationShutdown.StopPrimaryAsync(
            () => activationDispatcher?.Dispose(),
            () => notificationActivationRouter?.Dispose(),
            async () =>
            {
                if (host is not null)
                {
                    await Task.Factory.StartNew(
                        host.Dispose,
                        CancellationToken.None,
                        TaskCreationOptions.LongRunning,
                        TaskScheduler.Default);
                }
            },
            () => singleInstanceHost?.Dispose());
        Shutdown();
    }
}
