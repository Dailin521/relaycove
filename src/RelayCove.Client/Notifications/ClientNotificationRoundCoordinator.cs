using Microsoft.Extensions.Logging;
using RelayCove.Client.Accounts;
using RelayCove.Client.Storage;
using RelayCove.Client.Sync;
using RelayCove.Shared.Messages;

namespace RelayCove.Client.Notifications;

internal sealed class ClientNotificationRoundCoordinator :
    IClientNotificationRoundCoordinator
{
    private const int RecoveryBatchSize = 200;
    private readonly object stateGate = new();
    private readonly AccountScopedLocalCache localCache;
    private readonly IClientNotificationCoordinator notificationCoordinator;
    private readonly ClientActivityState activityState;
    private readonly ILogger<ClientNotificationRoundCoordinator> logger;
    private readonly Dictionary<long, IncomingMessageSource> roundCandidates = new();
    private readonly HashSet<long> recoveryCandidates = [];
    private ClientNotificationRoundToken activeToken;
    private long generation;
    private bool roundOpen;
    private bool snapshotCommitted;
    private int disposed;

    public ClientNotificationRoundCoordinator(
        AccountScopedLocalCache localCache,
        IClientNotificationCoordinator notificationCoordinator,
        ClientActivityState activityState,
        ILogger<ClientNotificationRoundCoordinator> logger)
    {
        this.localCache = localCache ?? throw new ArgumentNullException(nameof(localCache));
        this.notificationCoordinator = notificationCoordinator ??
            throw new ArgumentNullException(nameof(notificationCoordinator));
        this.activityState = activityState ?? throw new ArgumentNullException(nameof(activityState));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public ClientNotificationRoundToken OpenRound(SyncReason reason)
    {
        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        lock (stateGate)
        {
            ThrowIfDisposed();
            if (roundOpen)
            {
                throw new InvalidOperationException(
                    "A notification round is already open for this account.");
            }

            activeToken = new ClientNotificationRoundToken(++generation, reason);
            roundOpen = true;
            snapshotCommitted = false;
            roundCandidates.Clear();
            recoveryCandidates.Clear();
            return activeToken;
        }
    }

    public async Task SnapshotCommittedAsync(
        ClientNotificationRoundToken token,
        CancellationToken cancellationToken)
    {
        lock (stateGate)
        {
            if (!IsActive(token))
            {
                return;
            }

            snapshotCommitted = true;
        }

        if (token.Reason == SyncReason.WindowActivated)
        {
            return;
        }

        var recovery = await localCache
            .ReadNotificationRecoveryBatchAsync(RecoveryBatchSize, cancellationToken)
            .ConfigureAwait(false);
        if (recovery.Status != LocalCacheOperationStatus.Ready)
        {
            logger.LogWarning(
                "Notification recovery capture was skipped; status={Status}.",
                recovery.Status);
            return;
        }

        lock (stateGate)
        {
            if (!IsActive(token))
            {
                return;
            }

            recoveryCandidates.UnionWith(recovery.MessageIds);
        }
    }

    public void SubmitSyncCandidates(
        ClientNotificationRoundToken token,
        IReadOnlyCollection<long> messageIds)
    {
        ArgumentNullException.ThrowIfNull(messageIds);
        if (messageIds.Any(messageId => messageId <= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(messageIds));
        }

        lock (stateGate)
        {
            if (!IsActive(token))
            {
                return;
            }

            foreach (var messageId in messageIds)
            {
                roundCandidates.TryAdd(messageId, IncomingMessageSource.Sync);
            }
        }
    }

    public Task SubmitRealtimeCandidateAsync(
        long messageId,
        CancellationToken cancellationToken)
    {
        if (messageId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(messageId));
        }

        lock (stateGate)
        {
            ThrowIfDisposed();
            if (roundOpen)
            {
                roundCandidates.TryAdd(messageId, IncomingMessageSource.Realtime);
                return Task.CompletedTask;
            }
        }

        return DispatchAndObserveAsync(
            [messageId],
            ClientNotificationDispatchMode.PerMessage,
            cancellationToken);
    }

    public async Task CloseRoundAsync(
        ClientNotificationRoundToken token,
        ClientSyncRunStatus status)
    {
        KeyValuePair<long, IncomingMessageSource>[] capturedRound;
        long[] capturedRecovery;
        bool capturedSnapshotCommitted;
        ClientActivitySnapshot activity;
        lock (stateGate)
        {
            if (!IsActive(token))
            {
                return;
            }

            roundOpen = false;
            generation++;
            capturedRound = [.. roundCandidates];
            capturedRecovery = [.. recoveryCandidates];
            capturedSnapshotCommitted = snapshotCommitted;
            activity = activityState.Snapshot;
            roundCandidates.Clear();
            recoveryCandidates.Clear();
            snapshotCommitted = false;
        }

        var roundIds = capturedRound.Select(candidate => candidate.Key).ToHashSet();
        var oldRecoveryIds = capturedRecovery
            .Where(messageId => !roundIds.Contains(messageId))
            .ToArray();
        var realtimeIds = capturedRound
            .Where(candidate => candidate.Value == IncomingMessageSource.Realtime)
            .Select(candidate => candidate.Key)
            .ToArray();
        var attentionGate = new ClientNotificationAttentionGate();
        if (status != ClientSyncRunStatus.Completed)
        {
            if (realtimeIds.Length != 0)
            {
                await DispatchAndObserveAsync(
                        realtimeIds,
                        ClientNotificationDispatchMode.PerMessage,
                        CancellationToken.None,
                        attentionGate)
                    .ConfigureAwait(false);
            }

            if (status != ClientSyncRunStatus.Canceled &&
                capturedSnapshotCommitted &&
                token.Reason is SyncReason.Reconnect or SyncReason.Periodic &&
                !activity.IsMainWindowForeground &&
                oldRecoveryIds.Length != 0)
            {
                await DispatchAndObserveAsync(
                        oldRecoveryIds,
                        ClientNotificationDispatchMode.Automatic,
                        CancellationToken.None,
                        attentionGate)
                    .ConfigureAwait(false);
            }

            return;
        }

        if (token.Reason == SyncReason.Startup)
        {
            await DispatchAndObserveAsync(
                    roundIds.Concat(oldRecoveryIds).ToArray(),
                    ClientNotificationDispatchMode.Summary,
                    CancellationToken.None,
                    attentionGate)
                .ConfigureAwait(false);
            return;
        }

        if (token.Reason == SyncReason.WindowActivated || activity.IsMainWindowForeground)
        {
            await DispatchAndObserveAsync(
                    roundIds.ToArray(),
                    ClientNotificationDispatchMode.None,
                    CancellationToken.None,
                    attentionGate)
                .ConfigureAwait(false);
            return;
        }

        await DispatchAndObserveAsync(
                roundIds.Concat(oldRecoveryIds).ToArray(),
                ClientNotificationDispatchMode.Automatic,
                CancellationToken.None,
                attentionGate)
            .ConfigureAwait(false);
    }

    public Task ConversationRevokedAsync(
        Guid conversationId,
        CancellationToken cancellationToken) =>
        notificationCoordinator.ConversationRevokedAsync(conversationId, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        lock (stateGate)
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            roundOpen = false;
            generation++;
            roundCandidates.Clear();
            recoveryCandidates.Clear();
        }

        await notificationCoordinator.DisposeAsync().ConfigureAwait(false);
    }

    private bool IsActive(ClientNotificationRoundToken token) =>
        roundOpen && token == activeToken;

    private async Task DispatchAndObserveAsync(
        IReadOnlyCollection<long> messageIds,
        ClientNotificationDispatchMode mode,
        CancellationToken cancellationToken,
        ClientNotificationAttentionGate? attentionGate = null)
    {
        if (messageIds.Count == 0)
        {
            return;
        }

        try
        {
            var outcome = await notificationCoordinator
                .DispatchAsync(messageIds, mode, cancellationToken, attentionGate)
                .ConfigureAwait(false);
            if (outcome.Status is ClientNotificationDispatchStatus.LocalCacheFailure or
                ClientNotificationDispatchStatus.TransientFailure)
            {
                logger.LogWarning(
                    "Notification candidate dispatch did not complete; status={Status}.",
                    outcome.Status);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Notification candidate dispatch failed unexpectedly; " +
                "errorType={ErrorType}.",
                exception.GetType().Name);
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
}
