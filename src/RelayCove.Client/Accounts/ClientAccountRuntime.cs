using Microsoft.Extensions.Logging;
using RelayCove.Client.Auth;
using RelayCove.Client.Storage;
using RelayCove.Client.Sync;
using RelayCove.Shared.Messages;
using RelayCove.Shared.Realtime;

namespace RelayCove.Client.Accounts;

internal sealed class ClientAccountRuntime : IAsyncDisposable
{
    private readonly object stateGate = new();
    private readonly ClientAuthenticationSession authenticationSession;
    private readonly IClientAccountRealtimeConnection realtimeConnection;
    private readonly IClientAccountSyncCoordinator syncCoordinator;
    private readonly IClientAccountReadThroughCoordinator readThroughCoordinator;
    private readonly IAsyncDisposable localCache;
    private readonly ClientActivityState activityState;
    private readonly ILogger<ClientAccountRuntime> logger;
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private readonly HashSet<Task> explicitFlights = [];
    private Task<ClientAccountRuntimeStartOutcome>? startTask;
    private Task<ClientLogoutStatus>? terminalTask;
    private TerminalMode terminalMode;

    internal ClientAccountRuntime(
        AccountScopeIdentity identity,
        ClientAuthenticationSession authenticationSession,
        IClientAccountRealtimeConnection realtimeConnection,
        IClientAccountSyncCoordinator syncCoordinator,
        IClientAccountReadThroughCoordinator readThroughCoordinator,
        IAsyncDisposable localCache,
        ClientActivityState activityState,
        ILogger<ClientAccountRuntime> logger)
    {
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        this.authenticationSession = authenticationSession ??
            throw new ArgumentNullException(nameof(authenticationSession));
        this.realtimeConnection = realtimeConnection ??
            throw new ArgumentNullException(nameof(realtimeConnection));
        this.syncCoordinator = syncCoordinator ??
            throw new ArgumentNullException(nameof(syncCoordinator));
        this.readThroughCoordinator = readThroughCoordinator ??
            throw new ArgumentNullException(nameof(readThroughCoordinator));
        this.localCache = localCache ?? throw new ArgumentNullException(nameof(localCache));
        this.activityState = activityState ?? throw new ArgumentNullException(nameof(activityState));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        if (!authenticationSession.IsAuthenticated ||
            authenticationSession.UserId != identity.UserId ||
            !Equals(authenticationSession.ServerBaseUri, identity.CanonicalServerBaseUri))
        {
            throw new ArgumentException(
                "The authentication session must match the account scope identity.",
                nameof(authenticationSession));
        }
    }

    public AccountScopeIdentity Identity { get; }

    public ConnectionState ConnectionState => realtimeConnection.State;

    public override string ToString() =>
        $"{nameof(ClientAccountRuntime)} {{ Identity = [REDACTED], " +
        $"ConnectionState = {ConnectionState} }}";

    public void UpdateActivity(ClientActivitySnapshot snapshot)
    {
        lock (stateGate)
        {
            ThrowIfTerminating();
            activityState.Update(snapshot);
        }
    }

    public Task<ClientAccountRuntimeStartOutcome> StartAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task<ClientAccountRuntimeStartOutcome> sharedStart;
        lock (stateGate)
        {
            ThrowIfTerminating();
            if (startTask is null)
            {
                var completion = new TaskCompletionSource<ClientAccountRuntimeStartOutcome>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                startTask = completion.Task;
                _ = CompleteStartAsync(completion);
            }

            sharedStart = startTask;
        }

