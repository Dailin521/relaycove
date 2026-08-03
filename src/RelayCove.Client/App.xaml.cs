using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Extensions.Logging;
using RelayCove.Client.Accounts;
using RelayCove.Client.Activation;
using RelayCove.Client.Desktop;
using RelayCove.Client.Notifications;
using RelayCove.Client.Storage;
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
    private ClientAccountComposition? accountComposition;
    private bool? notificationRegistrationReady;
    private int lifecycleStopping;
    private int explicitExitRequested;
    private long lastAccountShellRevision;

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
            QueueAuthorizedNotificationNavigation,
            loggerFactory.CreateLogger<ClientNotificationActivationRouter>(),
            windowActivationSink: QueueAuthorizedNotificationWindowActivation);
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
                await ClientApplicationShutdown.RunBlockingOperationAsync(
                    notificationHost.Dispose);
                activationDispatcher.Dispose();
                notificationActivationRouter.Dispose();
                singleInstanceHost.Dispose();
                loggerFactory.CreateLogger<App>().LogError(
                    "Cold Windows notification activation registration failed closed.");
                Shutdown();
                return;
            }

            notificationRegistrationReady = true;
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

        try
        {
            accountComposition = CreateAccountComposition();
            accountComposition.Coordinator.SnapshotChanged += OnAccountShellSnapshotChanged;
            accountComposition.Coordinator.ConversationListChanged +=
                OnConversationListChanged;
            accountComposition.Coordinator.MessageListChanged += OnMessageListChanged;
        }
        catch (Exception exception)
        {
            Interlocked.Exchange(ref lifecycleStopping, 1);
            loggerFactory.CreateLogger<App>().LogCritical(
                "Creating the production account composition failed; errorType={ErrorType}.",
                exception.GetType().Name);
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
            notificationRegistrationReady = await RunNotificationHostStartAsync(
                notificationHost);
            if (MainWindow is MainWindow window)
            {
                window.SetNotificationAvailability(notificationRegistrationReady);
            }

            if (notificationRegistrationReady != true)
            {
                loggerFactory.CreateLogger<App>().LogWarning(
                    "Windows system notifications are unavailable; " +
                    "the account shell remains operational.");
            }
        }

        try
        {
            await accountComposition.Coordinator.RestoreAsync();
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref lifecycleStopping) != 0)
        {
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Interlocked.Exchange(ref lifecycleStopping, 1);
        trayHost?.Dispose();
        notificationAttention?.StopFlashing();
        mainWindowState?.Update(nint.Zero, isForeground: false);
        accountComposition?.DetachForProcessExit();
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

    private void NavigateNotificationTarget(
        ClientNotificationActivationTarget target)
    {
        if (Volatile.Read(ref lifecycleStopping) != 0)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => NavigateNotificationTarget(target));
            return;
        }

        var window = EnsureMainWindow();
        window.ShowAuthorizedNotificationTarget(target);
        notificationAttention?.StopFlashing();
    }

    private void QueueAuthorizedNotificationWindowActivation()
    {
        if (Volatile.Read(ref lifecycleStopping) == 0)
        {
            _ = Dispatcher.BeginInvoke(RestoreMainWindow);
        }
    }

    private void QueueAuthorizedNotificationNavigation(
        ClientNotificationActivationTarget target)
    {
        if (Volatile.Read(ref lifecycleStopping) == 0)
        {
            _ = Dispatcher.BeginInvoke(() => NavigateNotificationTarget(target));
        }
    }

    private ClientAccountComposition CreateAccountComposition()
    {
        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new InvalidOperationException(
                "The current Windows user does not expose a LocalAppData directory.");
        }

        return ClientAccountComposition.Create(
            Path.GetFullPath(Path.Combine(localAppData, "RelayCove")),
            notificationActivationRouter!,
            notificationAttention!,
            loggerFactory!);
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
        if (accountComposition is not null)
        {
            window.BindAccountShell(accountComposition.Coordinator);
        }

        window.SetNotificationAvailability(notificationRegistrationReady);

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

        accountComposition?.Coordinator.UpdateActivity(new ClientActivitySnapshot(
            window.IsVisible,
            window.WindowState == WindowState.Minimized,
            window.IsActive,
            OpenConversationId: null));
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
        accountComposition?.Coordinator.UpdateActivity(new ClientActivitySnapshot(
            IsMainWindowVisible: false,
            IsMainWindowMinimized: false,
            HasForegroundFocus: false,
            OpenConversationId: null));
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
        if (accountComposition is not null)
        {
            accountComposition.Coordinator.SnapshotChanged -= OnAccountShellSnapshotChanged;
            accountComposition.Coordinator.ConversationListChanged -=
                OnConversationListChanged;
            accountComposition.Coordinator.MessageListChanged -= OnMessageListChanged;
            try
            {
                await accountComposition.DisposeAsync();
            }
            catch (Exception exception)
            {
                loggerFactory?.CreateLogger<App>().LogWarning(
                    "Stopping the production account composition failed; " +
                    "errorType={ErrorType}.",
                    exception.GetType().Name);
            }
        }

        await ClientApplicationShutdown.StopPrimaryAsync(
            () => activationDispatcher?.Dispose(),
            () => notificationActivationRouter?.Dispose(),
            async () =>
            {
                if (host is not null)
                {
                    await ClientApplicationShutdown.RunBlockingOperationAsync(host.Dispose);
                }
            },
            () => singleInstanceHost?.Dispose());
        Shutdown();
    }

    private void OnAccountShellSnapshotChanged(ClientAccountShellSnapshot snapshot)
    {
        if (Volatile.Read(ref lifecycleStopping) != 0)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => OnAccountShellSnapshotChanged(snapshot));
            return;
        }

        if (snapshot.Revision < lastAccountShellRevision)
        {
            return;
        }

        lastAccountShellRevision = snapshot.Revision;

        if (MainWindow is MainWindow window)
        {
            window.ApplyAccountShellSnapshot(snapshot);
        }

        try
        {
            trayHost?.UpdateStatus(new ClientTrayStatus(
                snapshot.TotalUnreadCount,
                snapshot.ConnectionState));
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref lifecycleStopping) != 0)
        {
        }
    }

    private void OnConversationListChanged(LocalConversationListReadOutcome outcome)
    {
        if (Volatile.Read(ref lifecycleStopping) != 0)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => OnConversationListChanged(outcome));
            return;
        }

        if (MainWindow is MainWindow window)
        {
            window.ApplyConversationListSnapshot(outcome);
        }
    }

    private void OnMessageListChanged(ClientMessageListSnapshot snapshot)
    {
        if (Volatile.Read(ref lifecycleStopping) != 0)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => OnMessageListChanged(snapshot));
            return;
        }

        if (MainWindow is MainWindow window)
        {
            window.ApplyMessageListSnapshot(snapshot);
        }
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
