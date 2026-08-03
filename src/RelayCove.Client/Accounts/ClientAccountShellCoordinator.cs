using Microsoft.Extensions.Logging;
using RelayCove.Client.Activation;
using RelayCove.Client.Auth;
using RelayCove.Client.Sync;
using RelayCove.Shared.Auth;
using RelayCove.Shared.Realtime;

namespace RelayCove.Client.Accounts;

internal sealed class ClientAccountShellCoordinator : IAsyncDisposable
{
    private readonly object stateGate = new();
    // These primitives intentionally remain undisposed after cancellation because queued
    // operations can still observe the lifetime token while shutdown is converging.
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private readonly IClientPersistentAuthentication authentication;
    private readonly IClientAccountRuntimeFactory runtimeFactory;
    private readonly ClientNotificationActivationRouter activationRouter;
    private readonly Func<string> deviceNameProvider;
    private readonly Func<string> clientVersionProvider;
    private readonly ILogger<ClientAccountShellCoordinator> logger;
    private ClientAccountShellSnapshot snapshot = ClientAccountShellSnapshot.Initial;
    private IClientAccountRuntime? runtime;
    private IDisposable? activationLease;
    private ClientActivitySnapshot latestActivity = ClientActivitySnapshot.Inactive;
    private string? activeDisplayName;
    private Uri? activeServerBaseUri;
    private Task? disposeTask;
    private bool detachedForProcessExit;
    private int disposeStarted;

    public ClientAccountShellCoordinator(
        IClientPersistentAuthentication authentication,
        IClientAccountRuntimeFactory runtimeFactory,
        ClientNotificationActivationRouter activationRouter,
        ILogger<ClientAccountShellCoordinator> logger,
        Func<string>? deviceNameProvider = null,
        Func<string>? clientVersionProvider = null)
    {
        this.authentication = authentication ??
            throw new ArgumentNullException(nameof(authentication));
        this.runtimeFactory = runtimeFactory ??
            throw new ArgumentNullException(nameof(runtimeFactory));
        this.activationRouter = activationRouter ??
            throw new ArgumentNullException(nameof(activationRouter));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.deviceNameProvider = deviceNameProvider ?? GetDeviceName;
        this.clientVersionProvider = clientVersionProvider ?? GetClientVersion;
    }

    public event Action<ClientAccountShellSnapshot>? SnapshotChanged;

    public ClientAccountShellSnapshot Snapshot => Volatile.Read(ref snapshot);

    public Task RestoreAsync(CancellationToken cancellationToken = default) =>
        StartRestoreAsync(cancellationToken);

    public Task LoginAsync(
        string serverAddress,
        string userName,
        string password,
        CancellationToken cancellationToken = default)
    {
        ThrowIfStopping();
        if (!TryCreateLoginRequest(
                serverAddress,
                userName,
                password,
                out var serverBaseUri,
                out var request))
        {
            return PublishValidationFailureAsync(cancellationToken);
        }

        return AuthenticateAsync(
            ClientAccountShellPhase.SigningIn,
            token => authentication.LoginAsync(serverBaseUri!, request!, token),
            cancellationToken);
    }

