using System.Net.Http;
using Microsoft.Extensions.Logging;
using RelayCove.Client.Accounts;
using RelayCove.Client.Notifications;
using RelayCove.Client.Storage;
using RelayCove.Shared.Messages;

namespace RelayCove.Client.Sync;

public sealed class ClientSyncCoordinator : IClientAccountSyncCoordinator
{
    private readonly object stateGate = new();
    private readonly AccountScopedLocalCache localCache;
    private readonly ClientSyncHttpTransport transport;
    private readonly Func<Guid?> foregroundConversationIdProvider;
    private readonly Action requestReadThroughUpload;
    private readonly IClientNotificationRoundCoordinator notificationRoundCoordinator;
    private readonly ILogger<ClientSyncCoordinator> logger;
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private Task<ClientSyncRunOutcome>? activeFlight;
    private int pendingReasonMask;
    private bool rerunActive;
    private bool startupRecovery = true;
    private bool cursorInvalid;
    private int disposed;

    public ClientSyncCoordinator(
        AccountScopeIdentity identity,
        HttpClient httpClient,
        IClientAuthenticationSession authenticationSession,
        AccountScopedLocalCache localCache,
        ILogger<ClientSyncCoordinator> logger)
        : this(
            identity,
            httpClient,
            authenticationSession,
            localCache,
            logger,
            delayAsync: null,
            nextJitter: null,
            timeProvider: null,
            foregroundConversationIdProvider: null,
            requestReadThroughUpload: null,
            notificationRoundCoordinator: null)
    {
    }

    internal ClientSyncCoordinator(
        AccountScopeIdentity identity,
        HttpClient httpClient,
        IClientAuthenticationSession authenticationSession,
        AccountScopedLocalCache localCache,
        ILogger<ClientSyncCoordinator> logger,
        Func<Guid?> foregroundConversationIdProvider,
        Action? requestReadThroughUpload = null,
        IClientNotificationRoundCoordinator? notificationRoundCoordinator = null)
        : this(
            identity,
            httpClient,
            authenticationSession,
            localCache,
            logger,
            delayAsync: null,
            nextJitter: null,
            timeProvider: null,
            foregroundConversationIdProvider,
            requestReadThroughUpload,
            notificationRoundCoordinator)
    {
    }

    internal ClientSyncCoordinator(
        AccountScopeIdentity identity,
        HttpClient httpClient,
        IClientAuthenticationSession authenticationSession,
        AccountScopedLocalCache localCache,
        ILogger<ClientSyncCoordinator> logger,
        Func<TimeSpan, CancellationToken, Task>? delayAsync,
        Func<double>? nextJitter,
        TimeProvider? timeProvider,
        Func<Guid?>? foregroundConversationIdProvider = null,
        Action? requestReadThroughUpload = null,
        IClientNotificationRoundCoordinator? notificationRoundCoordinator = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        this.localCache = localCache ?? throw new ArgumentNullException(nameof(localCache));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.foregroundConversationIdProvider = foregroundConversationIdProvider ??
            (static () => null);
        this.requestReadThroughUpload = requestReadThroughUpload ?? (static () => { });
        this.notificationRoundCoordinator = notificationRoundCoordinator ??
            new NoOpClientNotificationRoundCoordinator();
        if (!string.Equals(identity.Id, localCache.Identity.Id, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The local cache must belong to the coordinator account scope.",
                nameof(localCache));
        }

        transport = new ClientSyncHttpTransport(
            identity,
            httpClient,
            authenticationSession,
            logger,
            delayAsync,
            nextJitter,
            timeProvider);
    }

