using Microsoft.Extensions.Logging;
using RelayCove.Client.Storage;
using RelayCove.Shared.Messages;

namespace RelayCove.Client.Notifications;

internal sealed class ClientNotificationCoordinator : IClientNotificationCoordinator
{
    private const int StorageBatchSize = 1000;
    private readonly object stateGate = new();
    private readonly AccountScopedLocalCache localCache;
    private readonly IClientNotificationPlatform platform;
    private readonly Func<ClientNotificationSettingsSnapshot> settingsProvider;
    private readonly Func<Guid?> foregroundConversationIdProvider;
    private readonly ILogger<ClientNotificationCoordinator> logger;
    private readonly SemaphoreSlim dispatchGate = new(1, 1);
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private readonly HashSet<Task> activeOperations = [];
    private int disposed;

    public ClientNotificationCoordinator(
        AccountScopeIdentity identity,
        AccountScopedLocalCache localCache,
        IClientNotificationPlatform platform,
        Func<ClientNotificationSettingsSnapshot> settingsProvider,
        Func<Guid?> foregroundConversationIdProvider,
        ILogger<ClientNotificationCoordinator> logger)
    {
        ArgumentNullException.ThrowIfNull(identity);
        this.localCache = localCache ?? throw new ArgumentNullException(nameof(localCache));
        this.platform = platform ?? throw new ArgumentNullException(nameof(platform));
        this.settingsProvider = settingsProvider ??
            throw new ArgumentNullException(nameof(settingsProvider));
        this.foregroundConversationIdProvider = foregroundConversationIdProvider ??
            throw new ArgumentNullException(nameof(foregroundConversationIdProvider));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        if (!string.Equals(identity.Id, localCache.Identity.Id, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The local cache must belong to the notification coordinator account scope.",
                nameof(localCache));
        }
    }

    public Task<ClientNotificationDispatchOutcome> DispatchAsync(
        IReadOnlyCollection<long> messageIds,
        ClientNotificationDispatchMode mode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messageIds);
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var distinctIds = messageIds.Distinct().ToArray();
        if (distinctIds.Any(messageId => messageId <= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(messageIds));
        }

        Task<ClientNotificationDispatchOutcome> operation;
        lock (stateGate)
        {
            ThrowIfDisposed();
            operation = ExecuteSerializedDispatchAsync(distinctIds, mode);
            TrackOperation(operation);
        }