    public async Task RetryAsync(CancellationToken cancellationToken = default)
    {
        using var linkedCancellation = CreateLinkedCancellation(cancellationToken);
        await operationGate.WaitAsync(linkedCancellation.Token).ConfigureAwait(false);
        try
        {
            ThrowIfStopping();
            var activeRuntime = runtime;
            if (activeRuntime is null)
            {
                return;
            }

            PublishActiveSnapshot(
                ClientAccountShellPhase.Retrying,
                activeRuntime.ConnectionState,
                Snapshot.LastSyncStatus);
            var outcome = await activeRuntime
                .RetryRealtimeAsync(linkedCancellation.Token)
                .ConfigureAwait(false);
            linkedCancellation.Token.ThrowIfCancellationRequested();
            if (outcome.Status == ClientSyncRunStatus.AuthenticationRequired)
            {
                await EndAuthenticationRequiredSessionAsync(activeRuntime)
                    .ConfigureAwait(false);
                return;
            }

            if (outcome.Status == ClientSyncRunStatus.Completed)
            {
                EnsureActivationLease(activeRuntime);
            }

            PublishActiveSnapshot(
                ClientAccountShellPhase.Active,
                activeRuntime.ConnectionState,
                outcome.Status);
        }
        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested)
        {
            PublishStoppingSnapshot();
        }
        catch (OperationCanceledException)
        {
            RestoreCurrentActiveSnapshot();
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Retrying the active account failed; errorType={ErrorType}.",
                exception.GetType().Name);
            RestoreCurrentActiveSnapshot(ClientSyncRunStatus.RemoteFailure);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        using var linkedCancellation = CreateLinkedCancellation(cancellationToken);
        await operationGate.WaitAsync(linkedCancellation.Token).ConfigureAwait(false);
        try
        {
            ThrowIfStopping();
            var activeRuntime = runtime;
            if (activeRuntime is null)
            {
                PublishSnapshot(ClientAccountShellSnapshot.SignedOut());
                return;
            }

            PublishActiveSnapshot(
                ClientAccountShellPhase.SigningOut,
                activeRuntime.ConnectionState,
                Snapshot.LastSyncStatus);
            ClearActiveOwnership(out var lease, out var detachedRuntime);
            lease?.Dispose();

            if (detachedRuntime is null)
            {
                PublishSnapshot(ClientAccountShellSnapshot.SignedOut());
                return;
            }

            var logoutStatus = await CompleteRuntimeLogoutAsync(detachedRuntime)
                .ConfigureAwait(false);

            PublishSnapshot(ClientAccountShellSnapshot.SignedOut(
                logoutStatus: logoutStatus));
        }
        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested)
        {
            PublishStoppingSnapshot();
        }
        finally
        {
            operationGate.Release();
        }
    }

    public void UpdateActivity(ClientActivitySnapshot activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        IClientAccountRuntime? activeRuntime;
        lock (stateGate)
        {
            latestActivity = activity;
            activeRuntime = runtime;
        }

        if (activeRuntime is null || Volatile.Read(ref disposeStarted) != 0)
        {
            return;
        }

        try
        {
            activeRuntime.UpdateActivity(activity);
        }
        catch (ObjectDisposedException) when (
            Volatile.Read(ref disposeStarted) != 0 ||
            !IsCurrentRuntime(activeRuntime))
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Publishing account window activity failed; errorType={ErrorType}.",
                exception.GetType().Name);
        }
    }

    public void DetachForProcessExit()
    {
        IDisposable? lease;
        lock (stateGate)
        {
            if (Interlocked.Exchange(ref disposeStarted, 1) != 0)
            {
                return;
            }

            detachedForProcessExit = true;
            lease = activationLease;
            activationLease = null;
            SnapshotChanged = null;
        }

        lifetimeCancellation.Cancel();
        lease?.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Task sharedDispose;
        TaskCompletionSource? completion = null;
        lock (stateGate)
        {
            if (disposeTask is null)
            {
                if (detachedForProcessExit)
                {
                    disposeTask = Task.CompletedTask;
                }
                else
                {
                    Interlocked.Exchange(ref disposeStarted, 1);
                    completion = new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    disposeTask = completion.Task;
                }
            }

            sharedDispose = disposeTask;
        }

        if (completion is not null)
        {
            lifetimeCancellation.Cancel();
            _ = CompleteDisposeAsync(completion);
        }

        return new ValueTask(sharedDispose);
    }

    private Task StartRestoreAsync(CancellationToken cancellationToken)
    {
        ThrowIfStopping();
        return AuthenticateAsync(
            ClientAccountShellPhase.Restoring,
            token => authentication.RestoreAsync(token),
            cancellationToken);
    }

    private async Task CompleteDisposeAsync(TaskCompletionSource completion)
    {
        Exception? failure = null;
        try
        {
            await operationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                ClearActiveOwnership(out var lease, out var activeRuntime);
                lease?.Dispose();
                if (activeRuntime is not null)
                {
                    try
                    {
                        await activeRuntime.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        logger.LogWarning(
                            "Disposing the active account during application shutdown failed; " +
                            "errorType={ErrorType}.",
                            exception.GetType().Name);
                    }
                }

                PublishStoppingSnapshot();
            }
            finally
            {
                operationGate.Release();
            }
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            lock (stateGate)
            {
                SnapshotChanged = null;
            }

            if (failure is null)
            {
                completion.TrySetResult();
            }
            else
            {
                completion.TrySetException(failure);
            }
        }
    }

    private async Task PublishValidationFailureAsync(CancellationToken cancellationToken)
    {
        using var linkedCancellation = CreateLinkedCancellation(cancellationToken);
        await operationGate.WaitAsync(linkedCancellation.Token).ConfigureAwait(false);
        try
        {
            ThrowIfStopping();
            if (runtime is null)
            {
                PublishSnapshot(ClientAccountShellSnapshot.SignedOut(
                    PersistentClientAuthenticationStatus.ValidationFailed));
            }
        }
        finally
        {
            operationGate.Release();
        }
    }

    private async Task AuthenticateAsync(
        ClientAccountShellPhase phase,
        Func<CancellationToken, Task<PersistentClientAuthenticationOutcome>> authenticate,
        CancellationToken cancellationToken)
    {
        using var linkedCancellation = CreateLinkedCancellation(cancellationToken);
        await operationGate.WaitAsync(linkedCancellation.Token).ConfigureAwait(false);
        ClientAuthenticationSession? unownedSession = null;
        try
        {
            ThrowIfStopping();
            if (runtime is not null)
            {
                return;
            }

            PublishSnapshot(new ClientAccountShellSnapshot(
                phase,
                AuthenticationStatus: null,
                DisplayName: null,
                ServerBaseUri: null,
                ConnectionState.Disconnected,
                LastSyncStatus: null,
                LastLogoutStatus: null,
                RetryAfter: null));

            var outcome = await authenticate(linkedCancellation.Token).ConfigureAwait(false);
            unownedSession = outcome.Session;
            if (outcome.Status != PersistentClientAuthenticationStatus.Authenticated ||
                unownedSession is null)
            {
                PublishSnapshot(ClientAccountShellSnapshot.SignedOut(
                    outcome.Status,
                    retryAfter: outcome.RetryAfter));
                return;
            }

            linkedCancellation.Token.ThrowIfCancellationRequested();
            var displayName = unownedSession.DisplayName;
            var serverBaseUri = unownedSession.ServerBaseUri;
            PublishSnapshot(new ClientAccountShellSnapshot(
                ClientAccountShellPhase.Starting,
                outcome.Status,
                displayName,
                serverBaseUri,
                ConnectionState.Connecting,
                LastSyncStatus: null,
                LastLogoutStatus: null,
                RetryAfter: null));

            IClientAccountRuntime? unownedRuntime = await runtimeFactory
                .CreateAsync(unownedSession, linkedCancellation.Token)
                .ConfigureAwait(false);
            unownedSession = null;
            IDisposable? unownedLease = null;
            try
            {
                ClientActivitySnapshot activityBeforeStart;
                lock (stateGate)
                {
                    activityBeforeStart = latestActivity;
                }

                unownedRuntime.UpdateActivity(activityBeforeStart);
                var startOutcome = await unownedRuntime
                    .StartAsync(linkedCancellation.Token)
                    .ConfigureAwait(false);
                linkedCancellation.Token.ThrowIfCancellationRequested();
                if (startOutcome.StartupSyncOutcome.Status ==
                    ClientSyncRunStatus.AuthenticationRequired)
                {
                    var logoutStatus = await CompleteRuntimeLogoutAsync(unownedRuntime)
                        .ConfigureAwait(false);
                    unownedRuntime = null;
                    PublishSnapshot(ClientAccountShellSnapshot.SignedOut(
                        PersistentClientAuthenticationStatus.AuthenticationFailed,
                        logoutStatus));
                    return;
                }

                if (startOutcome.IsAuthoritativeCacheReady)
                {
                    unownedLease = activationRouter.ActivateAccount(
                        unownedRuntime.Identity.Id,
                        unownedRuntime.TryAuthorizeNotificationTarget);
                }

                lock (stateGate)
                {
                    ThrowIfStopping();
                    runtime = unownedRuntime;
                    activationLease = unownedLease;
                    activeDisplayName = displayName;
                    activeServerBaseUri = serverBaseUri;
                }

                unownedRuntime = null;
                unownedLease = null;
                RestoreLatestActivity();
                PublishActiveSnapshot(
                    ClientAccountShellPhase.Active,
                    startOutcome.RealtimeState,
                    startOutcome.StartupSyncOutcome.Status);
            }
            finally
            {
                unownedLease?.Dispose();
                if (unownedRuntime is not null)
                {
                    await unownedRuntime.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested)
        {
            PublishStoppingSnapshot();
        }
        catch (OperationCanceledException)
        {
            PublishSnapshot(ClientAccountShellSnapshot.SignedOut());
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref disposeStarted) != 0)
        {
            PublishStoppingSnapshot();
        }
        catch (Exception exception)
        {
            logger.LogError(
                "Starting a production account failed; errorType={ErrorType}.",
                exception.GetType().Name);
            PublishSnapshot(ClientAccountShellSnapshot.SignedOut(
                PersistentClientAuthenticationStatus.RemoteFailure));
        }
        finally
        {
            if (unownedSession is not null)
            {
                await unownedSession.DisposeAsync().ConfigureAwait(false);
            }

            operationGate.Release();
        }
    }

    private bool TryCreateLoginRequest(
        string serverAddress,
        string userName,
        string password,
        out Uri? serverBaseUri,
        out LoginRequest? request)
    {
        serverBaseUri = null;
        request = null;
        var trimmedUserName = userName?.Trim();
        if (string.IsNullOrWhiteSpace(serverAddress) ||
            string.IsNullOrWhiteSpace(trimmedUserName) ||
            trimmedUserName.Length > 64 ||
            string.IsNullOrEmpty(password) ||
            password.Length > 1_024 ||
            !Uri.TryCreate(serverAddress.Trim(), UriKind.Absolute, out var parsed))
        {
            return false;
        }

        try
        {
            serverBaseUri = ClientAuthenticationUri.CanonicalizeServerBaseUri(parsed);
            var deviceName = deviceNameProvider();
            var clientVersion = clientVersionProvider();
            if (string.IsNullOrWhiteSpace(deviceName) ||
                deviceName.Length > 128 ||
                string.IsNullOrWhiteSpace(clientVersion) ||
                clientVersion.Length > 64)
            {
                return false;
            }

            request = new LoginRequest(
                trimmedUserName,
                password,
                deviceName,
                clientVersion);
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException)
        {
            logger.LogWarning(
                "Validating a login request failed; errorType={ErrorType}.",
                exception.GetType().Name);
            return false;
        }
    }

    private void PublishActiveSnapshot(
        ClientAccountShellPhase phase,
        RelayCove.Shared.Realtime.ConnectionState connectionState,
        ClientSyncRunStatus? syncStatus)
    {
        string? displayName;
        Uri? serverBaseUri;
        lock (stateGate)
        {
            displayName = activeDisplayName;
            serverBaseUri = activeServerBaseUri;
        }

        PublishSnapshot(new ClientAccountShellSnapshot(
            phase,
            PersistentClientAuthenticationStatus.Authenticated,
            displayName,
            serverBaseUri,
            connectionState,
            syncStatus,
            LastLogoutStatus: null,
            RetryAfter: null));
    }

    private void RestoreCurrentActiveSnapshot(ClientSyncRunStatus? syncStatus = null)
    {
        IClientAccountRuntime? activeRuntime;
        lock (stateGate)
        {
            activeRuntime = runtime;
        }

        if (activeRuntime is not null)
        {
            PublishActiveSnapshot(
                ClientAccountShellPhase.Active,
                activeRuntime.ConnectionState,
                syncStatus ?? Snapshot.LastSyncStatus);
        }
    }

    private async Task EndAuthenticationRequiredSessionAsync(
        IClientAccountRuntime expectedRuntime)
    {
        IDisposable? lease;
        IClientAccountRuntime? detachedRuntime;
        lock (stateGate)
        {
            if (!ReferenceEquals(runtime, expectedRuntime))
            {
                return;
            }

            lease = activationLease;
            detachedRuntime = runtime;
            activationLease = null;
            runtime = null;
            activeDisplayName = null;
            activeServerBaseUri = null;
        }

        lease?.Dispose();
        var logoutStatus = detachedRuntime is null
            ? ClientLogoutStatus.LoggedOut
            : await CompleteRuntimeLogoutAsync(detachedRuntime).ConfigureAwait(false);
        PublishSnapshot(ClientAccountShellSnapshot.SignedOut(
            PersistentClientAuthenticationStatus.AuthenticationFailed,
            logoutStatus));
    }

    private async Task<ClientLogoutStatus> CompleteRuntimeLogoutAsync(
        IClientAccountRuntime accountRuntime)
    {
        ClientLogoutStatus logoutStatus;
        try
        {
            logoutStatus = await accountRuntime
                .LogoutAsync(CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Logging out an account runtime did not fully converge; " +
                "errorType={ErrorType}.",
                exception.GetType().Name);
            logoutStatus = ClientLogoutStatus.RemoteFailure;
        }

        try
        {
            await accountRuntime.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Disposing a logged-out account runtime did not fully converge; " +
                "errorType={ErrorType}.",
                exception.GetType().Name);
        }

        return logoutStatus;
    }

    private bool IsCurrentRuntime(IClientAccountRuntime expectedRuntime)
    {
        lock (stateGate)
        {
            return ReferenceEquals(runtime, expectedRuntime);
        }
    }

    private void RestoreLatestActivity()
    {
        IClientAccountRuntime? activeRuntime;
        ClientActivitySnapshot activity;
        lock (stateGate)
        {
            activeRuntime = runtime;
            activity = latestActivity;
        }

        if (activeRuntime is null)
        {
            return;
        }

        try
        {
            activeRuntime.UpdateActivity(activity);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Restoring account window activity failed; errorType={ErrorType}.",
                exception.GetType().Name);
        }
    }

    private void EnsureActivationLease(IClientAccountRuntime expectedRuntime)
    {
        lock (stateGate)
        {
            if (!ReferenceEquals(runtime, expectedRuntime) || activationLease is not null)
            {
                return;
            }
        }

        IDisposable? unownedLease = activationRouter.ActivateAccount(
            expectedRuntime.Identity.Id,
            expectedRuntime.TryAuthorizeNotificationTarget);
        try
        {
            lock (stateGate)
            {
                ThrowIfStopping();
                if (ReferenceEquals(runtime, expectedRuntime) && activationLease is null)
                {
                    activationLease = unownedLease;
                    unownedLease = null;
                }
            }
        }
        finally
        {
            unownedLease?.Dispose();
        }
    }

    private void ClearActiveOwnership(
        out IDisposable? lease,
        out IClientAccountRuntime? activeRuntime)
    {
        lock (stateGate)
        {
            lease = activationLease;
            activeRuntime = runtime;
            activationLease = null;
            runtime = null;
            activeDisplayName = null;
            activeServerBaseUri = null;
        }
    }

    private void PublishStoppingSnapshot()
    {
        PublishSnapshot(new ClientAccountShellSnapshot(
            ClientAccountShellPhase.Stopping,
            AuthenticationStatus: null,
            DisplayName: null,
            ServerBaseUri: null,
            RelayCove.Shared.Realtime.ConnectionState.Disconnected,
            LastSyncStatus: null,
            LastLogoutStatus: null,
            RetryAfter: null));
    }

    private void PublishSnapshot(ClientAccountShellSnapshot value)
    {
        Volatile.Write(ref snapshot, value);
        Action<ClientAccountShellSnapshot>? handlers;
        lock (stateGate)
        {
            handlers = SnapshotChanged;
        }

        if (handlers is null)
        {
            return;
        }

        foreach (Action<ClientAccountShellSnapshot> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(value);
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    "Publishing an account shell snapshot failed; errorType={ErrorType}.",
                    exception.GetType().Name);
            }
        }
    }

    private CancellationTokenSource CreateLinkedCancellation(
        CancellationToken cancellationToken)
    {
        ThrowIfStopping();
        return CancellationTokenSource.CreateLinkedTokenSource(
            lifetimeCancellation.Token,
            cancellationToken);
    }

    private void ThrowIfStopping() =>
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref disposeStarted) != 0,
            this);

    private static string GetDeviceName() => Environment.MachineName;

    private static string GetClientVersion() =>
        typeof(ClientAccountShellCoordinator).Assembly.GetName().Version?.ToString() ??
        "1.0.0";
}