        return cancellationToken.CanBeCanceled
            ? sharedStart.WaitAsync(cancellationToken)
            : sharedStart;
    }

    public Task<ClientSyncRunOutcome> TriggerSyncAsync(
        SyncReason reason,
        CancellationToken cancellationToken = default)
    {
        if (reason is not SyncReason.WindowActivated and not SyncReason.Periodic)
        {
            throw new ArgumentOutOfRangeException(
                nameof(reason),
                "Only window-activation and periodic sync may be triggered explicitly.");
        }

        TaskCompletionSource<ClientSyncRunOutcome> completion;
        CancellationToken lifetimeToken;
        lock (stateGate)
        {
            ThrowIfTerminating();
            if (startTask?.IsCompletedSuccessfully != true)
            {
                throw new InvalidOperationException(
                    "The account runtime must finish starting before sync is triggered.");
            }

            completion = new TaskCompletionSource<ClientSyncRunOutcome>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            lifetimeToken = lifetimeCancellation.Token;
            TrackExplicitFlight(completion.Task);
        }

        _ = CompleteExplicitSyncAsync(reason, lifetimeToken, completion);
        return cancellationToken.CanBeCanceled
            ? completion.Task.WaitAsync(cancellationToken)
            : completion.Task;
    }

    public Task<ClientSyncRunOutcome> RetryRealtimeAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TaskCompletionSource<ClientSyncRunOutcome> completion;
        CancellationToken lifetimeToken;
        lock (stateGate)
        {
            ThrowIfTerminating();
            if (startTask?.IsCompletedSuccessfully != true)
            {
                throw new InvalidOperationException(
                    "The account runtime must finish starting before realtime is retried.");
            }

            completion = new TaskCompletionSource<ClientSyncRunOutcome>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            lifetimeToken = lifetimeCancellation.Token;
            TrackExplicitFlight(completion.Task);
        }

        _ = CompleteRetryAsync(lifetimeToken, completion);
        return cancellationToken.CanBeCanceled
            ? completion.Task.WaitAsync(cancellationToken)
            : completion.Task;
    }

    public Task<ClientLogoutStatus> LogoutAsync(
        CancellationToken cancellationToken = default)
    {
        Task<ClientLogoutStatus> sharedTerminal;
        lock (stateGate)
        {
            if (terminalMode == TerminalMode.Dispose)
            {
                throw new ObjectDisposedException(nameof(ClientAccountRuntime));
            }

            if (terminalMode == TerminalMode.None)
            {
                terminalMode = TerminalMode.Logout;
                StartTermination(logout: true);
            }

            sharedTerminal = terminalTask!;
        }

        return cancellationToken.CanBeCanceled
            ? sharedTerminal.WaitAsync(cancellationToken)
            : sharedTerminal;
    }

    public ValueTask DisposeAsync()
    {
        Task<ClientLogoutStatus> sharedTerminal;
        lock (stateGate)
        {
            if (terminalMode == TerminalMode.None)
            {
                terminalMode = TerminalMode.Dispose;
                StartTermination(logout: false);
            }

            sharedTerminal = terminalTask!;
        }

        return new ValueTask(sharedTerminal);
    }

    private async Task CompleteStartAsync(
        TaskCompletionSource<ClientAccountRuntimeStartOutcome> completion)
    {
        try
        {
            completion.TrySetResult(await StartCoreAsync().ConfigureAwait(false));
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private void StartTermination(bool logout)
    {
        var completion = new TaskCompletionSource<ClientLogoutStatus>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        terminalTask = completion.Task;
        _ = CompleteTerminationAsync(logout, completion);
    }

    private async Task CompleteTerminationAsync(
        bool logout,
        TaskCompletionSource<ClientLogoutStatus> completion)
    {
        try
        {
            completion.TrySetResult(await TerminateAsync(logout).ConfigureAwait(false));
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private async Task<ClientAccountRuntimeStartOutcome> StartCoreAsync()
    {
        try
        {
            await realtimeConnection.StartAsync(lifetimeCancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested)
        {
            return CanceledStartOutcome();
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Initial realtime connection failed; errorType={ErrorType}.",
                exception.GetType().Name);
        }

        if (lifetimeCancellation.IsCancellationRequested)
        {
            return CanceledStartOutcome();
        }

        ClientSyncRunOutcome syncOutcome;
        try
        {
            syncOutcome = await syncCoordinator
                .TriggerAsync(SyncReason.Startup, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (ObjectDisposedException) when (lifetimeCancellation.IsCancellationRequested)
        {
            syncOutcome = CanceledSyncOutcome(SyncReason.Startup);
        }
        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested)
        {
            syncOutcome = CanceledSyncOutcome(SyncReason.Startup);
        }
        catch (Exception exception)
        {
            logger.LogError(
                "Initial account sync failed unexpectedly; errorType={ErrorType}.",
                exception.GetType().Name);
            syncOutcome = new ClientSyncRunOutcome(
                ClientSyncRunStatus.LocalCacheFailure,
                SyncReason.Startup,
                RoundsExecuted: 0);
        }

        var outcome = new ClientAccountRuntimeStartOutcome(
            realtimeConnection.State,
            syncOutcome);
        logger.LogInformation(
            "Account runtime start completed; realtimeState={RealtimeState}; " +
            "syncStatus={SyncStatus}.",
            outcome.RealtimeState,
            outcome.StartupSyncOutcome.Status);
        return outcome;
    }

    private async Task CompleteExplicitSyncAsync(
        SyncReason reason,
        CancellationToken lifetimeToken,
        TaskCompletionSource<ClientSyncRunOutcome> completion)
    {
        try
        {
            completion.TrySetResult(await RunExplicitSyncAsync(reason, lifetimeToken)
                .ConfigureAwait(false));
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private async Task CompleteRetryAsync(
        CancellationToken lifetimeToken,
        TaskCompletionSource<ClientSyncRunOutcome> completion)
    {
        try
        {
            completion.TrySetResult(await RetryRealtimeCoreAsync(lifetimeToken)
                .ConfigureAwait(false));
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private async Task<ClientSyncRunOutcome> RetryRealtimeCoreAsync(
        CancellationToken lifetimeToken)
    {
        if (lifetimeToken.IsCancellationRequested)
        {
            return CanceledSyncOutcome(SyncReason.Reconnect);
        }

        try
        {
            await realtimeConnection.StartAsync(lifetimeToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested)
        {
            return CanceledSyncOutcome(SyncReason.Reconnect);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Realtime retry failed; errorType={ErrorType}; continuing with reconnect sync.",
                exception.GetType().Name);
        }

        return await RunExplicitSyncAsync(SyncReason.Reconnect, lifetimeToken)
            .ConfigureAwait(false);
    }

    private async Task<ClientSyncRunOutcome> RunExplicitSyncAsync(
        SyncReason reason,
        CancellationToken lifetimeToken)
    {
        if (lifetimeToken.IsCancellationRequested)
        {
            return CanceledSyncOutcome(reason);
        }

        try
        {
            return await syncCoordinator.TriggerAsync(reason, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (ObjectDisposedException) when (lifetimeToken.IsCancellationRequested)
        {
            return CanceledSyncOutcome(reason);
        }
        catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested)
        {
            return CanceledSyncOutcome(reason);
        }
        catch (Exception exception)
        {
            logger.LogError(
                "Explicit account sync failed unexpectedly; reason={Reason}; errorType={ErrorType}.",
                reason,
                exception.GetType().Name);
            return new ClientSyncRunOutcome(
                ClientSyncRunStatus.LocalCacheFailure,
                reason,
                RoundsExecuted: 0);
        }
    }

    private async Task<ClientLogoutStatus> TerminateAsync(bool logout)
    {
        var failures = new List<Exception>();
        try
        {
            lifetimeCancellation.Cancel();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        await CaptureFailureAsync(() => realtimeConnection.DisposeAsync(), failures)
            .ConfigureAwait(false);
        await CaptureFailureAsync(() => syncCoordinator.DisposeAsync(), failures)
            .ConfigureAwait(false);
        await CaptureFailureAsync(() => readThroughCoordinator.DisposeAsync(), failures)
            .ConfigureAwait(false);

        Task<ClientAccountRuntimeStartOutcome>? startup;
        Task[] explicitOperations;
        lock (stateGate)
        {
            startup = startTask;
            explicitOperations = [.. explicitFlights];
        }

        if (startup is not null)
        {
            await CaptureFailureAsync(() => new ValueTask(startup), failures)
                .ConfigureAwait(false);
        }

        foreach (var explicitOperation in explicitOperations)
        {
            await CaptureFailureAsync(() => new ValueTask(explicitOperation), failures)
                .ConfigureAwait(false);
        }

        await CaptureFailureAsync(() => localCache.DisposeAsync(), failures)
            .ConfigureAwait(false);

        var logoutStatus = ClientLogoutStatus.LoggedOut;
        if (logout)
        {
            try
            {
                logoutStatus = await authenticationSession
                    .LogoutAsync(CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        await CaptureFailureAsync(() => authenticationSession.DisposeAsync(), failures)
            .ConfigureAwait(false);
        lifetimeCancellation.Dispose();

        logger.LogInformation(
            "Account runtime terminated; mode={Mode}; logoutStatus={LogoutStatus}; " +
            "cleanupFailures={CleanupFailures}.",
            logout ? TerminalMode.Logout : TerminalMode.Dispose,
            logout ? logoutStatus.ToString() : "NotRequested",
            failures.Count);

        if (failures.Count != 0)
        {
            throw new AggregateException("Account runtime cleanup failed.", failures);
        }

        return logoutStatus;
    }

    private static async Task CaptureFailureAsync(
        Func<ValueTask> operation,
        ICollection<Exception> failures)
    {
        try
        {
            await operation().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }

    private ClientAccountRuntimeStartOutcome CanceledStartOutcome() =>
        new(realtimeConnection.State, CanceledSyncOutcome(SyncReason.Startup));

    private static ClientSyncRunOutcome CanceledSyncOutcome(SyncReason reason) =>
        new(ClientSyncRunStatus.Canceled, reason, RoundsExecuted: 0);

    private void TrackExplicitFlight(Task flight)
    {
        explicitFlights.Add(flight);
        _ = flight.ContinueWith(
            static (completed, state) =>
                ((ClientAccountRuntime)state!).RemoveExplicitFlight(completed),
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void RemoveExplicitFlight(Task flight)
    {
        lock (stateGate)
        {
            explicitFlights.Remove(flight);
        }
    }

    private void ThrowIfTerminating()
    {
        if (terminalMode != TerminalMode.None)
        {
            throw new ObjectDisposedException(nameof(ClientAccountRuntime));
        }
    }

    private enum TerminalMode
    {
        None,
        Dispose,
        Logout,
    }
}