    public Task<ClientSyncRunOutcome> TriggerAsync(
        SyncReason reason,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        TaskCompletionSource<ClientSyncRunOutcome>? newFlight = null;
        Task<ClientSyncRunOutcome> flight;
        lock (stateGate)
        {
            ThrowIfDisposed();
            if (cursorInvalid)
            {
                return Task.FromResult(new ClientSyncRunOutcome(
                    ClientSyncRunStatus.CursorInvalid,
                    reason,
                    RoundsExecuted: 0));
            }

            if (activeFlight is null)
            {
                pendingReasonMask = 0;
                rerunActive = false;
                newFlight = new TaskCompletionSource<ClientSyncRunOutcome>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                activeFlight = newFlight.Task;
            }
            else if (!rerunActive)
            {
                pendingReasonMask |= ReasonMask(reason);
            }

            flight = activeFlight;
        }

        if (newFlight is not null)
        {
            _ = ExecuteFlightAsync(reason, newFlight);
        }

        return cancellationToken.CanBeCanceled
            ? flight.WaitAsync(cancellationToken)
            : flight;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        lifetimeCancellation.Cancel();
        Task<ClientSyncRunOutcome>? flight;
        lock (stateGate)
        {
            flight = activeFlight;
        }

        if (flight is not null)
        {
            await flight.ConfigureAwait(false);
        }

        await notificationRoundCoordinator.DisposeAsync().ConfigureAwait(false);
        lifetimeCancellation.Dispose();
    }

