using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Extensions.Logging;
using RelayCove.Client.Accounts;
using RelayCove.Client.Activation;
using RelayCove.Client.Desktop;
using RelayCove.Client.Notifications;
using RelayCove.Client.Storage;
using RelayCove.Client.Updates;
using RelayCove.Shared.Realtime;
using RelayCove.Shared.Updates;

namespace RelayCove.Client;

public partial class App : System.Windows.Application
{
    private const string BootstrapRecordFileName = "owned-bootstrap-token.v1";
    private const string BootstrapMarkerFileName = ".relaycove-bootstrap-owner";
    private static readonly TimeSpan[] BootstrapCleanupDelays =
    [
        TimeSpan.Zero,
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromSeconds(1),
    ];
    private ILoggerFactory? loggerFactory;
    private readonly object bootstrapRecordGate = new();
    private WindowsSingleInstanceHost? singleInstanceHost;
    private ClientNotificationActivationRouter? notificationActivationRouter;
    private ClientActivationDispatcher? activationDispatcher;
    private WindowsClientNotificationHost? notificationHost;
    private WindowsMainWindowState? mainWindowState;
    private WindowsDesktopNotificationAttention? notificationAttention;
    private ClientTrayHost? trayHost;
    private ClientAccountComposition? accountComposition;
    private HttpClient? updateManifestHttpClient;
    private HttpClient? updateHttpClient;
    private ClientUpdateCoordinator? updateCoordinator;
    private string? updateCacheRoot;
    private bool? notificationRegistrationReady;
    private int lifecycleStopping;
    private int explicitExitRequested;
    private int updateHandoffStarted;
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
            updateCoordinator = CreateUpdateCoordinator();
            updateCoordinator.StateChanged += OnUpdateStateChanged;
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
            _ = CleanupOwnedBootstrapAsync();
            var savedServerBaseUri = await accountComposition.GetStoredServerBaseUriAsync();
            if (savedServerBaseUri is not null)
            {
                await updateCoordinator.CheckAsync(savedServerBaseUri);
            }

