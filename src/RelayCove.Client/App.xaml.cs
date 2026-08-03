using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Extensions.Logging;
using RelayCove.Client.Activation;
using RelayCove.Client.Desktop;
using RelayCove.Client.Notifications;
using RelayCove.Shared.Realtime;

namespace RelayCove.Client;

public partial class App : System.Windows.Application
{
    private ILoggerFactory? loggerFactory;
    private WindowsSingleInstanceHost? singleInstanceHost;
    private ClientNotificationActivationRouter? notificationActivationRouter;
    private ClientActivationDispatcher? activationDispatcher;
    private WindowsClientNotificationHost? notificationHost;
    private WindowsMainWindowState? mainWindowState;
    private WindowsDesktopNotificationAttention? notificationAttention;
    private ClientTrayHost? trayHost;
    private int lifecycleStopping;
    private int explicitExitRequested;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        loggerFactory = LoggerFactory.Create(builder => builder.AddDebug());
        mainWindowState = new WindowsMainWindowState();
        notificationAttention = new WindowsDesktopNotificationAttention(
            mainWindowState,
            loggerFactory.CreateLogger<WindowsDesktopNotificationAttention>());
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

        // Establish close-to-tray before the window first becomes visible. Window.Show
        // can pump native messages, so creating the tray immediately after it would
        // still leave a narrow interval in which WM_CLOSE terminates the application.
        TryStartTrayHost();
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
        trayHost?.Dispose();
        notificationAttention?.StopFlashing();
        mainWindowState?.Update(nint.Zero, isForeground: false);
        activationDispatcher?.Dispose();
        notificationActivationRouter?.Dispose();
        notificationHost?.DetachForProcessExit();
        // Release the AppInstance key only after this process can no longer receive
        // native notification callbacks; a successor may register immediately.
        singleInstanceHost?.Dispose();
        loggerFactory?.Dispose();
        base.OnExit(e);
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // A normal Close hides to the tray. Windows logoff and shutdown must instead
        // flow through the real close path so they cannot leave the session waiting.
        Interlocked.Exchange(ref explicitExitRequested, 1);
        base.OnSessionEnding(e);
        if (e.Cancel)
        {
            // A later SessionEnding handler may cancel logoff/shutdown. The process
            // then remains interactive, so restore the normal close-to-tray and
            // explicit Exit behavior instead of leaving a stale shutdown latch.
            Interlocked.Exchange(ref explicitExitRequested, 0);
        }
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

    private async void RequestExplicitExit()
    {
        if (Volatile.Read(ref lifecycleStopping) != 0)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(RequestExplicitExit);
            return;
        }

        if (Interlocked.Exchange(ref explicitExitRequested, 1) != 0)
        {
            return;
        }

        if (MainWindow is MainWindow window)
        {
            window.Close();
            return;
        }

        Interlocked.Exchange(ref lifecycleStopping, 1);
        trayHost?.Dispose();
        notificationAttention?.StopFlashing();
        mainWindowState?.Update(nint.Zero, isForeground: false);
        await StopPrimaryAndShutdownAsync();
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
        window.SourceInitialized += OnMainWindowStateChanged;
        window.Activated += OnMainWindowStateChanged;
        window.Deactivated += OnMainWindowStateChanged;
        window.StateChanged += OnMainWindowStateChanged;
        window.IsVisibleChanged += OnMainWindowVisibilityChanged;
        window.Closing += OnMainWindowClosing;
        window.Closed += OnMainWindowClosed;
        MainWindow = window;
        window.Show();
        return window;
    }

    private void OnMainWindowStateChanged(object? sender, EventArgs e)
    {
        _ = e;
        if (sender is not MainWindow window)
        {
            return;
        }

        var handle = new WindowInteropHelper(window).Handle;
        var isForeground = handle != nint.Zero &&
            window.IsVisible &&
            window.WindowState != WindowState.Minimized &&
            window.IsActive;
        mainWindowState?.Update(handle, isForeground);
        if (isForeground)
        {
            notificationAttention?.StopFlashing();
        }
    }

    private void OnMainWindowVisibilityChanged(
        object sender,
        DependencyPropertyChangedEventArgs e)
    {
        _ = e;
        OnMainWindowStateChanged(sender, EventArgs.Empty);
    }

    private void OnMainWindowClosing(object? sender, CancelEventArgs e)
    {
        if (Volatile.Read(ref lifecycleStopping) != 0 ||
            Volatile.Read(ref explicitExitRequested) != 0 ||
            trayHost?.IsAvailable != true ||
            sender is not MainWindow window)
        {
            return;
        }

        e.Cancel = true;
        window.Hide();
        mainWindowState?.Update(
            new WindowInteropHelper(window).Handle,
            isForeground: false);
        loggerFactory?.CreateLogger<App>().LogInformation(
            "The main window was hidden to the notification area.");
    }

    private async void OnMainWindowClosed(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref lifecycleStopping, 1) != 0)
        {
            return;
        }

        if (sender is MainWindow window)
        {
            window.SourceInitialized -= OnMainWindowStateChanged;
            window.Activated -= OnMainWindowStateChanged;
            window.Deactivated -= OnMainWindowStateChanged;
            window.StateChanged -= OnMainWindowStateChanged;
            window.IsVisibleChanged -= OnMainWindowVisibilityChanged;
            window.Closing -= OnMainWindowClosing;
            window.Closed -= OnMainWindowClosed;
        }

        trayHost?.Dispose();
        trayHost = null;
        notificationAttention?.StopFlashing();
        mainWindowState?.Update(nint.Zero, isForeground: false);
        await StopPrimaryAndShutdownAsync();
    }

    private async Task StopPrimaryAndShutdownAsync()
    {
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

    private void TryStartTrayHost()
    {
        try
        {
            var candidate = new ClientTrayHost(
                new WindowsFormsClientTrayIcon(),
                action =>
                {
                    if (Dispatcher.CheckAccess())
                    {
                        action();
                    }
                    else
                    {
                        Dispatcher.BeginInvoke(action);
                    }
                },
                RestoreMainWindow,
                RequestExplicitExit,
                new ClientTrayStatus(
                    totalUnreadCount: 0,
                    ConnectionState.Disconnected),
                loggerFactory!.CreateLogger<ClientTrayHost>());
            if (candidate.TryStart())
            {
                trayHost = candidate;
            }
            else
            {
                candidate.Dispose();
            }
        }
        catch (Exception exception)
        {
            loggerFactory?.CreateLogger<App>().LogError(
                "Creating the Windows tray icon failed; errorType={ErrorType}.",
                exception.GetType().Name);
        }
    }
}