        return cancellationToken.CanBeCanceled
            ? operation.WaitAsync(cancellationToken)
            : operation;
    }

    public Task ConversationRevokedAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        if (conversationId == Guid.Empty)
        {
            throw new ArgumentException(
                "A revoked conversation ID cannot be empty.",
                nameof(conversationId));
        }

        cancellationToken.ThrowIfCancellationRequested();
        Task operation;
        lock (stateGate)
        {
            ThrowIfDisposed();
            operation = ExecuteSerializedClearAsync(conversationId);
            TrackOperation(operation);
        }

        return cancellationToken.CanBeCanceled
            ? operation.WaitAsync(cancellationToken)
            : operation;
    }

    public async ValueTask DisposeAsync()
    {
        Task[] operations;
        lock (stateGate)
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            lifetimeCancellation.Cancel();
            operations = [.. activeOperations];
        }

        await Task.WhenAll(operations).ConfigureAwait(false);
        dispatchGate.Dispose();
        lifetimeCancellation.Dispose();
    }

    private async Task<ClientNotificationDispatchOutcome> ExecuteSerializedDispatchAsync(
        IReadOnlyList<long> messageIds,
        ClientNotificationDispatchMode mode)
    {
        try
        {
            await dispatchGate.WaitAsync(lifetimeCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return CanceledOutcome();
        }

        try
        {
            return await DispatchCoreAsync(messageIds, mode, lifetimeCancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested)
        {
            return CanceledOutcome();
        }
        catch (Exception exception)
        {
            logger.LogError(
                "Notification dispatch failed unexpectedly; errorType={ErrorType}.",
                exception.GetType().Name);
            return new ClientNotificationDispatchOutcome(
                ClientNotificationDispatchStatus.LocalCacheFailure,
                CandidateCount: 0,
                AcceptedCount: 0,
                HandledWithoutPlatformCount: 0);
        }
        finally
        {
            dispatchGate.Release();
        }
    }

    private async Task<ClientNotificationDispatchOutcome> DispatchCoreAsync(
        IReadOnlyList<long> messageIds,
        ClientNotificationDispatchMode mode,
        CancellationToken cancellationToken)
    {
        if (messageIds.Count == 0)
        {
            return CompletedOutcome();
        }

        var settings = GetSettings();
        var platformUnavailable = mode != ClientNotificationDispatchMode.None &&
            settings.PlatformAvailability ==
                ClientNotificationPlatformAvailability.Unavailable &&
            !settings.IsDoNotDisturbEnabled;
        var suppressAll = mode == ClientNotificationDispatchMode.None ||
            settings.IsDoNotDisturbEnabled ||
            settings.PlatformAvailability == ClientNotificationPlatformAvailability.Disabled;
        var evaluation = await EvaluateInBatchesAsync(
                messageIds,
                foregroundConversationIdProvider(),
                suppressAll,
                cancellationToken)
            .ConfigureAwait(false);
        if (evaluation.Status != LocalCacheOperationStatus.Ready)
        {
            return StorageFailureOutcome(
                evaluation.Status,
                evaluation.HandledWithoutPlatformCount);
        }

        if (suppressAll || evaluation.Candidates.Count == 0)
        {
            return new ClientNotificationDispatchOutcome(
                ClientNotificationDispatchStatus.Completed,
                evaluation.Candidates.Count + evaluation.HandledWithoutPlatformCount,
                AcceptedCount: 0,
                evaluation.HandledWithoutPlatformCount);
        }

        if (platformUnavailable)
        {
            return new ClientNotificationDispatchOutcome(
                ClientNotificationDispatchStatus.TransientFailure,
                evaluation.Candidates.Count + evaluation.HandledWithoutPlatformCount,
                AcceptedCount: 0,
                evaluation.HandledWithoutPlatformCount);
        }

        var effectiveMode = mode == ClientNotificationDispatchMode.Automatic
            ? evaluation.Candidates.Count <= 10
                ? ClientNotificationDispatchMode.PerMessage
                : ClientNotificationDispatchMode.Summary
            : mode;
        return effectiveMode switch
        {
            ClientNotificationDispatchMode.PerMessage => await DispatchPerMessageAsync(
                    evaluation,
                    cancellationToken)
                .ConfigureAwait(false),
            ClientNotificationDispatchMode.Summary => await DispatchSummaryAsync(
                    evaluation,
                    cancellationToken)
                .ConfigureAwait(false),
            _ => throw new InvalidOperationException("Unexpected notification dispatch mode."),
        };
    }

    private async Task<ClientNotificationDispatchOutcome> DispatchPerMessageAsync(
        LocalNotificationCandidateBatchOutcome initialEvaluation,
        CancellationToken cancellationToken)
    {
        var acceptedCount = 0;
        var handledWithoutPlatformCount = initialEvaluation.HandledWithoutPlatformCount;
        var transientFailure = false;
        foreach (var initialCandidate in initialEvaluation.Candidates)
        {
            var settings = GetSettings();
            var platformUnavailable = settings.PlatformAvailability ==
                ClientNotificationPlatformAvailability.Unavailable &&
                !settings.IsDoNotDisturbEnabled;
            var suppress = settings.IsDoNotDisturbEnabled ||
                settings.PlatformAvailability == ClientNotificationPlatformAvailability.Disabled;
            var current = await localCache.EvaluateNotificationCandidatesAsync(
                    [initialCandidate.MessageId],
                    foregroundConversationIdProvider(),
                    suppress,
                    cancellationToken)
                .ConfigureAwait(false);
            handledWithoutPlatformCount += current.HandledWithoutPlatformCount;
            if (current.Status != LocalCacheOperationStatus.Ready)
            {
                return StorageFailureOutcome(current.Status, handledWithoutPlatformCount);
            }

            var candidate = current.Candidates.SingleOrDefault();
            if (candidate is null)
            {
                continue;
            }

            if (platformUnavailable)
            {
                transientFailure = true;
                continue;
            }

            var result = await SubmitPlatformAsync(
                    new ClientNotificationRequest(
                        NotificationPolicy.PerMessage,
                        [ToPlatformMessage(candidate)]),
                    cancellationToken)
                .ConfigureAwait(false);
            if (result.Status == ClientNotificationPlatformStatus.TransientFailure)
            {
                transientFailure = true;
                continue;
            }

            var markStatus = await localCache.MarkNotificationCandidatesHandledAsync(
                    [candidate.MessageId],
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (markStatus != LocalCacheOperationStatus.Ready)
            {
                return StorageFailureOutcome(markStatus, handledWithoutPlatformCount);
            }

            if (result.Status == ClientNotificationPlatformStatus.Accepted)
            {
                acceptedCount++;
                await ClearIfRevokedAsync(candidate.ConversationId, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                handledWithoutPlatformCount++;
            }
        }

        return new ClientNotificationDispatchOutcome(
            transientFailure
                ? ClientNotificationDispatchStatus.TransientFailure
                : ClientNotificationDispatchStatus.Completed,
            initialEvaluation.Candidates.Count +
                initialEvaluation.HandledWithoutPlatformCount,
            acceptedCount,
            handledWithoutPlatformCount);
    }

    private async Task<ClientNotificationDispatchOutcome> DispatchSummaryAsync(
        LocalNotificationCandidateBatchOutcome initialEvaluation,
        CancellationToken cancellationToken)
    {
        var settings = GetSettings();
        var platformUnavailable = settings.PlatformAvailability ==
            ClientNotificationPlatformAvailability.Unavailable &&
            !settings.IsDoNotDisturbEnabled;
        var suppress = settings.IsDoNotDisturbEnabled ||
            settings.PlatformAvailability == ClientNotificationPlatformAvailability.Disabled;
        var current = await EvaluateInBatchesAsync(
                initialEvaluation.Candidates.Select(candidate => candidate.MessageId).ToArray(),
                foregroundConversationIdProvider(),
                suppress,
                cancellationToken)
            .ConfigureAwait(false);
        var handledWithoutPlatformCount = initialEvaluation.HandledWithoutPlatformCount +
            current.HandledWithoutPlatformCount;
        if (current.Status != LocalCacheOperationStatus.Ready)
        {
            return StorageFailureOutcome(current.Status, handledWithoutPlatformCount);
        }

        if (current.Candidates.Count == 0)
        {
            return new ClientNotificationDispatchOutcome(
                ClientNotificationDispatchStatus.Completed,
                initialEvaluation.Candidates.Count +
                    initialEvaluation.HandledWithoutPlatformCount,
                AcceptedCount: 0,
                handledWithoutPlatformCount);
        }

        if (platformUnavailable)
        {
            return new ClientNotificationDispatchOutcome(
                ClientNotificationDispatchStatus.TransientFailure,
                initialEvaluation.Candidates.Count +
                    initialEvaluation.HandledWithoutPlatformCount,
                AcceptedCount: 0,
                handledWithoutPlatformCount);
        }

        var result = await SubmitPlatformAsync(
                new ClientNotificationRequest(
                    NotificationPolicy.Summary,
                    current.Candidates.Select(ToPlatformMessage).ToArray()),
                cancellationToken)
            .ConfigureAwait(false);
        if (result.Status == ClientNotificationPlatformStatus.TransientFailure)
        {
            return new ClientNotificationDispatchOutcome(
                ClientNotificationDispatchStatus.TransientFailure,
                initialEvaluation.Candidates.Count +
                    initialEvaluation.HandledWithoutPlatformCount,
                AcceptedCount: 0,
                handledWithoutPlatformCount);
        }

        var candidateIds = current.Candidates.Select(candidate => candidate.MessageId).ToArray();
        var markStatus = await localCache.MarkNotificationCandidatesHandledAsync(
                candidateIds,
                CancellationToken.None)
            .ConfigureAwait(false);
        if (markStatus != LocalCacheOperationStatus.Ready)
        {
            return StorageFailureOutcome(markStatus, handledWithoutPlatformCount);
        }

        if (result.Status == ClientNotificationPlatformStatus.PermanentlyUnavailable)
        {
            handledWithoutPlatformCount += current.Candidates.Count;
        }
        else
        {
            foreach (var conversationId in current.Candidates
                         .Select(candidate => candidate.ConversationId)
                         .Distinct())
            {
                await ClearIfRevokedAsync(conversationId, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return new ClientNotificationDispatchOutcome(
            ClientNotificationDispatchStatus.Completed,
            initialEvaluation.Candidates.Count +
                initialEvaluation.HandledWithoutPlatformCount,
            result.Status == ClientNotificationPlatformStatus.Accepted
                ? current.Candidates.Count
                : 0,
            handledWithoutPlatformCount);
    }

    private async Task<LocalNotificationCandidateBatchOutcome> EvaluateInBatchesAsync(
        IReadOnlyList<long> messageIds,
        Guid? foregroundConversationId,
        bool suppressAll,
        CancellationToken cancellationToken)
    {
        var candidates = new List<LocalNotificationCandidate>(messageIds.Count);
        var handledWithoutPlatformCount = 0;
        foreach (var batch in messageIds.Chunk(StorageBatchSize))
        {
            var outcome = await localCache.EvaluateNotificationCandidatesAsync(
                    batch,
                    foregroundConversationId,
                    suppressAll,
                    cancellationToken)
                .ConfigureAwait(false);
            handledWithoutPlatformCount += outcome.HandledWithoutPlatformCount;
            if (outcome.Status != LocalCacheOperationStatus.Ready)
            {
                return new LocalNotificationCandidateBatchOutcome(
                    outcome.Status,
                    candidates,
                    handledWithoutPlatformCount);
            }

            candidates.AddRange(outcome.Candidates);
        }

        return new LocalNotificationCandidateBatchOutcome(
            LocalCacheOperationStatus.Ready,
            candidates,
            handledWithoutPlatformCount);
    }

    private async Task<ClientNotificationPlatformResult> SubmitPlatformAsync(
        ClientNotificationRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await platform.SubmitAsync(request, cancellationToken)
                .ConfigureAwait(false);
            if (!Enum.IsDefined(result.Status))
            {
                logger.LogError("The notification platform returned an invalid status.");
                return ClientNotificationPlatformResult.TransientFailure;
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "The notification platform failed unexpectedly; errorType={ErrorType}.",
                exception.GetType().Name);
            return ClientNotificationPlatformResult.TransientFailure;
        }
    }

    private async Task ClearIfRevokedAsync(
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        if (localCache.GetNotificationConversationAccessStatus(conversationId) ==
            LocalCacheOperationStatus.Ready)
        {
            return;
        }

        await ClearPlatformConversationAsync(conversationId, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task ExecuteSerializedClearAsync(Guid conversationId)
    {
        try
        {
            await dispatchGate.WaitAsync(lifetimeCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            await ClearPlatformConversationAsync(
                    conversationId,
                    lifetimeCancellation.Token)
                .ConfigureAwait(false);
        }
        finally
        {
            dispatchGate.Release();
        }
    }

    private async Task ClearPlatformConversationAsync(
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        try
        {
            await platform.ClearConversationAsync(conversationId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Clearing notification platform state for a conversation failed; " +
                "errorType={ErrorType}.",
                exception.GetType().Name);
        }
    }

    private ClientNotificationSettingsSnapshot GetSettings()
    {
        var settings = settingsProvider() ?? throw new InvalidOperationException(
            "The notification settings provider returned no snapshot.");
        if (!Enum.IsDefined(settings.PlatformAvailability))
        {
            throw new InvalidOperationException(
                "The notification settings provider returned an invalid availability.");
        }

        return settings;
    }

    private static ClientNotificationMessage ToPlatformMessage(
        LocalNotificationCandidate candidate) =>
        new(
            candidate.MessageId,
            candidate.ConversationId,
            candidate.ConversationType,
            candidate.ConversationName,
            candidate.SenderId,
            candidate.SenderDisplayName,
            candidate.MessageType,
            candidate.Content,
            candidate.CreatedAt);

    private void TrackOperation(Task operation)
    {
        activeOperations.Add(operation);
        _ = operation.ContinueWith(
            static (completed, state) =>
                ((ClientNotificationCoordinator)state!).RemoveOperation(completed),
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void RemoveOperation(Task operation)
    {
        lock (stateGate)
        {
            activeOperations.Remove(operation);
        }
    }

    private static ClientNotificationDispatchOutcome CompletedOutcome() =>
        new(
            ClientNotificationDispatchStatus.Completed,
            CandidateCount: 0,
            AcceptedCount: 0,
            HandledWithoutPlatformCount: 0);

    private static ClientNotificationDispatchOutcome CanceledOutcome() =>
        new(
            ClientNotificationDispatchStatus.Canceled,
            CandidateCount: 0,
            AcceptedCount: 0,
            HandledWithoutPlatformCount: 0);

    private static ClientNotificationDispatchOutcome StorageFailureOutcome(
        LocalCacheOperationStatus status,
        int handledWithoutPlatformCount) =>
        new(
            status == LocalCacheOperationStatus.TransientFailure
                ? ClientNotificationDispatchStatus.TransientFailure
                : ClientNotificationDispatchStatus.LocalCacheFailure,
            CandidateCount: 0,
            AcceptedCount: 0,
            handledWithoutPlatformCount);

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
}