            if (updateCoordinator.State.IsMandatory)
            {
                return;
            }

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
        _ = updateCoordinator?.DisposeAsync();
        updateManifestHttpClient?.Dispose();
        updateHttpClient?.Dispose();
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
        var localAppDataRoot = GetLocalApplicationDataRoot();
        return ClientAccountComposition.Create(
            localAppDataRoot,
            notificationActivationRouter!,
            notificationAttention!,
            loggerFactory!);
    }

    private ClientUpdateCoordinator CreateUpdateCoordinator()
    {
        var localAppDataRoot = GetLocalApplicationDataRoot();
        updateCacheRoot = Path.GetFullPath(Path.Combine(localAppDataRoot, "Updates"));
        updateManifestHttpClient = CreateUpdateHttpClient(
            ClientUpdateManifestHttpTransport.DefaultCheckTimeout);
        updateHttpClient = CreateUpdateHttpClient(TimeSpan.FromMinutes(10));
        var updateLogger = loggerFactory!.CreateLogger<ClientUpdateCoordinator>();
        var factory = loggerFactory!;
        return new ClientUpdateCoordinator(
            new ClientUpdateManifestHttpTransport(
                updateManifestHttpClient,
                factory.CreateLogger<ClientUpdateManifestHttpTransport>()),
            new ClientAssemblyCurrentVersionProvider(),
            new ClientUpdatePackageDownloader(
                updateCacheRoot,
                updateHttpClient,
                factory.CreateLogger<ClientUpdatePackageDownloader>()),
            updateLogger);
    }

    private static HttpClient CreateUpdateHttpClient(TimeSpan timeout) =>
        new(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
        })
        {
            Timeout = timeout,
        };

    private static string GetLocalApplicationDataRoot()
    {
        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new InvalidOperationException(
                "The current Windows user does not expose a LocalAppData directory.");
        }

        return Path.GetFullPath(Path.Combine(localAppData, "RelayCove"));
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

        if (updateCoordinator is not null)
        {
            window.BindUpdateActions(
                CheckTypedServerBeforeLoginAsync,
                CheckForUpdatesAsync,
                DownloadUpdateAsync,
                CancelUpdateDownload,
                ApplyDownloadedUpdateAsync,
                RequestExplicitExit);
            window.ApplyUpdateState(updateCoordinator.State);
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
            window.CancelAttachmentInputForShutdown();
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
        if (updateCoordinator is not null)
        {
            updateCoordinator.StateChanged -= OnUpdateStateChanged;
            await updateCoordinator.DisposeAsync();
        }

        updateManifestHttpClient?.Dispose();
        updateHttpClient?.Dispose();
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

    private void OnUpdateStateChanged(ClientUpdateState state)
    {
        if (Volatile.Read(ref lifecycleStopping) != 0)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => OnUpdateStateChanged(state));
            return;
        }

        if (MainWindow is MainWindow window)
        {
            window.ApplyUpdateState(state);
        }
    }

    private async Task<bool> CheckTypedServerBeforeLoginAsync(string serverAddress)
    {
        if (!Uri.TryCreate(serverAddress, UriKind.Absolute, out var parsedServerUri))
        {
            return true;
        }

        try
        {
            var serverBaseUri = Auth.ClientAuthenticationUri.CanonicalizeServerBaseUri(parsedServerUri);
            await updateCoordinator!.CheckAsync(serverBaseUri);
            return !updateCoordinator.State.IsMandatory;
        }
        catch (ArgumentException)
        {
            // The authentication flow owns validation feedback for a malformed address.
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private async Task CheckForUpdatesAsync()
    {
        var serverBaseUri = accountComposition?.Coordinator.Snapshot.ServerBaseUri;
        if (serverBaseUri is null || updateCoordinator is null)
        {
            return;
        }

        try
        {
            await updateCoordinator.CheckAsync(serverBaseUri);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task DownloadUpdateAsync()
    {
        try
        {
            if (updateCoordinator is not null)
            {
                await updateCoordinator.DownloadAsync();
            }
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void CancelUpdateDownload()
    {
        updateCoordinator?.CancelDownload();
    }

    private async Task ApplyDownloadedUpdateAsync()
    {
        var coordinator = updateCoordinator;
        var state = coordinator?.State;
        if (state is null || state.Phase != ClientUpdatePhase.Downloaded ||
            state.Manifest is null || string.IsNullOrWhiteSpace(state.ArchivePath) ||
            string.IsNullOrWhiteSpace(state.CurrentVersion) ||
            Interlocked.CompareExchange(ref updateHandoffStarted, 1, 0) != 0)
        {
            return;
        }

        var token = Guid.NewGuid().ToString("N");
        if (!TryPersistBootstrapToken(token))
        {
            TryDeleteBootstrapRecordIfMatches(token);
            ShowUpdateHandoffFailure("无法安全记录更新交接状态，请重试。");
            Interlocked.Exchange(ref updateHandoffStarted, 0);
            return;
        }

        Process? updaterProcess = null;
        try
        {
            var appDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
            var updaterPath = Path.Combine(appDirectory, "RelayCove.Updater.exe");
            if (!File.Exists(updaterPath) || IsReparsePoint(updaterPath))
            {
                throw new InvalidOperationException("The package-local updater is unavailable.");
            }

            using var currentProcess = Process.GetCurrentProcess();
            var startInfo = new ProcessStartInfo
            {
                FileName = updaterPath,
                WorkingDirectory = appDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            AddUpdaterArguments(
                startInfo,
                state.Manifest,
                state.ArchivePath,
                state.CurrentVersion,
                appDirectory,
                currentProcess.Id,
                currentProcess.StartTime.ToUniversalTime().Ticks,
                token);

            updaterProcess = Process.Start(startInfo) ??
                throw new InvalidOperationException("The package-local updater did not start.");
        }
        catch (Exception exception) when (exception is InvalidOperationException or
            System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            updaterProcess?.Dispose();
            TryDeleteBootstrapRecordIfMatches(token);
            loggerFactory?.CreateLogger<App>().LogWarning(
                "Starting the package-local updater failed; errorType={ErrorType}.",
                exception.GetType().Name);
            ShowUpdateHandoffFailure("更新程序未能接受交接；当前 RelayCove 仍可继续使用，请重试。");
            Interlocked.Exchange(ref updateHandoffStarted, 0);
            return;
        }

        ShowUpdateHandoffConfirming();
        try
        {
            using (updaterProcess)
            {
                // This is only the package-local bootstrap parent. It never waits for
                // this Client, so await its determinate acceptance result without a
                // timeout that could orphan an already-started external bootstrap.
                await updaterProcess.WaitForExitAsync();
                if (updaterProcess.ExitCode != 0)
                {
                    TryDeleteBootstrapRecordIfMatches(token);
                    loggerFactory?.CreateLogger<App>().LogWarning(
                        "The package-local updater rejected the handoff; exitCode={ExitCode}.",
                        updaterProcess.ExitCode);
                    ShowUpdateHandoffFailure(
                        "更新程序未能接受交接；当前 RelayCove 仍可继续使用，请重试。");
                    Interlocked.Exchange(ref updateHandoffStarted, 0);
                    return;
                }
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or
            System.ComponentModel.Win32Exception or IOException)
        {
            // Once Process.Start succeeds, an indeterminate wait failure must retain
            // both the latch and ownership record. The external bootstrap may already
            // be running, and a second handoff would violate single ownership.
            loggerFactory?.CreateLogger<App>().LogWarning(
                "Waiting for updater bootstrap acceptance became indeterminate; " +
                "errorType={ErrorType}.",
                exception.GetType().Name);
            ShowUpdateHandoffConfirming();
            return;
        }

        // Exit code 0 means only that the external bootstrap accepted ownership.
        // It is intentionally not an apply-complete signal.
        RequestExplicitExit();
    }

    private void ShowUpdateHandoffFailure(string message)
    {
        if (MainWindow is MainWindow window)
        {
            window.ShowUpdateHandoffFailure(message);
        }
    }

    private void ShowUpdateHandoffConfirming()
    {
        if (MainWindow is MainWindow window)
        {
            window.ShowUpdateHandoffConfirming();
        }
    }

    internal static void AddUpdaterArguments(
        ProcessStartInfo startInfo,
        UpdateManifestDto manifest,
        string archivePath,
        string currentVersion,
        string targetDirectory,
        int currentProcessId,
        long currentProcessStartTimeUtcTicks,
        string bootstrapToken)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);
        if (currentProcessId <= 0 || currentProcessStartTimeUtcTicks <= 0 ||
            !IsBootstrapToken(bootstrapToken))
        {
            throw new ArgumentOutOfRangeException(nameof(currentProcessId));
        }

        startInfo.ArgumentList.Add("apply");
        startInfo.ArgumentList.Add("--archive");
        startInfo.ArgumentList.Add(Path.GetFullPath(archivePath));
        startInfo.ArgumentList.Add("--expected-sha256");
        startInfo.ArgumentList.Add(manifest.Artifact.Sha256);
        startInfo.ArgumentList.Add("--expected-size");
        startInfo.ArgumentList.Add(manifest.Artifact.SizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--expected-version");
        startInfo.ArgumentList.Add(manifest.Version);
        startInfo.ArgumentList.Add("--current-version");
        startInfo.ArgumentList.Add(currentVersion);
        startInfo.ArgumentList.Add("--target");
        startInfo.ArgumentList.Add(Path.GetFullPath(targetDirectory));
        startInfo.ArgumentList.Add("--wait-pid");
        startInfo.ArgumentList.Add(currentProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--wait-start-time-utc-ticks");
        startInfo.ArgumentList.Add(currentProcessStartTimeUtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--bootstrap-token");
        startInfo.ArgumentList.Add(bootstrapToken);
    }

    private bool TryPersistBootstrapToken(string token)
    {
        if (!IsBootstrapToken(token) || string.IsNullOrWhiteSpace(updateCacheRoot))
        {
            return false;
        }

        try
        {
            lock (bootstrapRecordGate)
            {
                Directory.CreateDirectory(updateCacheRoot);
                if (IsReparsePoint(updateCacheRoot))
                {
                    return false;
                }

                var recordPath = GetBootstrapRecordPath();
                var temporaryPath = recordPath + ".tmp";
                if ((File.Exists(recordPath) && IsReparsePoint(recordPath)) ||
                    (File.Exists(temporaryPath) && IsReparsePoint(temporaryPath)))
                {
                    return false;
                }

                if (File.Exists(recordPath))
                {
                    var existingToken = File.ReadAllText(recordPath, Encoding.UTF8);
                    if (IsBootstrapToken(existingToken) &&
                        !TryDeleteOwnedBootstrap(existingToken))
                    {
                        return false;
                    }

                    if (!CompareAndDeleteBootstrapRecord(recordPath, existingToken))
                    {
                        return false;
                    }
                }

                File.Delete(temporaryPath);
                File.WriteAllText(
                    temporaryPath,
                    token,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                File.Move(temporaryPath, recordPath, overwrite: false);
                return true;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            System.Security.SecurityException)
        {
            loggerFactory?.CreateLogger<App>().LogWarning(
                "Update bootstrap ownership record failed; errorType={ErrorType}.",
                exception.GetType().Name);
            return false;
        }
    }

    private async Task CleanupOwnedBootstrapAsync()
    {
        if (string.IsNullOrWhiteSpace(updateCacheRoot))
        {
            return;
        }

        string? token;
        try
        {
            lock (bootstrapRecordGate)
            {
                var recordPath = GetBootstrapRecordPath();
                token = File.Exists(recordPath) && !IsReparsePoint(recordPath)
                    ? File.ReadAllText(recordPath, Encoding.UTF8)
                    : null;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            System.Security.SecurityException)
        {
            loggerFactory?.CreateLogger<App>().LogWarning(
                "Update bootstrap ownership read failed; errorType={ErrorType}.",
                exception.GetType().Name);
            return;
        }

        if (!IsBootstrapToken(token))
        {
            if (token is not null)
            {
                TryDeleteBootstrapRecordIfMatches(token);
            }

            return;
        }

        foreach (var delay in BootstrapCleanupDelays)
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay);
            }

            if (TryDeleteOwnedBootstrap(token!))
            {
                TryDeleteBootstrapRecordIfMatches(token!);
                return;
            }
        }
    }

    private bool TryDeleteOwnedBootstrap(string token)
    {
        var appDirectory = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(AppContext.BaseDirectory));
        var packageParent = Directory.GetParent(appDirectory)?.FullName;
        return packageParent is not null && TryDeleteOwnedBootstrap(
            token,
            appDirectory,
            packageParent);
    }

    internal static bool TryDeleteOwnedBootstrap(
        string token,
        string appDirectory,
        string packageParent)
    {
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(appDirectory);
            ArgumentException.ThrowIfNullOrWhiteSpace(packageParent);
            if (!IsBootstrapToken(token))
            {
                return false;
            }

            appDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(appDirectory));
            packageParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(packageParent));
            if (!string.Equals(
                    Directory.GetParent(appDirectory)?.FullName,
                    packageParent,
                    StringComparison.OrdinalIgnoreCase) ||
                !Directory.Exists(packageParent) ||
                IsReparsePoint(packageParent))
            {
                return false;
            }

            var expectedDirectory = Path.GetFullPath(Path.Combine(
                packageParent,
                ".relaycove-updater-" + token));
            if (!string.Equals(Path.GetDirectoryName(expectedDirectory), packageParent,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!Directory.Exists(expectedDirectory))
            {
                return true;
            }

            if (IsReparsePoint(expectedDirectory))
            {
                return false;
            }

            var expectedUpdater = Path.Combine(expectedDirectory, "RelayCove.Updater.exe");
            var expectedMarker = Path.Combine(expectedDirectory, BootstrapMarkerFileName);
            var entries = Directory.GetFileSystemEntries(expectedDirectory);
            if (entries.Length != 2 || entries.Any(entry =>
                    !string.Equals(entry, expectedUpdater, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(entry, expectedMarker, StringComparison.OrdinalIgnoreCase)) ||
                !File.Exists(expectedUpdater) || !File.Exists(expectedMarker) ||
                IsReparsePoint(expectedUpdater) || IsReparsePoint(expectedMarker) ||
                !string.Equals(
                    File.ReadAllText(expectedMarker, Encoding.UTF8),
                    "relaycove-bootstrap-owner:" + token,
                    StringComparison.Ordinal))
            {
                return false;
            }

            File.Delete(expectedUpdater);
            File.Delete(expectedMarker);
            Directory.Delete(expectedDirectory, recursive: false);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            System.Security.SecurityException)
        {
            return false;
        }
    }

    private string GetBootstrapRecordPath() => Path.Combine(updateCacheRoot!, BootstrapRecordFileName);

    private bool TryDeleteBootstrapRecordIfMatches(string expectedToken)
    {
        try
        {
            lock (bootstrapRecordGate)
            {
                return CompareAndDeleteBootstrapRecord(
                    GetBootstrapRecordPath(),
                    expectedToken);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            System.Security.SecurityException)
        {
            loggerFactory?.CreateLogger<App>().LogWarning(
                "Update bootstrap ownership record cleanup failed; errorType={ErrorType}.",
                exception.GetType().Name);
            return false;
        }
    }

    internal static bool CompareAndDeleteBootstrapRecord(
        string recordPath,
        string expectedToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordPath);
        ArgumentNullException.ThrowIfNull(expectedToken);
        if (!File.Exists(recordPath) || IsReparsePoint(recordPath) ||
            !string.Equals(
                File.ReadAllText(recordPath, Encoding.UTF8),
                expectedToken,
                StringComparison.Ordinal))
        {
            return false;
        }

        File.Delete(recordPath);
        return true;
    }

    private static bool IsBootstrapToken(string? value) =>
        value?.Length == 32 && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

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