    private async Task ExecuteFlightAsync(
        SyncReason initialReason,
        TaskCompletionSource<ClientSyncRunOutcome> completion)
    {
        var roundsExecuted = 1;
        var finalReason = initialReason;
        var finalStatus = ClientSyncRunStatus.Canceled;
        try
        {
            finalStatus = await RunRoundAsync(initialReason, lifetimeCancellation.Token)
                .ConfigureAwait(false);
            SyncReason? rerunReason = null;
            lock (stateGate)
            {
                if (finalStatus == ClientSyncRunStatus.Completed)
                {
                    startupRecovery = false;
                }

                if (CanRerun(finalStatus) &&
                    pendingReasonMask != 0 &&
                    !cursorInvalid &&
                    !lifetimeCancellation.IsCancellationRequested)
                {
                    rerunReason = SelectPendingReason();
                    pendingReasonMask = 0;
                    rerunActive = true;
                }
                else
                {
                    CompleteFlight(
                        completion,
                        new ClientSyncRunOutcome(finalStatus, finalReason, roundsExecuted));
                    return;
                }
            }

            finalReason = rerunReason!.Value;
            roundsExecuted++;
            finalStatus = await RunRoundAsync(
                    finalReason,
                    lifetimeCancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested)
        {
            finalStatus = ClientSyncRunStatus.Canceled;
        }
        catch (Exception exception)
        {
            logger.LogError(
                "Client sync flight failed unexpectedly; reason={Reason}; errorType={ErrorType}.",
                finalReason,
                exception.GetType().Name);
            finalStatus = ClientSyncRunStatus.LocalCacheFailure;
        }

        lock (stateGate)
        {
            if (finalStatus == ClientSyncRunStatus.Completed)
            {
                startupRecovery = false;
            }

            CompleteFlight(
                completion,
                new ClientSyncRunOutcome(finalStatus, finalReason, roundsExecuted));
        }
    }

    private void CompleteFlight(
        TaskCompletionSource<ClientSyncRunOutcome> completion,
        ClientSyncRunOutcome outcome)
    {
        pendingReasonMask = 0;
        rerunActive = false;
        activeFlight = null;
        completion.TrySetResult(outcome);
    }

    private async Task<ClientSyncRunStatus> RunRoundAsync(
        SyncReason reason,
        CancellationToken cancellationToken)
    {
        var notificationToken = notificationRoundCoordinator.OpenRound(reason);
        var status = ClientSyncRunStatus.LocalCacheFailure;
        try
        {
            status = await RunRoundCoreAsync(reason, notificationToken, cancellationToken)
                .ConfigureAwait(false);
            return status;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            status = ClientSyncRunStatus.Canceled;
            throw;
        }
        finally
        {
            await notificationRoundCoordinator
                .CloseRoundAsync(notificationToken, status)
                .ConfigureAwait(false);
        }
    }

    private async Task<ClientSyncRunStatus> RunRoundCoreAsync(
        SyncReason reason,
        ClientNotificationRoundToken notificationToken,
        CancellationToken cancellationToken)
    {
        var snapshotResult = await transport
            .GetConversationSnapshotAsync(cancellationToken)
            .ConfigureAwait(false);
        if (snapshotResult.Status != ClientSyncHttpStatus.Success)
        {
            return MapHttpStatus(snapshotResult.Status);
        }

        var snapshotStatus = await localCache
            .ApplyAuthoritativeConversationSnapshotAsync(
                snapshotResult.Value!,
                cancellationToken)
            .ConfigureAwait(false);
        if (snapshotStatus != LocalCacheOperationStatus.Ready)
        {
            return MapLocalStatus(snapshotStatus);
        }

        await notificationRoundCoordinator
            .SnapshotCommittedAsync(notificationToken, cancellationToken)
            .ConfigureAwait(false);

        var cursorOutcome = await localCache
            .ReadLastSyncCursorAsync(cancellationToken)
            .ConfigureAwait(false);
        if (cursorOutcome.Status != LocalCacheOperationStatus.Ready ||
            !cursorOutcome.Cursor.HasValue)
        {
            return MapLocalStatus(cursorOutcome.Status);
        }

        var cursor = cursorOutcome.Cursor.Value;
        long? snapshotUpperBound = null;
        while (true)
        {
            var pageResult = await transport
                .GetSyncPageAsync(cursor, snapshotUpperBound, cancellationToken)
                .ConfigureAwait(false);
            if (pageResult.Status != ClientSyncHttpStatus.Success)
            {
                var mappedStatus = MapHttpStatus(pageResult.Status);
                if (mappedStatus == ClientSyncRunStatus.CursorInvalid)
                {
                    lock (stateGate)
                    {
                        cursorInvalid = true;
                    }
                }

                return mappedStatus;
            }

            var page = pageResult.Value!;
            var commitOutcome = await localCache
                .ApplySyncPageAsync(
                    page,
                    cursor,
                    snapshotUpperBound,
                    new LocalMessageIngestionContext(
                        IncomingMessageSource.Sync,
                        foregroundConversationIdProvider()),
                    cancellationToken)
                .ConfigureAwait(false);
            if (commitOutcome.Status != LocalCacheOperationStatus.Ready)
            {
                return MapLocalStatus(commitOutcome.Status);
            }

            notificationRoundCoordinator.SubmitSyncCandidates(
                notificationToken,
                commitOutcome.NotificationCandidateMessageIds);
            RequestReadThroughUpload();

            snapshotUpperBound ??= page.SnapshotUpperBound;
            cursor = page.NextCursor;
            if (!page.HasMore)
            {
                logger.LogInformation(
                    "Client sync round completed; reason={Reason}.",
                    reason);
                return ClientSyncRunStatus.Completed;
            }
        }
    }

    private SyncReason SelectPendingReason()
    {
        if ((pendingReasonMask & ReasonMask(SyncReason.WindowActivated)) != 0)
        {
            return SyncReason.WindowActivated;
        }

        if (startupRecovery)
        {
            return SyncReason.Startup;
        }

        return (pendingReasonMask & ReasonMask(SyncReason.Reconnect)) != 0
            ? SyncReason.Reconnect
            : SyncReason.Periodic;
    }

    private static bool CanRerun(ClientSyncRunStatus status) =>
        status is ClientSyncRunStatus.Completed or ClientSyncRunStatus.TransientFailure;

    private static int ReasonMask(SyncReason reason) => 1 << ((int)reason - 1);

    private static ClientSyncRunStatus MapHttpStatus(ClientSyncHttpStatus status) =>
        status switch
        {
            ClientSyncHttpStatus.AuthenticationRequired =>
                ClientSyncRunStatus.AuthenticationRequired,
            ClientSyncHttpStatus.TransientFailure => ClientSyncRunStatus.TransientFailure,
            ClientSyncHttpStatus.ProtocolError => ClientSyncRunStatus.ProtocolError,
            ClientSyncHttpStatus.CursorInvalid => ClientSyncRunStatus.CursorInvalid,
            ClientSyncHttpStatus.RemoteFailure => ClientSyncRunStatus.RemoteFailure,
            _ => throw new InvalidOperationException("Unexpected sync HTTP status."),
        };

    private static ClientSyncRunStatus MapLocalStatus(LocalCacheOperationStatus status) =>
        status == LocalCacheOperationStatus.ProtocolError
            ? ClientSyncRunStatus.ProtocolError
            : ClientSyncRunStatus.LocalCacheFailure;

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);

    private void RequestReadThroughUpload()
    {
        try
        {
            requestReadThroughUpload();
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Requesting read-through upload after a committed sync page failed; " +
                "errorType={ErrorType}.",
                exception.GetType().Name);
        }
    }
}
