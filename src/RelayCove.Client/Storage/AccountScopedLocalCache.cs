using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using RelayCove.Shared.Conversations;
using RelayCove.Shared.Messages;

namespace RelayCove.Client.Storage;

public sealed class AccountScopedLocalCache : IAsyncDisposable
{
    private readonly object eventGate = new();
    private const string RevocationIntentPrefix = "RevocationIntent/";
    private const string NotificationClearPendingPrefix = "NotificationClearPending/";
    private const string NotificationClearCompletedPrefix = "NotificationClearCompleted/";
    private const string NotificationStateVersionKey = "NotificationStateVersion";
    private const int CurrentNotificationStateVersion = 1;
    private const int MaxNotificationCandidateIds = 1000;
    private const int MaxOutstandingPendingMessages = 50;
    private const int WriteRetryCount = 4;
    private static readonly IReadOnlyList<MessageDto> NoMessages = Array.Empty<MessageDto>();
    private static readonly ConcurrentDictionary<string, ScopeAccessState> ProcessScopeStates =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly AccountScopeIdentity identity;
    private readonly ILogger<AccountScopedLocalCache> logger;
    private readonly ILocalCacheFaultInjector? faultInjector;
    private readonly ScopeAccessState scopeState;
    private readonly SemaphoreSlim operationGate;
    private readonly ConcurrentDictionary<Guid, byte> authorizedConversations = new();
    private readonly ConcurrentDictionary<Guid, long> authoritativeLastMessageIds = new();
    private readonly ConcurrentDictionary<Guid, byte> invalidReadThroughConversations = new();
    private readonly ConcurrentDictionary<Guid, byte> deniedConversations;
    private Action<long>? conversationStateChanged;
    private long authoritativeSnapshotRevision;
    private long conversationStateRevision;
    private int authoritativeSnapshotApplied;
    private int disposed;

    private AccountScopedLocalCache(
        AccountScopeIdentity identity,
        ILogger<AccountScopedLocalCache> logger,
        ILocalCacheFaultInjector? faultInjector)
    {
        this.identity = identity;
        this.logger = logger;
        this.faultInjector = faultInjector;
        scopeState = ProcessScopeStates.GetOrAdd(
            identity.DatabasePath,
            static _ => new ScopeAccessState());
        operationGate = scopeState.OperationGate;
        deniedConversations = scopeState.DeniedConversations;
    }

    public AccountScopeIdentity Identity => identity;

    public bool IsFatal => Volatile.Read(ref scopeState.FatalScope) != 0;

    internal long AuthoritativeSnapshotRevision =>
        Volatile.Read(ref authoritativeSnapshotRevision);

    internal event Action<long> ConversationStateChanged
    {
        add
        {
            lock (eventGate)
            {
                conversationStateChanged += value;
            }
        }
        remove
        {
            lock (eventGate)
            {
                conversationStateChanged -= value;
            }
        }
    }

    public static Task<AccountScopedLocalCache> CreateAsync(
        AccountScopeIdentity identity,
        ILogger<AccountScopedLocalCache> logger,
        CancellationToken cancellationToken = default) =>
        CreateAsync(identity, logger, null, cancellationToken);

    internal static async Task<AccountScopedLocalCache> CreateAsync(
        AccountScopeIdentity identity,
        ILogger<AccountScopedLocalCache> logger,
        ILocalCacheFaultInjector? faultInjector,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(logger);
        cancellationToken.ThrowIfCancellationRequested();

        var cache = new AccountScopedLocalCache(identity, logger, faultInjector);
        await Task.Run(cache.Initialize, CancellationToken.None).ConfigureAwait(false);
        return cache;
    }

    internal static void ResetProcessStateForTest(AccountScopeIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ProcessScopeStates.TryRemove(identity.DatabasePath, out _);
    }

    internal async Task<bool> AdoptNotificationStateAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(AdoptNotificationState).ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }
    }

    internal async Task<LocalNotificationCandidateBatchOutcome> EvaluateNotificationCandidatesAsync(
        IReadOnlyCollection<long> messageIds,
        Guid? foregroundConversationId,
        bool suppressAll,
        CancellationToken cancellationToken = default)
    {
        var validatedIds = ValidateNotificationMessageIds(messageIds, enforceBatchLimit: true);
        if (foregroundConversationId == Guid.Empty)
        {
            throw new ArgumentException(
                "A foreground conversation ID cannot be empty.",
                nameof(foregroundConversationId));
        }

        ThrowIfDisposed();
        var status = GetSyncStatus();
        if (status != LocalCacheOperationStatus.Ready)
        {
            return LocalNotificationCandidateBatchOutcome.Failure(status);
        }

        if (validatedIds.Length == 0)
        {
            return new LocalNotificationCandidateBatchOutcome(
                LocalCacheOperationStatus.Ready,
                Array.Empty<LocalNotificationCandidate>(),
                HandledWithoutPlatformCount: 0);
        }

        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => EvaluateNotificationCandidates(
                    validatedIds,
                    foregroundConversationId,
                    suppressAll,
                    cancellationToken))
                .ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }
    }

    internal async Task<LocalNotificationRecoveryBatchOutcome> ReadNotificationRecoveryBatchAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        ThrowIfDisposed();
        var status = GetSyncStatus();
        if (status != LocalCacheOperationStatus.Ready)
        {
            return LocalNotificationRecoveryBatchOutcome.Failure(status);
        }

        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => ReadNotificationRecoveryBatch(limit, cancellationToken))
                .ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }
    }

    internal async Task<LocalCacheOperationStatus> MarkNotificationCandidatesHandledAsync(
        IReadOnlyCollection<long> messageIds,
        CancellationToken cancellationToken = default)
    {
        var validatedIds = ValidateNotificationMessageIds(messageIds, enforceBatchLimit: false);
        ThrowIfDisposed();
        var status = GetSyncStatus();
        if (status != LocalCacheOperationStatus.Ready)
        {
            return status;
        }

        if (validatedIds.Length == 0)
        {
            return LocalCacheOperationStatus.Ready;
        }

        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => MarkNotificationCandidatesHandled(
                    validatedIds,
                    cancellationToken))
                .ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }
    }

    internal async Task<LocalCacheOperationStatus>
        AcknowledgeNotificationConversationClearedAsync(
            Guid conversationId,
            CancellationToken cancellationToken = default)
    {
        ValidateGuid(conversationId, nameof(conversationId));
        ThrowIfDisposed();
        if (IsFatal)
        {
            return LocalCacheOperationStatus.FatalScope;
        }

        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsFatal)
            {
                return LocalCacheOperationStatus.FatalScope;
            }

            return await Task.Run(() =>
                    AcknowledgeNotificationConversationCleared(conversationId))
                .ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }
    }

    internal LocalCacheOperationStatus GetNotificationConversationAccessStatus(
        Guid conversationId)
    {
        ValidateGuid(conversationId, nameof(conversationId));
        ThrowIfDisposed();
        var syncStatus = GetSyncStatus();
        if (syncStatus != LocalCacheOperationStatus.Ready)
        {
            return syncStatus;
        }

        return GetAccessStatus(conversationId);
    }

    internal LocalCacheOperationStatus GetNotificationOverviewAccessStatus()
    {
        ThrowIfDisposed();
        return GetSyncStatus();
    }

    public async Task<LocalCacheOperationStatus> RegisterAuthoritativeConversationAsync(
        ConversationDto conversation,
        CancellationToken cancellationToken = default)
    {
        ValidateConversation(conversation);
        ThrowIfDisposed();

        var initialStatus = GetRegistrationStatus(conversation.Id);
        if (initialStatus != LocalCacheOperationStatus.Ready)
        {
            return initialStatus;
        }

        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        LocalCacheOperationStatus outcome;
        try
        {
            outcome = await Task.Run(() => RegisterAuthoritativeConversation(conversation))
                .ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }

        if (outcome is LocalCacheOperationStatus.Ready or
            LocalCacheOperationStatus.RevokedConversation or
            LocalCacheOperationStatus.FatalScope)
        {
            PublishConversationStateChanged();
        }

        return outcome;
    }

    public async Task<LocalCacheOperationStatus> ApplyAuthoritativeConversationSnapshotAsync(
        ConversationListResponse snapshot,
        CancellationToken cancellationToken = default) =>
        (await ApplyAuthoritativeConversationSnapshotWithRevocationsAsync(
                snapshot,
                cancellationToken)
            .ConfigureAwait(false)).Status;

    internal async Task<LocalAuthoritativeConversationSnapshotOutcome>
        ApplyAuthoritativeConversationSnapshotWithRevocationsAsync(
            ConversationListResponse snapshot,
            CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!TryValidateConversationSnapshot(snapshot))
        {
            logger.LogWarning("An authoritative conversation snapshot failed protocol validation.");
            return LocalAuthoritativeConversationSnapshotOutcome.Failure(
                LocalCacheOperationStatus.ProtocolError);
        }

        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        LocalAuthoritativeConversationSnapshotOutcome outcome;
        try
        {
            outcome = await Task.Run(() => ApplyAuthoritativeConversationSnapshot(snapshot))
                .ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }

        PublishConversationStateChanged();
        return outcome;
    }

    internal async Task<LocalConversationListReadOutcome> ReadConversationListAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var status = GetSyncStatus();
        if (status != LocalCacheOperationStatus.Ready)
        {
            return LocalConversationListReadOutcome.Failure(
                status,
                Volatile.Read(ref conversationStateRevision));
        }

        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(ReadConversationList).ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<LocalSyncCursorReadOutcome> ReadLastSyncCursorAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var status = GetSyncStatus();
        if (status != LocalCacheOperationStatus.Ready)
        {
            return new LocalSyncCursorReadOutcome(status, null);
        }

        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(ReadLastSyncCursor).ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public Task<SyncPageCommitOutcome> ApplySyncPageAsync(
        SyncResponse response,
        long expectedCursor,
        long? expectedSnapshotUpperBound,
        CancellationToken cancellationToken = default) =>
        ApplySyncPageAsync(
            response,
            expectedCursor,
            expectedSnapshotUpperBound,
            LocalMessageIngestionContext.Background(IncomingMessageSource.Sync),
            cancellationToken);

    public async Task<SyncPageCommitOutcome> ApplySyncPageAsync(
        SyncResponse response,
        long expectedCursor,
        long? expectedSnapshotUpperBound,
        LocalMessageIngestionContext context,
        CancellationToken cancellationToken = default)
    {
        ValidateIngestionContext(context, IncomingMessageSource.Sync);
        ThrowIfDisposed();
        if (IsFatal)
        {
            return SyncPageOutcome(LocalCacheOperationStatus.FatalScope);
        }

        if (!TryValidateSyncPage(response, expectedCursor, expectedSnapshotUpperBound))
        {
            logger.LogWarning("A sync page failed protocol validation.");
            return SyncPageOutcome(LocalCacheOperationStatus.ProtocolError);
        }

        var status = GetSyncStatus();
        if (status != LocalCacheOperationStatus.Ready)
        {
            return SyncPageOutcome(status);
        }

        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        SyncPageCommitOutcome outcome;
        try
        {
            outcome = await Task.Run(() => ApplySyncPage(response, expectedCursor, context))
                .ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }

        if (outcome.Status is LocalCacheOperationStatus.Ready or
            LocalCacheOperationStatus.FatalScope)
        {
            PublishConversationStateChanged();
        }

        return outcome;
    }

    public async Task<LocalCacheOperationStatus> AddPendingMessageAsync(
        PendingMessage message,
        CancellationToken cancellationToken = default)
    {
        var outcome = await CreatePendingMessageAsync(message, cancellationToken)
            .ConfigureAwait(false);
        return outcome.Result is LocalPendingMessageMutationResult.CapacityExceeded or
            LocalPendingMessageMutationResult.Conflict
                ? LocalCacheOperationStatus.Conflict
                : outcome.Status;
    }

    internal async Task<LocalPendingMessageMutationOutcome> CreatePendingMessageAsync(
        PendingMessage message,
        CancellationToken cancellationToken = default)
    {
        ValidatePendingMessage(message);
        if (message.SenderId != identity.UserId)
        {
            throw new ArgumentException(
                "A pending message must belong to the current account.",
                nameof(message));
        }

        ThrowIfDisposed();

        var initialStatus = GetAccessStatus(message.ConversationId);
        if (initialStatus != LocalCacheOperationStatus.Ready)
        {
            return LocalPendingMessageMutationOutcome.Failure(initialStatus);
        }

        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        LocalPendingMessageMutationOutcome outcome;
        try
        {
            outcome = await Task.Run(() => CreatePendingMessage(message)).ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }

        if (outcome.Status == LocalCacheOperationStatus.Ready &&
            outcome.Result == LocalPendingMessageMutationResult.Created)
        {
            PublishConversationStateChanged();
        }

        return outcome;
    }

    internal async Task<LocalPendingMessageMutationOutcome> PreparePendingMessageRetryAsync(
        Guid conversationId,
        Guid clientMessageId,
        CancellationToken cancellationToken = default)
    {
        ValidateGuid(conversationId, nameof(conversationId));
        ValidateGuid(clientMessageId, nameof(clientMessageId));
        ThrowIfDisposed();
        var initialStatus = GetAccessStatus(conversationId);
        if (initialStatus != LocalCacheOperationStatus.Ready)
        {
            return LocalPendingMessageMutationOutcome.Failure(initialStatus);
        }

        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        LocalPendingMessageMutationOutcome outcome;
        try
        {
            outcome = await Task.Run(() => PreparePendingMessageRetry(
                    conversationId,
                    clientMessageId))
                .ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }

        if (outcome.Status == LocalCacheOperationStatus.Ready &&
            outcome.Result == LocalPendingMessageMutationResult.PreparedRetry)
        {
            PublishConversationStateChanged();
        }

        return outcome;
    }

    internal async Task<LocalPendingMessageMutationOutcome> MarkPendingMessageFailedAsync(
        Guid conversationId,
        Guid clientMessageId,
        CancellationToken cancellationToken = default)
    {
        ValidateGuid(conversationId, nameof(conversationId));
        ValidateGuid(clientMessageId, nameof(clientMessageId));
        ThrowIfDisposed();
        var initialStatus = GetAccessStatus(conversationId);
        if (initialStatus != LocalCacheOperationStatus.Ready)
        {
            return LocalPendingMessageMutationOutcome.Failure(initialStatus);
        }

        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        LocalPendingMessageMutationOutcome outcome;
        try
        {
            outcome = await Task.Run(() => MarkPendingMessageFailed(
                    conversationId,
                    clientMessageId))
                .ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }

        if (outcome.Status == LocalCacheOperationStatus.Ready &&
            outcome.Result == LocalPendingMessageMutationResult.MarkedFailed)
        {
            PublishConversationStateChanged();
        }

        return outcome;
    }

    public Task<LocalCacheMergeOutcome> MergeIncomingMessageAsync(
        MessageDto message,
        CancellationToken cancellationToken = default) =>
        MergeIncomingMessageAsync(
            message,
            LocalMessageIngestionContext.Background(IncomingMessageSource.Realtime),
            cancellationToken);

    public async Task<LocalCacheMergeOutcome> MergeIncomingMessageAsync(
        MessageDto message,
        LocalMessageIngestionContext context,
        CancellationToken cancellationToken = default)
    {
        ValidateIncomingMessage(message);
        ValidateIngestionContext(context);
        ThrowIfDisposed();

        var initialStatus = GetAccessStatus(message.ConversationId);
        if (initialStatus != LocalCacheOperationStatus.Ready)
        {
            return new LocalCacheMergeOutcome(initialStatus, null);
        }

        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        LocalCacheMergeOutcome outcome;
        try
        {
            outcome = await Task.Run(() => MergeIncomingMessage(message, context))
                .ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }

        if ((outcome.Status == LocalCacheOperationStatus.Ready &&
             outcome.Result != IncomingMessageMergeResult.Conflict) ||
            outcome.Status == LocalCacheOperationStatus.FatalScope)
        {
            PublishConversationStateChanged();
        }

        return outcome;
    }

    public async Task<LocalCacheReadOutcome> ReadMessagesAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        ValidateGuid(conversationId, nameof(conversationId));
        ThrowIfDisposed();

        var initialStatus = GetAccessStatus(conversationId);
        if (initialStatus != LocalCacheOperationStatus.Ready)
        {
            return new LocalCacheReadOutcome(initialStatus, NoMessages);
        }

        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => ReadMessages(conversationId)).ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }
    }

    internal async Task<LocalMessagePageReadOutcome> ReadMessagePageAsync(
        Guid conversationId,
        long? beforeMessageId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ValidateGuid(conversationId, nameof(conversationId));
        if (beforeMessageId is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(beforeMessageId));
        }

        if (limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        ThrowIfDisposed();
        var syncStatus = GetSyncStatus();
        if (syncStatus != LocalCacheOperationStatus.Ready)
        {
            return LocalMessagePageReadOutcome.Failure(syncStatus, conversationId);
        }

        var accessStatus = GetAccessStatus(conversationId);
        if (accessStatus != LocalCacheOperationStatus.Ready)
        {
            return LocalMessagePageReadOutcome.Failure(accessStatus, conversationId);
        }

        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => ReadMessagePage(
                    conversationId,
                    beforeMessageId,
                    limit))
                .ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }
    }

    internal async Task<LocalHistoryPageCommitOutcome> ApplyHistoryPageAsync(
        Guid conversationId,
        IReadOnlyList<MessageDto> messages,
        CancellationToken cancellationToken = default)
    {
        ValidateGuid(conversationId, nameof(conversationId));
        ArgumentNullException.ThrowIfNull(messages);
        if (!TryValidateHistoryMessages(conversationId, messages))
        {
            return LocalHistoryPageCommitOutcome.Failure(
                LocalCacheOperationStatus.ProtocolError);
        }

        ThrowIfDisposed();
        var syncStatus = GetSyncStatus();
        if (syncStatus != LocalCacheOperationStatus.Ready)
        {
            return LocalHistoryPageCommitOutcome.Failure(syncStatus);
        }

        var accessStatus = GetAccessStatus(conversationId);
        if (accessStatus != LocalCacheOperationStatus.Ready)
        {
            return LocalHistoryPageCommitOutcome.Failure(accessStatus);
        }

        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        LocalHistoryPageCommitOutcome outcome;
        try
        {
            outcome = await Task.Run(() => ApplyHistoryPage(conversationId, messages))
                .ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }

        if (outcome.Status is LocalCacheOperationStatus.Ready or
            LocalCacheOperationStatus.RevokedConversation or
            LocalCacheOperationStatus.FatalScope)
        {
            PublishConversationStateChanged();
        }

        return outcome;
    }

    internal async Task<LocalCacheOperationStatus> MarkConversationRenderedThroughAsync(
        Guid conversationId,
        long messageId,
        CancellationToken cancellationToken = default)
    {
        ValidateGuid(conversationId, nameof(conversationId));
        if (messageId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(messageId));
        }

        ThrowIfDisposed();
        var syncStatus = GetSyncStatus();
        if (syncStatus != LocalCacheOperationStatus.Ready)
        {
            return syncStatus;
        }

        var accessStatus = GetAccessStatus(conversationId);
        if (accessStatus != LocalCacheOperationStatus.Ready)
        {
            return accessStatus;
        }

        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        LocalCacheOperationStatus outcome;
        try
        {
            outcome = await Task.Run(() => MarkConversationRenderedThrough(
                    conversationId,
                    messageId))
                .ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }

        if (outcome is LocalCacheOperationStatus.Ready or
            LocalCacheOperationStatus.RevokedConversation or
            LocalCacheOperationStatus.FatalScope)
        {
            PublishConversationStateChanged();
        }

        return outcome;
    }

    internal async Task<LocalReadThroughBatchOutcome> ReadPendingReadThroughBatchAsync(
        Guid? afterConversationId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (afterConversationId == Guid.Empty)
        {
            throw new ArgumentException(
                "A read-through continuation conversation ID cannot be empty.",
                nameof(afterConversationId));
        }

        if (limit is < 1 or > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        ThrowIfDisposed();
        var status = GetSyncStatus();
        if (status != LocalCacheOperationStatus.Ready)
        {
            return LocalReadThroughBatchOutcome.Failure(status);
        }

        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => ReadPendingReadThroughBatch(
                    afterConversationId,
                    limit,
                    cancellationToken))
                .ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }
    }

    internal async Task<LocalCacheOperationStatus> ApplyReadThroughReceiptAsync(
        Guid conversationId,
        long requestedMessageId,
        long confirmedMessageId,
        CancellationToken cancellationToken = default)
    {
        ValidateGuid(conversationId, nameof(conversationId));
        if (requestedMessageId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requestedMessageId));
        }

        if (confirmedMessageId < requestedMessageId)
        {
            throw new ArgumentOutOfRangeException(nameof(confirmedMessageId));
        }

        ThrowIfDisposed();
        var initialStatus = GetAccessStatus(conversationId);
        if (initialStatus != LocalCacheOperationStatus.Ready)
        {
            return initialStatus;
        }

        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        LocalCacheOperationStatus outcome;
        try
        {
            outcome = await Task.Run(() => ApplyReadThroughReceipt(
                    conversationId,
                    confirmedMessageId))
                .ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }

        if (outcome is LocalCacheOperationStatus.Ready or
            LocalCacheOperationStatus.FatalScope)
        {
            PublishConversationStateChanged();
        }

        return outcome;
    }

    public async Task<LocalCacheOperationStatus> RevokeConversationAccessAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        ValidateGuid(conversationId, nameof(conversationId));
        ThrowIfDisposed();

        deniedConversations.TryAdd(conversationId, 0);
        authorizedConversations.TryRemove(conversationId, out _);
        authoritativeLastMessageIds.TryRemove(conversationId, out _);
        invalidReadThroughConversations.TryRemove(conversationId, out _);

        // Once a revocation reaches this boundary, caller cancellation must not drop
        // the durable intent or tombstone work.
        await operationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        LocalCacheOperationStatus outcome;
        try
        {
            if (IsFatal)
            {
                outcome = LocalCacheOperationStatus.FatalScope;
            }
            else
            {
                outcome = await Task.Run(() => PersistRevocation(conversationId))
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            operationGate.Release();
        }

        PublishConversationStateChanged();
        return outcome;
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref disposed, 1);
        lock (eventGate)
        {
            conversationStateChanged = null;
        }

        return ValueTask.CompletedTask;
    }

    private void PublishConversationStateChanged()
    {
        var revision = Interlocked.Increment(ref conversationStateRevision);
        Action<long>? handlers;
        lock (eventGate)
        {
            handlers = conversationStateChanged;
        }

        if (handlers is null)
        {
            return;
        }

        foreach (Action<long> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(revision);
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    "Publishing a local conversation-state change failed; " +
                    "errorType={ErrorType}.",
                    exception.GetType().Name);
            }
        }
    }

    private void MarkScopeFatal()
    {
        if (Interlocked.Exchange(ref scopeState.FatalScope, 1) != 0)
        {
            return;
        }

        ThreadPool.UnsafeQueueUserWorkItem(
            static cache => cache.PublishConversationStateChanged(),
            this,
            preferLocal: false);
    }

    private void Initialize()
    {
        Directory.CreateDirectory(identity.ScopeDirectory);
        operationGate.Wait();
        try
        {
            using (var connection = OpenConnection())
            {
                ExecuteNonQuery(connection, null, "PRAGMA journal_mode=WAL;");
                ExecuteNonQuery(connection, null, "PRAGMA synchronous=NORMAL;");
                using var versionCommand = CreateCommand(connection, null, "PRAGMA user_version;");
                var schemaVersion = Convert.ToInt32(
                    versionCommand.ExecuteScalar(),
                    CultureInfo.InvariantCulture);
                if (schemaVersion is not 0 and not 1)
                {
                    throw new InvalidDataException(
                        "The local cache schema version is not supported.");
                }

                ExecuteNonQuery(connection, null, SchemaSql);
                if (Interlocked.CompareExchange(
                        ref scopeState.PendingRecoveryCompleted,
                        1,
                        0) == 0)
                {
                    try
                    {
                        using var recoverPending = CreateCommand(connection, null, """
                            UPDATE LocalMessages
                            SET LocalSendStatus = $failed
                            WHERE ServerMessageId IS NULL
                              AND LocalSendStatus = $sending;
                            """);
                        AddParameter(
                            recoverPending,
                            "$failed",
                            (int)MessageSendStatus.Failed);
                        AddParameter(
                            recoverPending,
                            "$sending",
                            (int)MessageSendStatus.Sending);
                        recoverPending.ExecuteNonQuery();
                    }
                    catch
                    {
                        Volatile.Write(ref scopeState.PendingRecoveryCompleted, 0);
                        throw;
                    }
                }
            }

            ReplayRevocationIntents();
            LoadPersistedTombstones();
        }
        finally
        {
            operationGate.Release();
        }
    }

    private bool AdoptNotificationState()
    {
        return ExecuteWriteWithRetry((connection, transaction) =>
        {
            using var readVersion = CreateCommand(connection, transaction, """
                SELECT Value
                FROM LocalAppState
                WHERE Key = $key
                LIMIT 1;
                """);
            AddParameter(readVersion, "$key", NotificationStateVersionKey);
            var storedValue = readVersion.ExecuteScalar() as string;
            if (storedValue is not null)
            {
                if (!int.TryParse(
                        storedValue,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var storedVersion) ||
                    storedVersion is < 0 or > CurrentNotificationStateVersion)
                {
                    throw new InvalidDataException(
                        "The local notification state version is not supported.");
                }

                if (storedVersion == CurrentNotificationStateVersion)
                {
                    return TransactionResult<bool>.Rollback(false);
                }
            }

            ExecuteNonQuery(
                connection,
                transaction,
                "UPDATE LocalMessages SET IsNotificationHandled = 1 " +
                "WHERE IsNotificationHandled = 0;");
            using var writeVersion = CreateCommand(connection, transaction, """
                INSERT INTO LocalAppState (Key, Value, UpdatedAt)
                VALUES ($key, $value, $updatedAt)
                ON CONFLICT(Key) DO UPDATE SET
                    Value = excluded.Value,
                    UpdatedAt = excluded.UpdatedAt;
                """);
            AddParameter(writeVersion, "$key", NotificationStateVersionKey);
            AddParameter(
                writeVersion,
                "$value",
                CurrentNotificationStateVersion.ToString(CultureInfo.InvariantCulture));
            AddParameter(writeVersion, "$updatedAt", FormatDateTime(DateTimeOffset.UtcNow));
            writeVersion.ExecuteNonQuery();
            faultInjector?.BeforeNotificationAdoptionCommit();
            return TransactionResult<bool>.Commit(true);
        });
    }

    private LocalNotificationCandidateBatchOutcome EvaluateNotificationCandidates(
        IReadOnlyList<long> messageIds,
        Guid? foregroundConversationId,
        bool suppressAll,
        CancellationToken cancellationToken)
    {
        try
        {
            return ExecuteWriteWithRetry((connection, transaction) =>
            {
                if (!IsNotificationStateAdopted(connection, transaction))
                {
                    return TransactionResult<LocalNotificationCandidateBatchOutcome>.Rollback(
                        LocalNotificationCandidateBatchOutcome.Failure(
                            LocalCacheOperationStatus.NotificationStateNotAdopted));
                }

                var candidates = new List<LocalNotificationCandidate>(messageIds.Count);
                var handledWithoutPlatformCount = 0;
                foreach (var messageId in messageIds)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var command = CreateCommand(connection, transaction, """
                        SELECT m.ServerMessageId,
                               m.ConversationId,
                               c.Type,
                               c.Name,
                               m.SenderId,
                               m.SenderDisplayName,
                               m.Type,
                               m.Content,
                               m.CreatedAt,
                               m.IsRead,
                               m.IsNotificationHandled,
                               c.IsMuted
                        FROM LocalMessages AS m
                        INNER JOIN LocalConversations AS c ON c.Id = m.ConversationId
                        WHERE m.ServerMessageId = $messageId
                          AND NOT EXISTS (
                              SELECT 1
                              FROM RevokedConversations AS revoked
                              WHERE revoked.ConversationId = c.Id)
                          AND NOT EXISTS (
                              SELECT 1
                              FROM LocalAppState AS state
                              WHERE state.Key = $revocationIntentPrefix || c.Id)
                        LIMIT 1;
                        """);
                    AddParameter(command, "$messageId", messageId);
                    AddParameter(command, "$revocationIntentPrefix", RevocationIntentPrefix);
                    using var reader = command.ExecuteReader();
                    if (!reader.Read() || reader.GetInt64(10) != 0)
                    {
                        continue;
                    }

                    if (!Guid.TryParseExact(reader.GetString(1), "D", out var conversationId) ||
                        conversationId == Guid.Empty)
                    {
                        MarkNotificationHandled(connection, transaction, messageId);
                        handledWithoutPlatformCount++;
                        logger.LogError(
                            "A notification candidate was suppressed because its conversation identity is invalid.");
                        continue;
                    }

                    var shouldHandle = reader.GetInt64(9) != 0 ||
                        string.Equals(
                            reader.GetString(4),
                            FormatGuid(identity.UserId),
                            StringComparison.Ordinal) ||
                        reader.GetInt64(11) != 0 ||
                        foregroundConversationId == conversationId ||
                        suppressAll ||
                        deniedConversations.ContainsKey(conversationId);
                    if (shouldHandle)
                    {
                        MarkNotificationHandled(connection, transaction, messageId);
                        handledWithoutPlatformCount++;
                        continue;
                    }

                    if (!Guid.TryParseExact(reader.GetString(4), "D", out var senderId) ||
                        senderId == Guid.Empty ||
                        !Enum.IsDefined((ConversationType)reader.GetInt32(2)) ||
                        !Enum.IsDefined((MessageType)reader.GetInt32(6)) ||
                        !DateTimeOffset.TryParse(
                            reader.GetString(8),
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind,
                            out var createdAt))
                    {
                        MarkNotificationHandled(connection, transaction, messageId);
                        handledWithoutPlatformCount++;
                        logger.LogError(
                            "A notification candidate was suppressed because its local payload is invalid.");
                        continue;
                    }

                    candidates.Add(new LocalNotificationCandidate(
                        reader.GetInt64(0),
                        conversationId,
                        (ConversationType)reader.GetInt32(2),
                        reader.GetString(3),
                        senderId,
                        reader.GetString(5),
                        (MessageType)reader.GetInt32(6),
                        reader.IsDBNull(7) ? null : reader.GetString(7),
                        createdAt));
                }

                return TransactionResult<LocalNotificationCandidateBatchOutcome>.Commit(
                    new LocalNotificationCandidateBatchOutcome(
                        LocalCacheOperationStatus.Ready,
                        candidates,
                        handledWithoutPlatformCount));
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SqliteException exception) when (IsBusy(exception))
        {
            logger.LogWarning(
                "Evaluating notification candidates remained busy after bounded retries; " +
                "errorType={ExceptionType}.",
                exception.GetType().Name);
            return LocalNotificationCandidateBatchOutcome.Failure(
                LocalCacheOperationStatus.TransientFailure);
        }
        catch (Exception exception)
        {
            MarkScopeFatal();
            logger.LogCritical(
                "Local cache scope entered fatal fail-closed state while evaluating notification candidates after an error of type {ExceptionType}.",
                exception.GetType().Name);
            return LocalNotificationCandidateBatchOutcome.Failure(
                LocalCacheOperationStatus.FatalScope);
        }
    }

    private LocalNotificationRecoveryBatchOutcome ReadNotificationRecoveryBatch(
        int limit,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction(deferred: true);
            if (!IsNotificationStateAdopted(connection, transaction))
            {
                return LocalNotificationRecoveryBatchOutcome.Failure(
                    LocalCacheOperationStatus.NotificationStateNotAdopted);
            }

            using var command = CreateCommand(connection, transaction, """
                SELECT m.ServerMessageId, m.ConversationId
                FROM LocalMessages AS m
                INNER JOIN LocalConversations AS c ON c.Id = m.ConversationId
                WHERE m.ServerMessageId IS NOT NULL
                  AND m.IsNotificationHandled = 0
                  AND NOT EXISTS (
                      SELECT 1
                      FROM RevokedConversations AS revoked
                      WHERE revoked.ConversationId = c.Id)
                  AND NOT EXISTS (
                      SELECT 1
                      FROM LocalAppState AS state
                      WHERE state.Key = $revocationIntentPrefix || c.Id)
                ORDER BY m.ServerMessageId
                LIMIT $limitPlusOne;
                """);
            AddParameter(command, "$revocationIntentPrefix", RevocationIntentPrefix);
            AddParameter(command, "$limitPlusOne", limit + 1);
            var rows = new List<(long MessageId, Guid ConversationId)>(limit + 1);
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!Guid.TryParseExact(reader.GetString(1), "D", out var conversationId) ||
                        conversationId == Guid.Empty)
                    {
                        throw new InvalidDataException(
                            "The local notification recovery state contains an invalid conversation identity.");
                    }

                    rows.Add((reader.GetInt64(0), conversationId));
                }
            }

            var hasMore = rows.Count > limit;
            if (hasMore)
            {
                rows.RemoveAt(limit);
            }

            var messageIds = rows
                .Where(row => !deniedConversations.ContainsKey(row.ConversationId))
                .Select(row => row.MessageId)
                .ToArray();
            transaction.Commit();
            return new LocalNotificationRecoveryBatchOutcome(
                LocalCacheOperationStatus.Ready,
                messageIds,
                hasMore);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SqliteException exception) when (IsBusy(exception))
        {
            logger.LogWarning(
                "Reading notification recovery candidates remained busy; " +
                "errorType={ExceptionType}.",
                exception.GetType().Name);
            return LocalNotificationRecoveryBatchOutcome.Failure(
                LocalCacheOperationStatus.TransientFailure);
        }
        catch (Exception exception)
        {
            MarkScopeFatal();
            logger.LogCritical(
                "Local cache scope entered fatal fail-closed state while reading notification recovery candidates after an error of type {ExceptionType}.",
                exception.GetType().Name);
            return LocalNotificationRecoveryBatchOutcome.Failure(
                LocalCacheOperationStatus.FatalScope);
        }
    }

    private LocalCacheOperationStatus MarkNotificationCandidatesHandled(
        IReadOnlyList<long> messageIds,
        CancellationToken cancellationToken)
    {
        try
        {
            return ExecuteWriteWithRetry((connection, transaction) =>
            {
                if (!IsNotificationStateAdopted(connection, transaction))
                {
                    return TransactionResult<LocalCacheOperationStatus>.Rollback(
                        LocalCacheOperationStatus.NotificationStateNotAdopted);
                }

                foreach (var messageId in messageIds)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    MarkNotificationHandled(connection, transaction, messageId);
                }

                faultInjector?.BeforeNotificationHandledCommit();
                return TransactionResult<LocalCacheOperationStatus>.Commit(
                    LocalCacheOperationStatus.Ready);
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SqliteException exception) when (IsBusy(exception))
        {
            logger.LogWarning(
                "Marking notification candidates handled remained busy after bounded retries; " +
                "errorType={ExceptionType}.",
                exception.GetType().Name);
            return LocalCacheOperationStatus.TransientFailure;
        }
        catch (Exception exception)
        {
            MarkScopeFatal();
            logger.LogCritical(
                "Local cache scope entered fatal fail-closed state while marking notification candidates handled after an error of type {ExceptionType}.",
                exception.GetType().Name);
            return LocalCacheOperationStatus.FatalScope;
        }
    }

    private LocalCacheOperationStatus AcknowledgeNotificationConversationCleared(
        Guid conversationId)
    {
        try
        {
            return ExecuteWriteWithRetry((connection, transaction) =>
            {
                DeleteNotificationClearPending(connection, transaction, conversationId);
                if (HasTombstone(connection, transaction, conversationId))
                {
                    WriteNotificationClearCompleted(connection, transaction, conversationId);
                }
                else
                {
                    DeleteNotificationClearCompleted(connection, transaction, conversationId);
                }

                return TransactionResult<LocalCacheOperationStatus>.Commit(
                    LocalCacheOperationStatus.Ready);
            });
        }
        catch (SqliteException exception) when (IsBusy(exception))
        {
            logger.LogWarning(
                "Acknowledging notification platform cleanup remained busy after bounded " +
                "retries; errorType={ExceptionType}.",
                exception.GetType().Name);
            return LocalCacheOperationStatus.TransientFailure;
        }
        catch (Exception exception)
        {
            MarkScopeFatal();
            logger.LogCritical(
                "Local cache scope entered fatal fail-closed state while acknowledging " +
                "notification platform cleanup after an error of type {ExceptionType}.",
                exception.GetType().Name);
            return LocalCacheOperationStatus.FatalScope;
        }
    }

    private LocalAuthoritativeConversationSnapshotOutcome ApplyAuthoritativeConversationSnapshot(
        ConversationListResponse snapshot)
    {
        var conversationsById = snapshot.Conversations.ToDictionary(conversation => conversation.Id);
        Guid[] newlyMissingConversationIds = [];
        Guid[] notificationClearConversationIds = [];
        try
        {
            newlyMissingConversationIds = LoadLocalConversationIds()
                .Concat(LoadRevocationIntentIds())
                .Concat(authorizedConversations.Keys)
                .Distinct()
                .Where(conversationId => !conversationsById.ContainsKey(conversationId))
                .ToArray();
            notificationClearConversationIds = newlyMissingConversationIds;
            notificationClearConversationIds = notificationClearConversationIds
                .Concat(LoadNotificationClearConversationIds())
                .Distinct()
                .ToArray();

            foreach (var conversationId in newlyMissingConversationIds)
            {
                deniedConversations.TryAdd(conversationId, 0);
                authorizedConversations.TryRemove(conversationId, out _);
                authoritativeLastMessageIds.TryRemove(conversationId, out _);
            }

            PersistRevocationIntents(newlyMissingConversationIds);
            faultInjector?.BeforeAuthoritativeSnapshotCommit();
            ExecuteWriteWithRetry((connection, transaction) =>
            {
                foreach (var conversation in conversationsById.Values)
                {
                    DeleteRevocationState(connection, transaction, conversation.Id);
                    UpsertConversation(connection, transaction, conversation);
                }

                foreach (var conversationId in newlyMissingConversationIds)
                {
                    WriteTombstoneAndDeleteConversation(connection, transaction, conversationId);
                    DeleteRevocationIntent(connection, transaction, conversationId);
                }

                foreach (var conversationId in notificationClearConversationIds)
                {
                    WriteNotificationClearPending(connection, transaction, conversationId);
                }

                return TransactionResult<bool>.Commit(true);
            });

            foreach (var authorizedConversationId in authorizedConversations.Keys)
            {
                if (!conversationsById.ContainsKey(authorizedConversationId))
                {
                    authorizedConversations.TryRemove(authorizedConversationId, out _);
                }
            }

            foreach (var conversationId in conversationsById.Keys)
            {
                authorizedConversations.TryAdd(conversationId, 0);
                authoritativeLastMessageIds[conversationId] =
                    conversationsById[conversationId].LastMessageId;
                deniedConversations.TryRemove(conversationId, out _);
            }

            Volatile.Write(ref authoritativeSnapshotApplied, 1);
            Volatile.Write(ref scopeState.FatalScope, 0);
            Interlocked.Increment(ref authoritativeSnapshotRevision);
            invalidReadThroughConversations.Clear();
            logger.LogInformation("An authoritative conversation snapshot was committed.");
            return new LocalAuthoritativeConversationSnapshotOutcome(
                LocalCacheOperationStatus.Ready,
                notificationClearConversationIds);
        }
        catch (Exception exception)
        {
            MarkScopeFatal();
            Volatile.Write(ref authoritativeSnapshotApplied, 0);
            logger.LogCritical(
                "Local cache scope entered fatal fail-closed state after an authoritative snapshot failure of type {ExceptionType}.",
                exception.GetType().Name);
            return new LocalAuthoritativeConversationSnapshotOutcome(
                LocalCacheOperationStatus.FatalScope,
                notificationClearConversationIds);
        }
    }

    private LocalConversationListReadOutcome ReadConversationList()
    {
        var status = GetSyncStatus();
        var revision = Volatile.Read(ref conversationStateRevision);
        if (status != LocalCacheOperationStatus.Ready)
        {
            return LocalConversationListReadOutcome.Failure(status, revision);
        }

        try
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction(deferred: true);
            using var command = CreateCommand(connection, transaction, """
                SELECT c.Id,
                       c.Type,
                       c.Name,
                       c.AvatarUrl,
                       c.LastMessageId,
                       last.Type,
                       last.Content,
                       last.CreatedAt,
                       c.UnreadCount,
                       c.IsMuted,
                       c.UpdatedAt
                FROM LocalConversations AS c
                LEFT JOIN LocalMessages AS last
                  ON last.ConversationId = c.Id
                 AND last.ServerMessageId = c.LastMessageId
                WHERE NOT EXISTS (
                          SELECT 1
                          FROM RevokedConversations AS revoked
                          WHERE revoked.ConversationId = c.Id)
                  AND NOT EXISTS (
                          SELECT 1
                          FROM LocalAppState AS state
                          WHERE state.Key = $revocationIntentPrefix || c.Id)
                ORDER BY c.UpdatedAt DESC, c.Id ASC;
                """);
            AddParameter(command, "$revocationIntentPrefix", RevocationIntentPrefix);
            var conversations = new List<LocalConversationListItem>();
            long totalUnreadCount = 0;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                try
                {
                    if (!Guid.TryParseExact(reader.GetString(0), "D", out var conversationId) ||
                        conversationId == Guid.Empty ||
                        !Enum.IsDefined((ConversationType)reader.GetInt32(1)) ||
                        string.IsNullOrWhiteSpace(reader.GetString(2)) ||
                        reader.GetInt64(4) < 0 ||
                        reader.GetInt32(8) < 0)
                    {
                        throw new InvalidDataException(
                            "The local cache contains an invalid conversation-list row.");
                    }

                    if (deniedConversations.ContainsKey(conversationId) ||
                        !authorizedConversations.ContainsKey(conversationId))
                    {
                        continue;
                    }

                    var lastMessageType = reader.IsDBNull(5)
                        ? null
                        : (MessageType?)reader.GetInt32(5);
                    if (lastMessageType.HasValue && !Enum.IsDefined(lastMessageType.Value))
                    {
                        throw new InvalidDataException(
                            "The local cache contains an invalid last-message type.");
                    }

                    var lastMessageCreatedAt = reader.IsDBNull(7)
                        ? (DateTimeOffset?)null
                        : ParseStoredDateTime(reader.GetString(7));
                    var unreadCount = reader.GetInt32(8);
                    var item = new LocalConversationListItem(
                        conversationId,
                        (ConversationType)reader.GetInt32(1),
                        reader.GetString(2),
                        reader.IsDBNull(3) ? null : reader.GetString(3),
                        reader.GetInt64(4),
                        lastMessageType,
                        reader.IsDBNull(6) ? null : reader.GetString(6),
                        lastMessageCreatedAt,
                        unreadCount,
                        reader.GetBoolean(9),
                        ParseStoredDateTime(reader.GetString(10)));
                    conversations.Add(item);
                    totalUnreadCount = Math.Min(
                        (long)int.MaxValue,
                        totalUnreadCount + unreadCount);
                }
                catch (Exception exception) when (
                    exception is InvalidDataException or FormatException or
                        OverflowException or InvalidCastException)
                {
                    logger.LogError(
                        "A corrupt local conversation-list row was excluded; " +
                        "errorType={ErrorType}.",
                        exception.GetType().Name);
                }
            }

            return new LocalConversationListReadOutcome(
                LocalCacheOperationStatus.Ready,
                conversations.AsReadOnly(),
                (int)totalUnreadCount,
                revision);
        }
        catch (SqliteException exception) when (IsBusy(exception))
        {
            logger.LogWarning(
                "Reading the local conversation list was busy; errorType={ErrorType}.",
                exception.GetType().Name);
            return LocalConversationListReadOutcome.Failure(
                LocalCacheOperationStatus.TransientFailure,
                revision);
        }
        catch (Exception exception)
        {
            MarkScopeFatal();
            logger.LogCritical(
                "Local cache scope entered fatal fail-closed state while reading the " +
                "conversation list after an error of type {ExceptionType}.",
                exception.GetType().Name);
            return LocalConversationListReadOutcome.Failure(
                LocalCacheOperationStatus.FatalScope,
                revision);
        }
    }

    private LocalSyncCursorReadOutcome ReadLastSyncCursor()
    {
        var status = GetSyncStatus();
        if (status != LocalCacheOperationStatus.Ready)
        {
            return new LocalSyncCursorReadOutcome(status, null);
        }

        try
        {
            using var connection = OpenConnection();
            var cursor = ReadLastSyncCursor(connection, null);
            return new LocalSyncCursorReadOutcome(LocalCacheOperationStatus.Ready, cursor);
        }
        catch (Exception exception)
        {
            MarkScopeFatal();
            logger.LogCritical(
                "Local cache scope entered fatal fail-closed state while reading sync state after an error of type {ExceptionType}.",
                exception.GetType().Name);
            return new LocalSyncCursorReadOutcome(LocalCacheOperationStatus.FatalScope, null);
        }
    }

    private SyncPageCommitOutcome ApplySyncPage(
        SyncResponse response,
        long expectedCursor,
        LocalMessageIngestionContext context)
    {
        var status = GetSyncStatus();
        if (status != LocalCacheOperationStatus.Ready)
        {
            return SyncPageOutcome(status);
        }

        SyncPageCommitOutcome outcome;
        try
        {
            outcome = ExecuteWriteWithRetry((connection, transaction) =>
            {
                if (ReadLastSyncCursor(connection, transaction) != expectedCursor)
                {
                    return TransactionResult<SyncPageCommitOutcome>.Rollback(
                        SyncPageOutcome(LocalCacheOperationStatus.StaleCursor));
                }

                var mergeResults = new List<IncomingMessageMergeResult>(response.Messages.Count);
                var notificationCandidateMessageIds = new List<long>(response.Messages.Count);
                var foregroundReadThroughs = new ForegroundReadThroughAccumulator();
                foreach (var message in response.Messages)
                {
                    var mergeOutcome = MergeIncomingMessageInTransaction(
                        connection,
                        transaction,
                        message,
                        context,
                        foregroundReadThroughs);
                    if (mergeOutcome.Status != LocalCacheOperationStatus.Ready)
                    {
                        return TransactionResult<SyncPageCommitOutcome>.Rollback(
                            SyncPageOutcome(mergeOutcome.Status));
                    }

                    if (mergeOutcome.Result == IncomingMessageMergeResult.Conflict)
                    {
                        return TransactionResult<SyncPageCommitOutcome>.Rollback(
                            SyncPageOutcome(LocalCacheOperationStatus.Conflict));
                    }

                    mergeResults.Add(mergeOutcome.Result!.Value);
                    if (mergeOutcome.NotificationCandidateMessageId is { } candidateMessageId)
                    {
                        notificationCandidateMessageIds.Add(candidateMessageId);
                    }
                }

                foreach (var readThrough in foregroundReadThroughs.Values)
                {
                    AdvanceForegroundReadThrough(
                        connection,
                        transaction,
                        readThrough.LatestMessage.ConversationId,
                        readThrough.LatestMessage.Id,
                        readThrough.LatestMessage.CreatedAt,
                        readThrough.UncountedMessageIds);
                }

                WriteLastSyncCursor(connection, transaction, response.NextCursor);
                return TransactionResult<SyncPageCommitOutcome>.Commit(
                    new SyncPageCommitOutcome(
                        LocalCacheOperationStatus.Ready,
                        mergeResults,
                        response.NextCursor,
                        notificationCandidateMessageIds));
            });
        }
        catch (InvalidDataException exception)
        {
            MarkScopeFatal();
            logger.LogCritical(
                "Local cache scope entered fatal fail-closed state during sync-page attention processing after an error of type {ExceptionType}.",
                exception.GetType().Name);
            return SyncPageOutcome(LocalCacheOperationStatus.FatalScope);
        }

        if (outcome.Status == LocalCacheOperationStatus.Conflict)
        {
            logger.LogWarning("A sync page was rolled back because an immutable message payload conflicted.");
        }

        return outcome;
    }

    private LocalHistoryPageCommitOutcome ApplyHistoryPage(
        Guid conversationId,
        IReadOnlyList<MessageDto> messages)
    {
        var status = GetAccessStatus(conversationId);
        if (status != LocalCacheOperationStatus.Ready)
        {
            return LocalHistoryPageCommitOutcome.Failure(status);
        }

        try
        {
            var outcome = ExecuteWriteWithRetry((connection, transaction) =>
            {
                var databaseStatus = GetDatabaseAccessStatus(
                    connection,
                    transaction,
                    conversationId);
                if (databaseStatus != LocalCacheOperationStatus.Ready)
                {
                    return TransactionResult<LocalHistoryPageCommitOutcome>.Rollback(
                        LocalHistoryPageCommitOutcome.Failure(databaseStatus));
                }

                var mergeResults = new List<IncomingMessageMergeResult>(messages.Count);
                foreach (var message in messages)
                {
                    var mergeOutcome = MergeIncomingMessageInTransaction(
                        connection,
                        transaction,
                        message,
                        LocalMessageIngestionContext.UnobservedHistory);
                    if (mergeOutcome.Status != LocalCacheOperationStatus.Ready)
                    {
                        return TransactionResult<LocalHistoryPageCommitOutcome>.Rollback(
                            LocalHistoryPageCommitOutcome.Failure(mergeOutcome.Status));
                    }

                    if (mergeOutcome.Result == IncomingMessageMergeResult.Conflict)
                    {
                        return TransactionResult<LocalHistoryPageCommitOutcome>.Rollback(
                            LocalHistoryPageCommitOutcome.Failure(
                                LocalCacheOperationStatus.Conflict));
                    }

                    mergeResults.Add(mergeOutcome.Result!.Value);
                }

                return TransactionResult<LocalHistoryPageCommitOutcome>.Commit(
                    new LocalHistoryPageCommitOutcome(
                        LocalCacheOperationStatus.Ready,
                        mergeResults.AsReadOnly()));
            });

            if (outcome.Status == LocalCacheOperationStatus.RevokedConversation)
            {
                deniedConversations.TryAdd(conversationId, 0);
                authorizedConversations.TryRemove(conversationId, out _);
                authoritativeLastMessageIds.TryRemove(conversationId, out _);
            }

            if (outcome.Status == LocalCacheOperationStatus.Conflict)
            {
                logger.LogWarning(
                    "A history page was rolled back because an immutable message payload conflicted.");
            }

            return outcome;
        }
        catch (SqliteException exception) when (IsBusy(exception))
        {
            logger.LogWarning(
                "Applying a local history page was busy; errorType={ErrorType}.",
                exception.GetType().Name);
            return LocalHistoryPageCommitOutcome.Failure(
                LocalCacheOperationStatus.TransientFailure);
        }
        catch (Exception exception)
        {
            MarkScopeFatal();
            logger.LogCritical(
                "Local cache scope entered fatal fail-closed state while applying a history " +
                "page after an error of type {ExceptionType}.",
                exception.GetType().Name);
            return LocalHistoryPageCommitOutcome.Failure(
                LocalCacheOperationStatus.FatalScope);
        }
    }

    private LocalCacheOperationStatus RegisterAuthoritativeConversation(ConversationDto conversation)
    {
        var status = GetRegistrationStatus(conversation.Id);
        if (status != LocalCacheOperationStatus.Ready)
        {
            return status;
        }

        var result = ExecuteWriteWithRetry((connection, transaction) =>
        {
            if (HasTombstone(connection, transaction, conversation.Id) ||
                HasRevocationIntent(connection, transaction, conversation.Id))
            {
                return TransactionResult<LocalCacheOperationStatus>.Rollback(
                    LocalCacheOperationStatus.RevokedConversation);
            }

            UpsertConversation(connection, transaction, conversation);
            return TransactionResult<LocalCacheOperationStatus>.Commit(LocalCacheOperationStatus.Ready);
        });

        if (result == LocalCacheOperationStatus.RevokedConversation)
        {
            deniedConversations.TryAdd(conversation.Id, 0);
            authorizedConversations.TryRemove(conversation.Id, out _);
            authoritativeLastMessageIds.TryRemove(conversation.Id, out _);
            return result;
        }

        if (deniedConversations.ContainsKey(conversation.Id) || IsFatal)
        {
            authorizedConversations.TryRemove(conversation.Id, out _);
            authoritativeLastMessageIds.TryRemove(conversation.Id, out _);
            return GetRegistrationStatus(conversation.Id);
        }

        authorizedConversations.TryAdd(conversation.Id, 0);
        authoritativeLastMessageIds[conversation.Id] = conversation.LastMessageId;
        return LocalCacheOperationStatus.Ready;
    }

    private LocalPendingMessageMutationOutcome CreatePendingMessage(PendingMessage message)
    {
        var status = GetAccessStatus(message.ConversationId);
        if (status != LocalCacheOperationStatus.Ready)
        {
            return LocalPendingMessageMutationOutcome.Failure(status);
        }

        return ExecuteWriteWithRetry((connection, transaction) =>
        {
            var databaseStatus = GetDatabaseAccessStatus(connection, transaction, message.ConversationId);
            if (databaseStatus != LocalCacheOperationStatus.Ready)
            {
                return TransactionResult<LocalPendingMessageMutationOutcome>.Rollback(
                    LocalPendingMessageMutationOutcome.Failure(databaseStatus));
            }

            var existing = LoadMessageByClientKey(
                connection,
                transaction,
                identity.UserId,
                message.ClientMessageId);
            if (existing is not null)
            {
                if (!IsPendingRequestCompatible(connection, transaction, existing, message))
                {
                    return TransactionResult<LocalPendingMessageMutationOutcome>.Rollback(
                        new LocalPendingMessageMutationOutcome(
                            LocalCacheOperationStatus.Ready,
                            LocalPendingMessageMutationResult.Conflict));
                }

                var result = existing.ServerMessageId is null
                    ? LocalPendingMessageMutationResult.AlreadyExists
                    : LocalPendingMessageMutationResult.AlreadySent;
                var pending = existing.ServerMessageId is null
                    ? ToLocalPendingMessage(connection, transaction, existing)
                    : null;
                return TransactionResult<LocalPendingMessageMutationOutcome>.Rollback(
                    new LocalPendingMessageMutationOutcome(
                        LocalCacheOperationStatus.Ready,
                        result,
                        pending));
            }

            using (var count = CreateCommand(connection, transaction, """
                SELECT COUNT(*)
                FROM LocalMessages
                WHERE ConversationId = $conversationId
                  AND SenderId = $senderId
                  AND ServerMessageId IS NULL;
                """))
            {
                AddParameter(count, "$conversationId", FormatGuid(message.ConversationId));
                AddParameter(count, "$senderId", FormatGuid(identity.UserId));
                var outstanding = Convert.ToInt32(
                    count.ExecuteScalar(),
                    CultureInfo.InvariantCulture);
                if (outstanding >= MaxOutstandingPendingMessages)
                {
                    return TransactionResult<LocalPendingMessageMutationOutcome>.Rollback(
                        new LocalPendingMessageMutationOutcome(
                            LocalCacheOperationStatus.Ready,
                            LocalPendingMessageMutationResult.CapacityExceeded));
                }
            }

            using var command = CreateCommand(connection, transaction, """
                INSERT INTO LocalMessages (
                    ServerMessageId, ClientMessageId, ConversationId, SenderId,
                    SenderDisplayName, Type, Content, ReplyToMessageId, CreatedAt,
                    IsRead, IsNotificationHandled, LocalSendStatus)
                VALUES (
                    NULL, $clientMessageId, $conversationId, $senderId,
                    $senderDisplayName, $type, $content, $replyToMessageId, $createdAt,
                    1, 1, $sendStatus)
                """);
            AddMessageParameters(command, message);
            if (command.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException(
                    "Creating a pending message did not insert exactly one row.");
            }

            var localId = GetLastInsertRowId(connection, transaction);
            InsertMentions(connection, transaction, localId, message.MentionUserIds);
            return TransactionResult<LocalPendingMessageMutationOutcome>.Commit(
                new LocalPendingMessageMutationOutcome(
                    LocalCacheOperationStatus.Ready,
                    LocalPendingMessageMutationResult.Created,
                    new LocalPendingMessage(
                        localId,
                        message.ClientMessageId,
                        message.ConversationId,
                        message.SenderId,
                        message.SenderDisplayName,
                        message.Type,
                        message.Content,
                        message.ReplyToMessageId,
                        message.MentionUserIds.ToArray(),
                        message.CreatedAt,
                        MessageSendStatus.Sending)));
        });
    }

    private LocalPendingMessageMutationOutcome PreparePendingMessageRetry(
        Guid conversationId,
        Guid clientMessageId) =>
        MutatePendingMessage(
            conversationId,
            clientMessageId,
            MessageSendStatus.Failed,
            MessageSendStatus.Sending,
            LocalPendingMessageMutationResult.PreparedRetry);

    private LocalPendingMessageMutationOutcome MarkPendingMessageFailed(
        Guid conversationId,
        Guid clientMessageId) =>
        MutatePendingMessage(
            conversationId,
            clientMessageId,
            MessageSendStatus.Sending,
            MessageSendStatus.Failed,
            LocalPendingMessageMutationResult.MarkedFailed);

    private LocalPendingMessageMutationOutcome MutatePendingMessage(
        Guid conversationId,
        Guid clientMessageId,
        MessageSendStatus expectedStatus,
        MessageSendStatus nextStatus,
        LocalPendingMessageMutationResult successResult)
    {
        var status = GetAccessStatus(conversationId);
        if (status != LocalCacheOperationStatus.Ready)
        {
            return LocalPendingMessageMutationOutcome.Failure(status);
        }

        return ExecuteWriteWithRetry((connection, transaction) =>
        {
            var databaseStatus = GetDatabaseAccessStatus(connection, transaction, conversationId);
            if (databaseStatus != LocalCacheOperationStatus.Ready)
            {
                return TransactionResult<LocalPendingMessageMutationOutcome>.Rollback(
                    LocalPendingMessageMutationOutcome.Failure(databaseStatus));
            }

            var existing = LoadMessageByClientKey(
                connection,
                transaction,
                identity.UserId,
                clientMessageId);
            if (existing is null || existing.ConversationId != conversationId)
            {
                return TransactionResult<LocalPendingMessageMutationOutcome>.Rollback(
                    new LocalPendingMessageMutationOutcome(
                        LocalCacheOperationStatus.Ready,
                        LocalPendingMessageMutationResult.NotFound));
            }

            if (existing.ServerMessageId is not null ||
                existing.SendStatus == MessageSendStatus.Sent)
            {
                return TransactionResult<LocalPendingMessageMutationOutcome>.Rollback(
                    new LocalPendingMessageMutationOutcome(
                        LocalCacheOperationStatus.Ready,
                        LocalPendingMessageMutationResult.AlreadySent));
            }

            if (existing.SendStatus != expectedStatus)
            {
                return TransactionResult<LocalPendingMessageMutationOutcome>.Rollback(
                    new LocalPendingMessageMutationOutcome(
                        LocalCacheOperationStatus.Ready,
                        LocalPendingMessageMutationResult.NotRetryable,
                        ToLocalPendingMessage(connection, transaction, existing)));
            }

            using var update = CreateCommand(connection, transaction, """
                UPDATE LocalMessages
                SET LocalSendStatus = $nextStatus
                WHERE LocalId = $localId
                  AND ServerMessageId IS NULL
                  AND LocalSendStatus = $expectedStatus;
                """);
            AddParameter(update, "$nextStatus", (int)nextStatus);
            AddParameter(update, "$localId", existing.LocalId);
            AddParameter(update, "$expectedStatus", (int)expectedStatus);
            if (update.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException(
                    "Changing a pending message state did not update exactly one row.");
            }

            return TransactionResult<LocalPendingMessageMutationOutcome>.Commit(
                new LocalPendingMessageMutationOutcome(
                    LocalCacheOperationStatus.Ready,
                    successResult,
                    ToLocalPendingMessage(
                        connection,
                        transaction,
                        existing with { SendStatus = nextStatus })));
        });
    }

    private LocalCacheMergeOutcome MergeIncomingMessage(
        MessageDto message,
        LocalMessageIngestionContext context)
    {
        var status = GetAccessStatus(message.ConversationId);
        if (status != LocalCacheOperationStatus.Ready)
        {
            return new LocalCacheMergeOutcome(status, null);
        }

        LocalCacheMergeOutcome outcome;
        try
        {
            outcome = ExecuteWriteWithRetry((connection, transaction) =>
            {
                var mergeOutcome = MergeIncomingMessageInTransaction(
                    connection,
                    transaction,
                    message,
                    context);
                var shouldCommit = mergeOutcome.Status == LocalCacheOperationStatus.Ready &&
                    mergeOutcome.Result != IncomingMessageMergeResult.Conflict;
                return shouldCommit
                    ? TransactionResult<LocalCacheMergeOutcome>.Commit(mergeOutcome)
                    : TransactionResult<LocalCacheMergeOutcome>.Rollback(mergeOutcome);
            });
        }
        catch (InvalidDataException exception)
        {
            MarkScopeFatal();
            logger.LogCritical(
                "Local cache scope entered fatal fail-closed state during message attention processing after an error of type {ExceptionType}.",
                exception.GetType().Name);
            return new LocalCacheMergeOutcome(LocalCacheOperationStatus.FatalScope, null);
        }

        if (outcome.Status == LocalCacheOperationStatus.RevokedConversation)
        {
            deniedConversations.TryAdd(message.ConversationId, 0);
            authorizedConversations.TryRemove(message.ConversationId, out _);
            authoritativeLastMessageIds.TryRemove(message.ConversationId, out _);
        }

        if (outcome.Result == IncomingMessageMergeResult.Conflict)
        {
            logger.LogWarning(
                "An incoming message was rejected because its immutable payload conflicted.");
        }

        return outcome;
    }

    private LocalCacheMergeOutcome MergeIncomingMessageInTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        MessageDto message,
        LocalMessageIngestionContext context,
        ForegroundReadThroughAccumulator? foregroundReadThroughs = null)
    {
        var databaseStatus = GetDatabaseAccessStatus(connection, transaction, message.ConversationId);
        if (databaseStatus != LocalCacheOperationStatus.Ready)
        {
            if (databaseStatus == LocalCacheOperationStatus.RevokedConversation)
            {
                deniedConversations.TryAdd(message.ConversationId, 0);
                authorizedConversations.TryRemove(message.ConversationId, out _);
                authoritativeLastMessageIds.TryRemove(message.ConversationId, out _);
            }

            return new LocalCacheMergeOutcome(databaseStatus, null);
        }

        var conversationState = LoadConversationAttentionState(
            connection,
            transaction,
            message.ConversationId,
            GetAuthoritativeLastMessageId(message.ConversationId));
        var serverHit = LoadMessageByServerId(connection, transaction, message.Id);
        var keyHit = LoadMessageByClientKey(
            connection,
            transaction,
            message.SenderId,
            message.ClientMessageId);

        if (serverHit is not null)
        {
            var isDuplicate = keyHit is not null &&
                serverHit.LocalId == keyHit.LocalId &&
                IsExactMatch(connection, transaction, serverHit, message);
            var result = isDuplicate
                ? IncomingMessageMergeResult.Duplicate
                : IncomingMessageMergeResult.Conflict;
            if (isDuplicate && !string.Equals(
                    serverHit.SenderDisplayName,
                    message.SenderDisplayName,
                    StringComparison.Ordinal))
            {
                RefreshSenderDisplayName(
                    connection,
                    transaction,
                    serverHit.LocalId,
                    message.SenderDisplayName);
            }

            var candidateMessageId = result == IncomingMessageMergeResult.Conflict
                ? null
                : ApplyMessageAttentionEffects(
                    connection,
                    transaction,
                    message,
                    context,
                    conversationState,
                    result,
                    foregroundReadThroughs);
            return new LocalCacheMergeOutcome(
                LocalCacheOperationStatus.Ready,
                result,
                candidateMessageId);
        }

        if (keyHit is not null)
        {
            if (keyHit.ServerMessageId is not null ||
                !IsPendingCompatible(connection, transaction, keyHit, message))
            {
                return new LocalCacheMergeOutcome(
                    LocalCacheOperationStatus.Ready,
                    IncomingMessageMergeResult.Conflict);
            }

            using var promote = CreateCommand(connection, transaction, """
                UPDATE LocalMessages
                SET ServerMessageId = $serverMessageId,
                    SenderDisplayName = $senderDisplayName,
                    CreatedAt = $createdAt,
                    LocalSendStatus = $sendStatus
                WHERE LocalId = $localId AND ServerMessageId IS NULL;
                """);
            AddParameter(promote, "$serverMessageId", message.Id);
            AddParameter(promote, "$senderDisplayName", message.SenderDisplayName);
            AddParameter(promote, "$createdAt", FormatDateTime(message.CreatedAt));
            AddParameter(promote, "$sendStatus", (int)MessageSendStatus.Sent);
            AddParameter(promote, "$localId", keyHit.LocalId);
            if (promote.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException("Pending message promotion did not update exactly one row.");
            }

            var result = IncomingMessageMergeResult.PendingPromoted;
            var candidateMessageId = ApplyMessageAttentionEffects(
                connection,
                transaction,
                message,
                context,
                conversationState,
                result,
                foregroundReadThroughs);
            return new LocalCacheMergeOutcome(
                LocalCacheOperationStatus.Ready,
                result,
                candidateMessageId);
        }

        using var insert = CreateCommand(connection, transaction, """
            INSERT INTO LocalMessages (
                ServerMessageId, ClientMessageId, ConversationId, SenderId,
                SenderDisplayName, Type, Content, ReplyToMessageId, CreatedAt, LocalSendStatus)
            VALUES (
                $serverMessageId, $clientMessageId, $conversationId, $senderId,
                $senderDisplayName, $type, $content, $replyToMessageId, $createdAt, $sendStatus);
            """);
        AddMessageParameters(insert, message);
        insert.ExecuteNonQuery();
        InsertMentions(
            connection,
            transaction,
            GetLastInsertRowId(connection, transaction),
            message.MentionUserIds);
        var insertedResult = IncomingMessageMergeResult.Inserted;
        var insertedCandidateMessageId = ApplyMessageAttentionEffects(
            connection,
            transaction,
            message,
            context,
            conversationState,
            insertedResult,
            foregroundReadThroughs);
        return new LocalCacheMergeOutcome(
            LocalCacheOperationStatus.Ready,
            insertedResult,
            insertedCandidateMessageId);
    }

    private long? ApplyMessageAttentionEffects(
        SqliteConnection connection,
        SqliteTransaction transaction,
        MessageDto message,
        LocalMessageIngestionContext context,
        ConversationAttentionState conversationState,
        IncomingMessageMergeResult mergeResult,
        ForegroundReadThroughAccumulator? foregroundReadThroughs)
    {
        var liveSource = context.Source is IncomingMessageSource.Realtime or
            IncomingMessageSource.Sync;
        var ownMessage = message.SenderId == identity.UserId;
        var atOrBelowReadBoundary = message.Id <= conversationState.LastReadMessageId;
        var foregroundLiveMessage = liveSource &&
            context.IsForegroundConversation(message.ConversationId);
        var historyObservationConfirmed =
            context.Source != IncomingMessageSource.History ||
            context.IsHistoryObservationConfirmed;
        var suppressNotification = ownMessage ||
            atOrBelowReadBoundary ||
            conversationState.IsMuted ||
            foregroundLiveMessage ||
            context.Source is IncomingMessageSource.History or
                IncomingMessageSource.SendResponse ||
            mergeResult == IncomingMessageMergeResult.PendingPromoted;

        if (foregroundLiveMessage && message.Id > conversationState.LastReadMessageId)
        {
            if (foregroundReadThroughs is null)
            {
                var uncountedMessageIds =
                    mergeResult == IncomingMessageMergeResult.Inserted &&
                    message.Id > conversationState.AuthoritativeLastMessageId
                        ? new[] { message.Id }
                        : Array.Empty<long>();
                AdvanceForegroundReadThrough(
                    connection,
                    transaction,
                    message.ConversationId,
                    message.Id,
                    message.CreatedAt,
                    uncountedMessageIds);
            }
            else
            {
                foregroundReadThroughs.Observe(
                    message,
                    conversationState,
                    mergeResult);
            }
        }
        else
        {
            var markRead = ownMessage || atOrBelowReadBoundary ||
                mergeResult == IncomingMessageMergeResult.PendingPromoted ||
                (context.Source == IncomingMessageSource.History &&
                 historyObservationConfirmed);
            var consumeObservedUnread =
                context.Source == IncomingMessageSource.History &&
                historyObservationConfirmed &&
                !ownMessage &&
                !atOrBelowReadBoundary &&
                (mergeResult != IncomingMessageMergeResult.Inserted ||
                    message.Id <= conversationState.AuthoritativeLastMessageId) &&
                IsMessageUnread(connection, transaction, message.Id);
            UpdateMessageAttention(
                connection,
                transaction,
                message.Id,
                markRead,
                suppressNotification);

            if (consumeObservedUnread)
            {
                DecrementConversationUnread(
                    connection,
                    transaction,
                    message.ConversationId);
            }

            if (mergeResult is IncomingMessageMergeResult.Inserted or
                IncomingMessageMergeResult.PendingPromoted)
            {
                UpdateConversationForArrival(
                    connection,
                    transaction,
                    message,
                    context.Source,
                    incrementUnread: mergeResult == IncomingMessageMergeResult.Inserted &&
                        liveSource &&
                        !ownMessage &&
                        !atOrBelowReadBoundary &&
                        message.Id > conversationState.AuthoritativeLastMessageId);
            }
        }

        return mergeResult == IncomingMessageMergeResult.Inserted &&
            liveSource &&
            !suppressNotification
                ? message.Id
                : null;
    }

    private static ConversationAttentionState LoadConversationAttentionState(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid conversationId,
        long authoritativeLastMessageId)
    {
        using var command = CreateCommand(connection, transaction, """
            SELECT LastMessageId, LastReadMessageId, IsMuted
            FROM LocalConversations
            WHERE Id = $conversationId;
            """);
        AddParameter(command, "$conversationId", FormatGuid(conversationId));
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidOperationException(
                "The authorized local conversation disappeared during message ingestion.");
        }

        return new ConversationAttentionState(
            reader.GetInt64(0),
            reader.GetInt64(1),
            authoritativeLastMessageId,
            reader.GetBoolean(2));
    }

    private long GetAuthoritativeLastMessageId(Guid conversationId)
    {
        if (authoritativeLastMessageIds.TryGetValue(
                conversationId,
                out var authoritativeLastMessageId))
        {
            return authoritativeLastMessageId;
        }

        throw new InvalidOperationException(
            "An authorized conversation is missing its authoritative message boundary.");
    }

    private static void UpdateMessageAttention(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long serverMessageId,
        bool markRead,
        bool markNotificationHandled)
    {
        if (!markRead && !markNotificationHandled)
        {
            return;
        }

        using var command = CreateCommand(connection, transaction, """
            UPDATE LocalMessages
            SET IsRead = CASE WHEN $markRead = 1 THEN 1 ELSE IsRead END,
                IsNotificationHandled = CASE
                    WHEN $markNotificationHandled = 1 THEN 1
                    ELSE IsNotificationHandled
                END
            WHERE ServerMessageId = $serverMessageId;
            """);
        AddParameter(command, "$markRead", markRead ? 1 : 0);
        AddParameter(
            command,
            "$markNotificationHandled",
            markNotificationHandled ? 1 : 0);
        AddParameter(command, "$serverMessageId", serverMessageId);
        command.ExecuteNonQuery();
    }

    private static bool IsMessageUnread(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long serverMessageId)
    {
        using var command = CreateCommand(connection, transaction, """
            SELECT 1
            FROM LocalMessages
            WHERE ServerMessageId = $serverMessageId AND IsRead = 0
            LIMIT 1;
            """);
        AddParameter(command, "$serverMessageId", serverMessageId);
        return command.ExecuteScalar() is not null;
    }

    private static void DecrementConversationUnread(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid conversationId)
    {
        using var command = CreateCommand(connection, transaction, """
            UPDATE LocalConversations
            SET UnreadCount = MAX(UnreadCount - 1, 0)
            WHERE Id = $conversationId;
            """);
        AddParameter(command, "$conversationId", FormatGuid(conversationId));
        command.ExecuteNonQuery();
    }

    private static void UpdateConversationForArrival(
        SqliteConnection connection,
        SqliteTransaction transaction,
        MessageDto message,
        IncomingMessageSource source,
        bool incrementUnread)
    {
        if (source == IncomingMessageSource.History)
        {
            return;
        }

        using var command = CreateCommand(connection, transaction, """
            UPDATE LocalConversations
            SET LastMessageId = MAX(LastMessageId, $serverMessageId),
                UnreadCount = UnreadCount + $unreadIncrement,
                UpdatedAt = CASE
                    WHEN LastMessageId < $serverMessageId THEN $updatedAt
                    ELSE UpdatedAt
                END
            WHERE Id = $conversationId;
            """);
        AddParameter(command, "$serverMessageId", message.Id);
        AddParameter(command, "$unreadIncrement", incrementUnread ? 1 : 0);
        AddParameter(command, "$updatedAt", FormatDateTime(message.CreatedAt));
        AddParameter(command, "$conversationId", FormatGuid(message.ConversationId));
        command.ExecuteNonQuery();
    }

    private void ApplyRenderedReadBoundary(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid conversationId,
        long messageId) =>
        ApplyObservedReadBoundary(
            connection,
            transaction,
            conversationId,
            messageId,
            messageCreatedAt: null,
            Array.Empty<long>());

    private void AdvanceForegroundReadThrough(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid conversationId,
        long messageId,
        DateTimeOffset messageCreatedAt,
        IReadOnlyCollection<long> uncountedMessageIds) =>
        ApplyObservedReadBoundary(
            connection,
            transaction,
            conversationId,
            messageId,
            messageCreatedAt,
            uncountedMessageIds);

    private void ApplyObservedReadBoundary(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid conversationId,
        long messageId,
        DateTimeOffset? messageCreatedAt,
        IReadOnlyCollection<long> uncountedMessageIds)
    {
        // A realtime ID can be ahead of unseen sync gaps. The message row is safe to
        // mark read immediately, but the contiguous conversation boundary must not
        // advance beyond the cursor already committed before this transaction.
        var committedSyncCursor = ReadLastSyncCursor(connection, transaction);
        foreach (var uncountedMessageId in uncountedMessageIds)
        {
            UpdateMessageAttention(
                connection,
                transaction,
                uncountedMessageId,
                markRead: true,
                markNotificationHandled: true);
        }

        using var countCommand = CreateCommand(connection, transaction, """
            SELECT COUNT(*)
            FROM LocalMessages
            WHERE ConversationId = $conversationId
              AND ServerMessageId > (
                  SELECT LastReadMessageId
                  FROM LocalConversations
                  WHERE Id = $conversationId)
              AND ServerMessageId <= $serverMessageId
              AND IsRead = 0
              AND SenderId <> $currentUserId;
            """);
        AddParameter(countCommand, "$conversationId", FormatGuid(conversationId));
        AddParameter(countCommand, "$serverMessageId", messageId);
        AddParameter(countCommand, "$currentUserId", FormatGuid(identity.UserId));
        var newlyReadCount = Convert.ToInt32(countCommand.ExecuteScalar());

        using (var messagesCommand = CreateCommand(connection, transaction, """
            UPDATE LocalMessages
            SET IsRead = 1,
                IsNotificationHandled = 1
            WHERE ConversationId = $conversationId
              AND ServerMessageId <= $serverMessageId
              AND (IsRead = 0 OR IsNotificationHandled = 0);
            """))
        {
            AddParameter(messagesCommand, "$conversationId", FormatGuid(conversationId));
            AddParameter(messagesCommand, "$serverMessageId", messageId);
            messagesCommand.ExecuteNonQuery();
        }

        var safeReadBoundary = ReadSafeConversationMessageId(
            connection,
            transaction,
            conversationId,
            Math.Min(messageId, committedSyncCursor));

        using var conversationCommand = CreateCommand(connection, transaction, """
            UPDATE LocalConversations
            SET LastMessageId = CASE
                    WHEN $updateArrivalMetadata = 1
                        THEN MAX(LastMessageId, $serverMessageId)
                    ELSE LastMessageId
                END,
                LastReadMessageId = MAX(LastReadMessageId, $safeReadBoundary),
                PendingReadThroughMessageId = CASE
                    WHEN $serverMessageId > LastReadMessageId
                        THEN MAX(
                            COALESCE(PendingReadThroughMessageId, 0),
                            $serverMessageId)
                    ELSE PendingReadThroughMessageId
                END,
                UnreadCount = MAX(UnreadCount - $newlyReadCount, 0),
                UpdatedAt = CASE
                    WHEN $updateArrivalMetadata = 1 AND LastMessageId < $serverMessageId
                        THEN $updatedAt
                    ELSE UpdatedAt
                END
            WHERE Id = $conversationId;
            """);
        AddParameter(conversationCommand, "$serverMessageId", messageId);
        AddParameter(conversationCommand, "$safeReadBoundary", safeReadBoundary);
        AddParameter(conversationCommand, "$newlyReadCount", newlyReadCount);
        AddParameter(
            conversationCommand,
            "$updateArrivalMetadata",
            messageCreatedAt.HasValue ? 1 : 0);
        AddParameter(
            conversationCommand,
            "$updatedAt",
            FormatDateTime(messageCreatedAt ?? DateTimeOffset.UnixEpoch));
        AddParameter(conversationCommand, "$conversationId", FormatGuid(conversationId));
        conversationCommand.ExecuteNonQuery();
    }

    private static long ReadSafeConversationMessageId(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid conversationId,
        long maximumMessageId)
    {
        if (maximumMessageId <= 0)
        {
            return 0;
        }

        using var command = CreateCommand(connection, transaction, """
            SELECT COALESCE(MAX(ServerMessageId), 0)
            FROM LocalMessages
            WHERE ConversationId = $conversationId
              AND ServerMessageId <= $maximumMessageId
              AND IsRead = 1;
            """);
        AddParameter(command, "$conversationId", FormatGuid(conversationId));
        AddParameter(command, "$maximumMessageId", maximumMessageId);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private LocalCacheReadOutcome ReadMessages(Guid conversationId)
    {
        var status = GetAccessStatus(conversationId);
        if (status != LocalCacheOperationStatus.Ready)
        {
            return new LocalCacheReadOutcome(status, NoMessages);
        }

        using var connection = OpenConnection();
        if (HasTombstone(connection, null, conversationId) ||
            HasRevocationIntent(connection, null, conversationId))
        {
            deniedConversations.TryAdd(conversationId, 0);
            authorizedConversations.TryRemove(conversationId, out _);
            authoritativeLastMessageIds.TryRemove(conversationId, out _);
            return new LocalCacheReadOutcome(LocalCacheOperationStatus.RevokedConversation, NoMessages);
        }

        using var command = CreateCommand(connection, null, """
            SELECT LocalId, ServerMessageId, ClientMessageId, ConversationId, SenderId,
                   SenderDisplayName, Type, Content, ReplyToMessageId, CreatedAt, LocalSendStatus
            FROM LocalMessages
            WHERE ConversationId = $conversationId
            ORDER BY COALESCE(ServerMessageId, 9223372036854775807), LocalId;
            """);
        AddParameter(command, "$conversationId", FormatGuid(conversationId));
        var records = new List<LocalMessageRecord>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                records.Add(ReadMessageRecord(reader));
            }
        }

        var messages = records
            .Where(record => record.ServerMessageId is not null)
            .Select(record => ToMessageDto(connection, record))
            .ToArray();
        return new LocalCacheReadOutcome(LocalCacheOperationStatus.Ready, messages);
    }

    private LocalMessagePageReadOutcome ReadMessagePage(
        Guid conversationId,
        long? beforeMessageId,
        int limit)
    {
        var syncStatus = GetSyncStatus();
        if (syncStatus != LocalCacheOperationStatus.Ready)
        {
            return LocalMessagePageReadOutcome.Failure(syncStatus, conversationId);
        }

        var accessStatus = GetAccessStatus(conversationId);
        if (accessStatus != LocalCacheOperationStatus.Ready)
        {
            return LocalMessagePageReadOutcome.Failure(accessStatus, conversationId);
        }

        try
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction(deferred: true);
            var databaseStatus = GetDatabaseAccessStatus(
                connection,
                transaction,
                conversationId);
            if (databaseStatus != LocalCacheOperationStatus.Ready)
            {
                return LocalMessagePageReadOutcome.Failure(
                    databaseStatus,
                    conversationId);
            }

            long lastReadMessageId;
            int unreadCount;
            using (var stateCommand = CreateCommand(connection, transaction, """
                SELECT LastReadMessageId, UnreadCount
                FROM LocalConversations
                WHERE Id = $conversationId
                LIMIT 1;
                """))
            {
                AddParameter(stateCommand, "$conversationId", FormatGuid(conversationId));
                using var stateReader = stateCommand.ExecuteReader();
                if (!stateReader.Read())
                {
                    throw new InvalidDataException(
                        "The local cache is missing the authorized conversation row.");
                }

                lastReadMessageId = stateReader.GetInt64(0);
                unreadCount = stateReader.GetInt32(1);
                if (lastReadMessageId < 0 || unreadCount < 0)
                {
                    throw new InvalidDataException(
                        "The local cache contains invalid conversation read state.");
                }
            }

            using var command = CreateCommand(connection, transaction, """
                SELECT LocalId, ServerMessageId, ClientMessageId, ConversationId, SenderId,
                       SenderDisplayName, Type, Content, ReplyToMessageId, CreatedAt,
                       LocalSendStatus
                FROM LocalMessages
                WHERE ConversationId = $conversationId
                  AND ServerMessageId IS NOT NULL
                  AND ($beforeMessageId IS NULL OR ServerMessageId < $beforeMessageId)
                ORDER BY ServerMessageId DESC
                LIMIT $limitPlusOne;
                """);
            AddParameter(command, "$conversationId", FormatGuid(conversationId));
            AddParameter(command, "$beforeMessageId", beforeMessageId);
            AddParameter(command, "$limitPlusOne", limit + 1);
            var records = new List<LocalMessageRecord>(limit + 1);
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    var record = ReadMessageRecord(reader);
                    if (record.ServerMessageId is null ||
                        record.ConversationId != conversationId)
                    {
                        throw new InvalidDataException(
                            "The local cache contains an invalid message-page row.");
                    }

                    records.Add(record);
                }
            }

            var hasMoreBefore = records.Count > limit;
            if (hasMoreBefore)
            {
                records.RemoveAt(records.Count - 1);
            }

            records.Reverse();
            var messages = records
                .Select(record => ToMessageDto(connection, record, transaction))
                .ToList()
                .AsReadOnly();
            var pendingMessages = beforeMessageId.HasValue
                ? Array.Empty<LocalPendingMessage>()
                : ReadPendingMessages(connection, transaction, conversationId);
            return new LocalMessagePageReadOutcome(
                LocalCacheOperationStatus.Ready,
                conversationId,
                messages,
                hasMoreBefore && messages.Count != 0 ? messages[0].Id : null,
                hasMoreBefore,
                pendingMessages,
                lastReadMessageId,
                unreadCount);
        }
        catch (SqliteException exception) when (IsBusy(exception))
        {
            logger.LogWarning(
                "Reading a local message page was busy; errorType={ErrorType}.",
                exception.GetType().Name);
            return LocalMessagePageReadOutcome.Failure(
                LocalCacheOperationStatus.TransientFailure,
                conversationId);
        }
        catch (Exception exception)
        {
            MarkScopeFatal();
            logger.LogCritical(
                "Local cache scope entered fatal fail-closed state while reading a message " +
                "page after an error of type {ExceptionType}.",
                exception.GetType().Name);
            return LocalMessagePageReadOutcome.Failure(
                LocalCacheOperationStatus.FatalScope,
                conversationId);
        }
    }

    private LocalCacheOperationStatus MarkConversationRenderedThrough(
        Guid conversationId,
        long messageId)
    {
        var status = GetAccessStatus(conversationId);
        if (status != LocalCacheOperationStatus.Ready)
        {
            return status;
        }

        try
        {
            return ExecuteWriteWithRetry((connection, transaction) =>
            {
                var databaseStatus = GetDatabaseAccessStatus(
                    connection,
                    transaction,
                    conversationId);
                if (databaseStatus != LocalCacheOperationStatus.Ready)
                {
                    return TransactionResult<LocalCacheOperationStatus>.Rollback(databaseStatus);
                }

                using var command = CreateCommand(connection, transaction, """
                    SELECT 1
                    FROM LocalMessages
                    WHERE ConversationId = $conversationId
                      AND ServerMessageId = $messageId
                    LIMIT 1;
                    """);
                AddParameter(command, "$conversationId", FormatGuid(conversationId));
                AddParameter(command, "$messageId", messageId);
                if (command.ExecuteScalar() is null)
                {
                    return TransactionResult<LocalCacheOperationStatus>.Rollback(
                        LocalCacheOperationStatus.ProtocolError);
                }

                ApplyRenderedReadBoundary(
                    connection,
                    transaction,
                    conversationId,
                    messageId);
                return TransactionResult<LocalCacheOperationStatus>.Commit(
                    LocalCacheOperationStatus.Ready);
            });
        }
        catch (SqliteException exception) when (IsBusy(exception))
        {
            logger.LogWarning(
                "Marking a rendered message boundary was busy; errorType={ErrorType}.",
                exception.GetType().Name);
            return LocalCacheOperationStatus.TransientFailure;
        }
        catch (Exception exception)
        {
            MarkScopeFatal();
            logger.LogCritical(
                "Local cache scope entered fatal fail-closed state while marking a rendered " +
                "message boundary after an error of type {ExceptionType}.",
                exception.GetType().Name);
            return LocalCacheOperationStatus.FatalScope;
        }
    }

    private LocalReadThroughBatchOutcome ReadPendingReadThroughBatch(
        Guid? afterConversationId,
        int limit,
        CancellationToken cancellationToken)
    {
        var status = GetSyncStatus();
        if (status != LocalCacheOperationStatus.Ready)
        {
            return LocalReadThroughBatchOutcome.Failure(status);
        }

        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                faultInjector?.BeforeReadPendingReadThroughBatch();
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction(deferred: true);
                var committedCursor = ReadLastSyncCursor(connection, transaction);
                using var command = CreateCommand(connection, transaction, """
                SELECT c.Id,
                       c.PendingReadThroughMessageId,
                       EXISTS (
                           SELECT 1
                           FROM LocalMessages AS pending
                           WHERE pending.ConversationId = c.Id
                             AND pending.ServerMessageId = c.PendingReadThroughMessageId
                             AND pending.IsRead = 1
                       ) AS IsRawPendingValid,
                       (
                           SELECT MAX(candidate.ServerMessageId)
                           FROM LocalMessages AS candidate
                           WHERE candidate.ConversationId = c.Id
                             AND candidate.IsRead = 1
                             AND candidate.ServerMessageId <= MIN(
                                 c.PendingReadThroughMessageId,
                                 $committedCursor)
                             AND NOT EXISTS (
                                 SELECT 1
                                 FROM LocalMessages AS gap
                                 WHERE gap.ConversationId = c.Id
                                   AND gap.ServerMessageId > c.LastReadMessageId
                                   AND gap.ServerMessageId <= candidate.ServerMessageId
                                   AND gap.IsRead = 0)
                       ) AS SafeMessageId
                FROM LocalConversations AS c
                WHERE c.PendingReadThroughMessageId IS NOT NULL
                  AND NOT EXISTS (
                      SELECT 1
                      FROM RevokedConversations AS revoked
                      WHERE revoked.ConversationId = c.Id)
                  AND NOT EXISTS (
                      SELECT 1
                      FROM LocalAppState AS state
                      WHERE state.Key = $revocationIntentPrefix || c.Id)
                  AND ($afterConversationId IS NULL OR c.Id > $afterConversationId)
                ORDER BY c.Id
                LIMIT $limitPlusOne;
                """);
                AddParameter(command, "$committedCursor", committedCursor);
                AddParameter(command, "$revocationIntentPrefix", RevocationIntentPrefix);
                AddParameter(
                    command,
                    "$afterConversationId",
                    afterConversationId.HasValue
                        ? FormatGuid(afterConversationId.Value)
                        : null);
                AddParameter(command, "$limitPlusOne", limit + 1);
                var rows = new List<(
                    Guid ConversationId,
                    long RawPendingMessageId,
                    long? SafeMessageId)>(limit + 1);
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        if (!Guid.TryParseExact(reader.GetString(0), "D", out var conversationId) ||
                            conversationId == Guid.Empty ||
                            reader.IsDBNull(1))
                        {
                            throw new InvalidDataException(
                                "The local cache contains invalid read-through state.");
                        }

                        var rawPendingMessageId = reader.GetInt64(1);
                        if (rawPendingMessageId <= 0 || reader.GetInt64(2) != 1)
                        {
                            if (invalidReadThroughConversations.TryAdd(conversationId, 0))
                            {
                                logger.LogError(
                                    "A conversation read-through target was isolated because its local pending state is invalid.");
                            }

                            rows.Add((conversationId, rawPendingMessageId, SafeMessageId: null));
                            continue;
                        }

                        invalidReadThroughConversations.TryRemove(conversationId, out _);

                        long? safeMessageId = null;
                        if (!reader.IsDBNull(3))
                        {
                            safeMessageId = reader.GetInt64(3);
                            if (safeMessageId.Value <= 0 ||
                                safeMessageId.Value > rawPendingMessageId ||
                                safeMessageId.Value > committedCursor)
                            {
                                throw new InvalidDataException(
                                    "The local cache contains an unsafe read-through target.");
                            }
                        }

                        rows.Add((conversationId, rawPendingMessageId, safeMessageId));
                    }
                }

                var hasMore = rows.Count > limit;
                if (hasMore)
                {
                    rows.RemoveAt(limit);
                }

                var targets = rows
                    .Where(row =>
                        row.SafeMessageId.HasValue &&
                        !deniedConversations.ContainsKey(row.ConversationId))
                    .Select(row => new LocalReadThroughUploadTarget(
                        row.ConversationId,
                        row.RawPendingMessageId,
                        row.SafeMessageId!.Value))
                    .ToArray();
                transaction.Commit();

                return new LocalReadThroughBatchOutcome(
                    LocalCacheOperationStatus.Ready,
                    targets,
                    hasMore ? rows[^1].ConversationId : null,
                    Volatile.Read(ref authoritativeSnapshotRevision));
            }
            catch (SqliteException exception) when (IsBusy(exception) && attempt < WriteRetryCount)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(25 * attempt));
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (SqliteException exception) when (IsBusy(exception))
            {
                logger.LogWarning(
                    "Reading pending read-through targets remained busy after bounded retries; " +
                    "errorType={ExceptionType}.",
                    exception.GetType().Name);
                return LocalReadThroughBatchOutcome.Failure(
                    LocalCacheOperationStatus.TransientFailure,
                    Volatile.Read(ref authoritativeSnapshotRevision));
            }
            catch (Exception exception)
            {
                MarkScopeFatal();
                logger.LogCritical(
                    "Local cache scope entered fatal fail-closed state while reading pending read-through targets after an error of type {ExceptionType}.",
                    exception.GetType().Name);
                return LocalReadThroughBatchOutcome.Failure(LocalCacheOperationStatus.FatalScope);
            }
        }
    }

    private LocalCacheOperationStatus ApplyReadThroughReceipt(
        Guid conversationId,
        long confirmedMessageId)
    {
        var status = GetAccessStatus(conversationId);
        if (status != LocalCacheOperationStatus.Ready)
        {
            return status;
        }

        return ExecuteWriteWithRetry((connection, transaction) =>
        {
            var databaseStatus = GetDatabaseAccessStatus(connection, transaction, conversationId);
            if (databaseStatus != LocalCacheOperationStatus.Ready)
            {
                return TransactionResult<LocalCacheOperationStatus>.Rollback(databaseStatus);
            }

            using var countCommand = CreateCommand(connection, transaction, """
                SELECT COUNT(*)
                FROM LocalMessages
                WHERE ConversationId = $conversationId
                  AND ServerMessageId > (
                      SELECT LastReadMessageId
                      FROM LocalConversations
                      WHERE Id = $conversationId)
                  AND ServerMessageId <= $confirmedMessageId
                  AND IsRead = 0
                  AND SenderId <> $currentUserId;
                """);
            AddParameter(countCommand, "$conversationId", FormatGuid(conversationId));
            AddParameter(countCommand, "$confirmedMessageId", confirmedMessageId);
            AddParameter(countCommand, "$currentUserId", FormatGuid(identity.UserId));
            var newlyReadCount = Convert.ToInt32(countCommand.ExecuteScalar());

            using (var messagesCommand = CreateCommand(connection, transaction, """
                UPDATE LocalMessages
                SET IsRead = 1,
                    IsNotificationHandled = 1
                WHERE ConversationId = $conversationId
                  AND ServerMessageId <= $confirmedMessageId
                  AND (IsRead = 0 OR IsNotificationHandled = 0);
                """))
            {
                AddParameter(messagesCommand, "$conversationId", FormatGuid(conversationId));
                AddParameter(messagesCommand, "$confirmedMessageId", confirmedMessageId);
                messagesCommand.ExecuteNonQuery();
            }

            using var conversationCommand = CreateCommand(connection, transaction, """
                UPDATE LocalConversations
                SET LastReadMessageId = MAX(LastReadMessageId, $confirmedMessageId),
                    PendingReadThroughMessageId = CASE
                        WHEN PendingReadThroughMessageId IS NOT NULL
                             AND $confirmedMessageId >= PendingReadThroughMessageId
                            THEN NULL
                        ELSE PendingReadThroughMessageId
                    END,
                    UnreadCount = MAX(UnreadCount - $newlyReadCount, 0)
                WHERE Id = $conversationId;
                """);
            AddParameter(conversationCommand, "$confirmedMessageId", confirmedMessageId);
            AddParameter(conversationCommand, "$newlyReadCount", newlyReadCount);
            AddParameter(conversationCommand, "$conversationId", FormatGuid(conversationId));
            if (conversationCommand.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException(
                    "Applying a read-through receipt did not update exactly one conversation.");
            }

            return TransactionResult<LocalCacheOperationStatus>.Commit(
                LocalCacheOperationStatus.Ready);
        });
    }

    private LocalCacheOperationStatus PersistRevocation(Guid conversationId)
    {
        try
        {
            PersistRevocationIntent(conversationId);
            faultInjector?.BeforeRevocationTombstone(conversationId);
            ExecuteRevocationTransaction(conversationId);
            logger.LogInformation("Local conversation access revocation was persisted.");
            return LocalCacheOperationStatus.RevokedConversation;
        }
        catch (Exception exception)
        {
            MarkScopeFatal();
            logger.LogCritical(
                "Local cache scope entered fatal fail-closed state after a revocation persistence failure of type {ExceptionType}.",
                exception.GetType().Name);
            return LocalCacheOperationStatus.FatalScope;
        }
    }

    private void PersistRevocationIntent(Guid conversationId) =>
        PersistRevocationIntents([conversationId]);

    private void PersistRevocationIntents(IReadOnlyList<Guid> conversationIds)
    {
        if (conversationIds.Count == 0)
        {
            return;
        }

        ExecuteWriteWithRetry((connection, transaction) =>
        {
            foreach (var conversationId in conversationIds)
            {
                WriteRevocationIntent(connection, transaction, conversationId);
            }

            return TransactionResult<bool>.Commit(true);
        });
    }

    private void ExecuteRevocationTransaction(Guid conversationId)
    {
        ExecuteWriteWithRetry((connection, transaction) =>
        {
            WriteTombstoneAndDeleteConversation(connection, transaction, conversationId);
            DeleteRevocationIntent(connection, transaction, conversationId);
            return TransactionResult<bool>.Commit(true);
        });
    }

    private void UpsertConversation(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ConversationDto conversation)
    {
        using var command = CreateCommand(connection, transaction, """
            INSERT INTO LocalConversations (
                Id, Type, Name, AvatarUrl, LastMessageId, LastReadMessageId,
                UnreadCount, IsMuted, UpdatedAt)
            VALUES (
                $id, $type, $name, $avatarUrl, $lastMessageId, $lastReadMessageId,
                $unreadCount, $isMuted, $updatedAt)
            ON CONFLICT(Id) DO UPDATE SET
                Type = excluded.Type,
                Name = excluded.Name,
                AvatarUrl = excluded.AvatarUrl,
                LastMessageId = MAX(LocalConversations.LastMessageId, excluded.LastMessageId),
                LastReadMessageId = MAX(LocalConversations.LastReadMessageId, excluded.LastReadMessageId),
                PendingReadThroughMessageId = CASE
                    WHEN LocalConversations.PendingReadThroughMessageId IS NOT NULL
                         AND excluded.LastReadMessageId >=
                             LocalConversations.PendingReadThroughMessageId
                        THEN NULL
                    ELSE LocalConversations.PendingReadThroughMessageId
                END,
                UnreadCount =
                    MAX(
                        excluded.UnreadCount - (
                            SELECT COUNT(*)
                            FROM LocalMessages
                            WHERE ConversationId = excluded.Id
                              AND ServerMessageId > excluded.LastReadMessageId
                              AND ServerMessageId <= excluded.LastMessageId
                              AND SenderId <> $currentUserId
                              AND IsRead = 1),
                        0)
                    + (
                        SELECT COUNT(*)
                        FROM LocalMessages
                        WHERE ConversationId = excluded.Id
                          AND ServerMessageId > excluded.LastMessageId
                          AND SenderId <> $currentUserId
                          AND IsRead = 0),
                IsMuted = excluded.IsMuted,
                UpdatedAt = CASE
                    WHEN LocalConversations.LastMessageId > excluded.LastMessageId
                        THEN LocalConversations.UpdatedAt
                    ELSE excluded.UpdatedAt
                END;
            """);
        AddParameter(command, "$id", FormatGuid(conversation.Id));
        AddParameter(command, "$type", (int)conversation.Type);
        AddParameter(command, "$name", conversation.Name);
        AddParameter(command, "$avatarUrl", conversation.AvatarUrl);
        AddParameter(command, "$lastMessageId", conversation.LastMessageId);
        AddParameter(command, "$lastReadMessageId", conversation.LastReadMessageId);
        AddParameter(command, "$unreadCount", conversation.UnreadCount);
        AddParameter(command, "$isMuted", conversation.IsMuted ? 1 : 0);
        AddParameter(command, "$updatedAt", FormatDateTime(conversation.UpdatedAt));
        AddParameter(command, "$currentUserId", FormatGuid(identity.UserId));
        command.ExecuteNonQuery();
    }

    private static void WriteRevocationIntent(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid conversationId) =>
        WriteLocalAppState(
            connection,
            transaction,
            RevocationIntentPrefix + FormatGuid(conversationId),
            "pending");

    private static void WriteNotificationClearPending(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid conversationId) =>
        WriteLocalAppState(
            connection,
            transaction,
            NotificationClearPendingPrefix + FormatGuid(conversationId),
            "pending");

    private static void WriteNotificationClearCompleted(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid conversationId) =>
        WriteLocalAppState(
            connection,
            transaction,
            NotificationClearCompletedPrefix + FormatGuid(conversationId),
            "completed");

    private static void WriteLocalAppState(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string key,
        string value)
    {
        using var command = CreateCommand(connection, transaction, """
            INSERT INTO LocalAppState (Key, Value, UpdatedAt)
            VALUES ($key, $value, $updatedAt)
            ON CONFLICT(Key) DO UPDATE SET
                Value = excluded.Value,
                UpdatedAt = excluded.UpdatedAt;
            """);
        AddParameter(command, "$key", key);
        AddParameter(command, "$value", value);
        AddParameter(command, "$updatedAt", FormatDateTime(DateTimeOffset.UtcNow));
        command.ExecuteNonQuery();
    }

    private static void WriteTombstoneAndDeleteConversation(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid conversationId)
    {
        DeleteNotificationClearCompleted(connection, transaction, conversationId);
        WriteNotificationClearPending(connection, transaction, conversationId);
        using (var tombstone = CreateCommand(connection, transaction, """
            INSERT INTO RevokedConversations (ConversationId, RevokedAt)
            VALUES ($conversationId, $revokedAt)
            ON CONFLICT(ConversationId) DO UPDATE SET RevokedAt = excluded.RevokedAt;
            """))
        {
            AddParameter(tombstone, "$conversationId", FormatGuid(conversationId));
            AddParameter(tombstone, "$revokedAt", FormatDateTime(DateTimeOffset.UtcNow));
            tombstone.ExecuteNonQuery();
        }

        using var deleteConversation = CreateCommand(
            connection,
            transaction,
            "DELETE FROM LocalConversations WHERE Id = $conversationId;");
        AddParameter(deleteConversation, "$conversationId", FormatGuid(conversationId));
        deleteConversation.ExecuteNonQuery();
    }

    private static void DeleteRevocationIntent(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid conversationId) =>
        DeleteLocalAppState(
            connection,
            transaction,
            RevocationIntentPrefix + FormatGuid(conversationId));

    private static void DeleteNotificationClearPending(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid conversationId) =>
        DeleteLocalAppState(
            connection,
            transaction,
            NotificationClearPendingPrefix + FormatGuid(conversationId));

    private static void DeleteNotificationClearCompleted(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid conversationId) =>
        DeleteLocalAppState(
            connection,
            transaction,
            NotificationClearCompletedPrefix + FormatGuid(conversationId));

    private static void DeleteLocalAppState(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string key)
    {
        using var command = CreateCommand(
            connection,
            transaction,
            "DELETE FROM LocalAppState WHERE Key = $key;");
        AddParameter(command, "$key", key);
        command.ExecuteNonQuery();
    }

    private static void DeleteRevocationState(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid conversationId)
    {
        using (var tombstone = CreateCommand(
            connection,
            transaction,
            "DELETE FROM RevokedConversations WHERE ConversationId = $conversationId;"))
        {
            AddParameter(tombstone, "$conversationId", FormatGuid(conversationId));
            tombstone.ExecuteNonQuery();
        }

        DeleteRevocationIntent(connection, transaction, conversationId);
        DeleteNotificationClearCompleted(connection, transaction, conversationId);
    }

    private IReadOnlyList<Guid> LoadLocalConversationIds()
    {
        using var connection = OpenConnection();
        using var command = CreateCommand(connection, null, "SELECT Id FROM LocalConversations;");
        var ids = new List<Guid>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!Guid.TryParseExact(reader.GetString(0), "D", out var conversationId) ||
                conversationId == Guid.Empty)
            {
                throw new InvalidDataException("The local cache contains an invalid conversation ID.");
            }

            ids.Add(conversationId);
        }

        return ids;
    }

    private IReadOnlyList<Guid> LoadRevocationIntentIds() =>
        LoadAppStateConversationIds(
            RevocationIntentPrefix,
            "The local cache contains an invalid revocation intent.");

    private IReadOnlyList<Guid> LoadNotificationClearConversationIds()
    {
        var ids = new List<Guid>(LoadAppStateConversationIds(
            NotificationClearPendingPrefix,
            "The local cache contains an invalid pending notification cleanup."));
        using var connection = OpenConnection();
        using var command = CreateCommand(connection, null, """
            SELECT revoked.ConversationId
            FROM RevokedConversations AS revoked
            WHERE NOT EXISTS (
                SELECT 1
                FROM LocalAppState AS state
                WHERE state.Key = $completedPrefix || revoked.ConversationId);
            """);
        AddParameter(command, "$completedPrefix", NotificationClearCompletedPrefix);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!Guid.TryParseExact(reader.GetString(0), "D", out var conversationId) ||
                conversationId == Guid.Empty)
            {
                throw new InvalidDataException(
                    "The local cache contains an invalid notification cleanup tombstone.");
            }

            ids.Add(conversationId);
        }

        return ids.Distinct().ToArray();
    }

    private IReadOnlyList<Guid> LoadAppStateConversationIds(
        string prefix,
        string invalidDataMessage)
    {
        using var connection = OpenConnection();
        using var command = CreateCommand(connection, null, """
            SELECT Key FROM LocalAppState
            WHERE Key LIKE $prefix ESCAPE '\';
            """);
        AddParameter(command, "$prefix", prefix + "%");
        var ids = new List<Guid>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var key = reader.GetString(0);
            if (!Guid.TryParseExact(key[prefix.Length..], "D", out var conversationId) ||
                conversationId == Guid.Empty)
            {
                throw new InvalidDataException(invalidDataMessage);
            }

            ids.Add(conversationId);
        }

        return ids;
    }

    private void ReplayRevocationIntents()
    {
        IReadOnlyList<Guid> conversationIds;
        try
        {
            conversationIds = LoadRevocationIntentIds();
            foreach (var conversationId in conversationIds)
            {
                deniedConversations.TryAdd(conversationId, 0);
            }
        }
        catch (Exception exception)
        {
            SetFatalDuringInitialization(exception);
            return;
        }

        foreach (var conversationId in conversationIds)
        {
            try
            {
                ExecuteRevocationTransaction(conversationId);
            }
            catch (Exception exception)
            {
                SetFatalDuringInitialization(exception);
                return;
            }
        }
    }

    private void LoadPersistedTombstones()
    {
        try
        {
            using var connection = OpenConnection();
            using var command = CreateCommand(
                connection,
                null,
                "SELECT ConversationId FROM RevokedConversations;");
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!Guid.TryParseExact(reader.GetString(0), "D", out var conversationId) ||
                    conversationId == Guid.Empty)
                {
                    throw new InvalidDataException("The local cache contains an invalid revocation tombstone.");
                }

                deniedConversations.TryAdd(conversationId, 0);
            }
        }
        catch (Exception exception)
        {
            SetFatalDuringInitialization(exception);
        }
    }

    private void SetFatalDuringInitialization(Exception exception)
    {
        MarkScopeFatal();
        logger.LogCritical(
            "Local cache scope entered fatal fail-closed state during initialization after an error of type {ExceptionType}.",
            exception.GetType().Name);
    }

    private static long ReadLastSyncCursor(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        using var command = CreateCommand(
            connection,
            transaction,
            "SELECT Value FROM LocalAppState WHERE Key = 'LastSyncCursor' LIMIT 1;");
        var value = command.ExecuteScalar();
        if (value is null)
        {
            return 0;
        }

        if (!long.TryParse(
                Convert.ToString(value, CultureInfo.InvariantCulture),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var cursor) ||
            cursor < 0)
        {
            throw new InvalidDataException("The local cache contains an invalid sync cursor.");
        }

        return cursor;
    }

    private static void WriteLastSyncCursor(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long cursor)
    {
        using var command = CreateCommand(connection, transaction, """
            INSERT INTO LocalAppState (Key, Value, UpdatedAt)
            VALUES ('LastSyncCursor', $value, $updatedAt)
            ON CONFLICT(Key) DO UPDATE SET
                Value = excluded.Value,
                UpdatedAt = excluded.UpdatedAt;
            """);
        AddParameter(command, "$value", cursor.ToString(CultureInfo.InvariantCulture));
        AddParameter(command, "$updatedAt", FormatDateTime(DateTimeOffset.UtcNow));
        command.ExecuteNonQuery();
    }

    private LocalCacheOperationStatus GetSyncStatus()
    {
        if (IsFatal)
        {
            return LocalCacheOperationStatus.FatalScope;
        }

        return Volatile.Read(ref authoritativeSnapshotApplied) == 0
            ? LocalCacheOperationStatus.AuthoritativeSnapshotRequired
            : LocalCacheOperationStatus.Ready;
    }

    private static SyncPageCommitOutcome SyncPageOutcome(LocalCacheOperationStatus status) =>
        new(
            status,
            Array.Empty<IncomingMessageMergeResult>(),
            null,
            Array.Empty<long>());

    private LocalCacheOperationStatus GetRegistrationStatus(Guid conversationId)
    {
        if (IsFatal)
        {
            return LocalCacheOperationStatus.FatalScope;
        }

        return deniedConversations.ContainsKey(conversationId)
            ? LocalCacheOperationStatus.RevokedConversation
            : LocalCacheOperationStatus.Ready;
    }

    private LocalCacheOperationStatus GetAccessStatus(Guid conversationId)
    {
        var registrationStatus = GetRegistrationStatus(conversationId);
        if (registrationStatus != LocalCacheOperationStatus.Ready)
        {
            return registrationStatus;
        }

        return authorizedConversations.ContainsKey(conversationId)
            ? LocalCacheOperationStatus.Ready
            : LocalCacheOperationStatus.UnknownConversation;
    }

    private LocalCacheOperationStatus GetDatabaseAccessStatus(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid conversationId)
    {
        var accessStatus = GetAccessStatus(conversationId);
        if (accessStatus != LocalCacheOperationStatus.Ready)
        {
            return accessStatus;
        }

        if (HasTombstone(connection, transaction, conversationId) ||
            HasRevocationIntent(connection, transaction, conversationId))
        {
            return LocalCacheOperationStatus.RevokedConversation;
        }

        using var command = CreateCommand(
            connection,
            transaction,
            "SELECT 1 FROM LocalConversations WHERE Id = $id LIMIT 1;");
        AddParameter(command, "$id", FormatGuid(conversationId));
        return command.ExecuteScalar() is null
            ? LocalCacheOperationStatus.UnknownConversation
            : LocalCacheOperationStatus.Ready;
    }

    private T ExecuteWriteWithRetry<T>(
        Func<SqliteConnection, SqliteTransaction, TransactionResult<T>> operation)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction(deferred: false);
                var result = operation(connection, transaction);
                if (result.ShouldCommit)
                {
                    transaction.Commit();
                }
                else
                {
                    transaction.Rollback();
                }

                return result.Value;
            }
            catch (SqliteException exception) when (IsBusy(exception) && attempt < WriteRetryCount)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(25 * attempt));
            }
        }
    }

    private static void MarkNotificationHandled(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long messageId)
    {
        using var command = CreateCommand(connection, transaction, """
            UPDATE LocalMessages
            SET IsNotificationHandled = 1
            WHERE ServerMessageId = $messageId
              AND IsNotificationHandled = 0;
            """);
        AddParameter(command, "$messageId", messageId);
        command.ExecuteNonQuery();
    }

    private static bool IsNotificationStateAdopted(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using var command = CreateCommand(connection, transaction, """
            SELECT Value
            FROM LocalAppState
            WHERE Key = $key
            LIMIT 1;
            """);
        AddParameter(command, "$key", NotificationStateVersionKey);
        return string.Equals(
            command.ExecuteScalar() as string,
            CurrentNotificationStateVersion.ToString(CultureInfo.InvariantCulture),
            StringComparison.Ordinal);
    }

    private static long[] ValidateNotificationMessageIds(
        IReadOnlyCollection<long> messageIds,
        bool enforceBatchLimit)
    {
        ArgumentNullException.ThrowIfNull(messageIds);
        if (enforceBatchLimit && messageIds.Count > MaxNotificationCandidateIds)
        {
            throw new ArgumentOutOfRangeException(nameof(messageIds));
        }

        var validatedIds = messageIds.Distinct().ToArray();
        if (validatedIds.Any(messageId => messageId <= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(messageIds));
        }

        return validatedIds;
    }

    private SqliteConnection OpenConnection()
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = identity.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default,
            ForeignKeys = true,
            Pooling = true,
            DefaultTimeout = 5,
        }.ToString();
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=5000;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static bool IsBusy(SqliteException exception) =>
        exception.SqliteErrorCode is 5 or 6 || exception.SqliteExtendedErrorCode == 517;

    private static bool HasTombstone(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid conversationId)
    {
        using var command = CreateCommand(
            connection,
            transaction,
            "SELECT 1 FROM RevokedConversations WHERE ConversationId = $id LIMIT 1;");
        AddParameter(command, "$id", FormatGuid(conversationId));
        return command.ExecuteScalar() is not null;
    }

    private static bool HasRevocationIntent(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid conversationId)
    {
        using var command = CreateCommand(
            connection,
            transaction,
            "SELECT 1 FROM LocalAppState WHERE Key = $key LIMIT 1;");
        AddParameter(command, "$key", RevocationIntentPrefix + FormatGuid(conversationId));
        return command.ExecuteScalar() is not null;
    }

    private static LocalMessageRecord? LoadMessageByServerId(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long serverMessageId)
    {
        using var command = CreateCommand(connection, transaction, MessageSelectSql +
            " WHERE ServerMessageId = $serverMessageId LIMIT 1;");
        AddParameter(command, "$serverMessageId", serverMessageId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadMessageRecord(reader) : null;
    }

    private static LocalMessageRecord? LoadMessageByClientKey(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid senderId,
        Guid clientMessageId)
    {
        using var command = CreateCommand(connection, transaction, MessageSelectSql + """
             WHERE SenderId = $senderId AND ClientMessageId = $clientMessageId LIMIT 1;
            """);
        AddParameter(command, "$senderId", FormatGuid(senderId));
        AddParameter(command, "$clientMessageId", FormatGuid(clientMessageId));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadMessageRecord(reader) : null;
    }

    private static IReadOnlyList<LocalPendingMessage> ReadPendingMessages(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid conversationId)
    {
        using var command = CreateCommand(connection, transaction, MessageSelectSql + """
             WHERE ConversationId = $conversationId
               AND ServerMessageId IS NULL
             ORDER BY LocalId
             LIMIT $limitPlusOne;
            """);
        AddParameter(command, "$conversationId", FormatGuid(conversationId));
        AddParameter(command, "$limitPlusOne", MaxOutstandingPendingMessages + 1);
        var records = new List<LocalMessageRecord>(MaxOutstandingPendingMessages + 1);
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                records.Add(ReadMessageRecord(reader));
            }
        }

        if (records.Count > MaxOutstandingPendingMessages)
        {
            throw new InvalidDataException(
                "The local cache contains too many outstanding pending messages.");
        }

        return records
            .Select(record => ToLocalPendingMessage(connection, transaction, record))
            .ToList()
            .AsReadOnly();
    }

    private static LocalMessageRecord ReadMessageRecord(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.IsDBNull(1) ? null : reader.GetInt64(1),
        Guid.ParseExact(reader.GetString(2), "D"),
        Guid.ParseExact(reader.GetString(3), "D"),
        Guid.ParseExact(reader.GetString(4), "D"),
        reader.GetString(5),
        (MessageType)reader.GetInt32(6),
        reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.IsDBNull(8) ? null : reader.GetInt64(8),
        DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        (MessageSendStatus)reader.GetInt32(10));

    private static MessageDto ToMessageDto(
        SqliteConnection connection,
        LocalMessageRecord record,
        SqliteTransaction? transaction = null) => new(
        record.ServerMessageId!.Value,
        record.ClientMessageId,
        record.ConversationId,
        record.SenderId,
        record.SenderDisplayName,
        record.Type,
        record.Content,
        record.ReplyToMessageId,
        Array.Empty<AttachmentDto>(),
        LoadMentions(connection, transaction, record.LocalId),
        record.CreatedAt);

    private static LocalPendingMessage ToLocalPendingMessage(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LocalMessageRecord record)
    {
        if (record.ServerMessageId is not null ||
            record.SendStatus is not MessageSendStatus.Sending and not MessageSendStatus.Failed)
        {
            throw new InvalidDataException(
                "The local cache contains an invalid pending message row.");
        }

        return new LocalPendingMessage(
            record.LocalId,
            record.ClientMessageId,
            record.ConversationId,
            record.SenderId,
            record.SenderDisplayName,
            record.Type,
            record.Content,
            record.ReplyToMessageId,
            LoadMentions(connection, transaction, record.LocalId),
            record.CreatedAt,
            record.SendStatus);
    }

    private static bool IsPendingRequestCompatible(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LocalMessageRecord record,
        PendingMessage message) =>
        record.ClientMessageId == message.ClientMessageId &&
        record.ConversationId == message.ConversationId &&
        record.SenderId == message.SenderId &&
        record.Type == message.Type &&
        string.Equals(record.Content, message.Content, StringComparison.Ordinal) &&
        record.ReplyToMessageId == message.ReplyToMessageId &&
        MentionSetsEqual(
            LoadMentions(connection, transaction, record.LocalId),
            message.MentionUserIds);

    private static bool IsScalarExactMatch(LocalMessageRecord record, MessageDto message) =>
        record.ServerMessageId == message.Id &&
        record.ClientMessageId == message.ClientMessageId &&
        record.ConversationId == message.ConversationId &&
        record.SenderId == message.SenderId &&
        record.Type == message.Type &&
        string.Equals(record.Content, message.Content, StringComparison.Ordinal) &&
        record.ReplyToMessageId == message.ReplyToMessageId &&
        record.CreatedAt.Equals(message.CreatedAt);

    private static void RefreshSenderDisplayName(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long localMessageId,
        string senderDisplayName)
    {
        using var command = CreateCommand(connection, transaction, """
            UPDATE LocalMessages
            SET SenderDisplayName = $senderDisplayName
            WHERE LocalId = $localMessageId;
            """);
        AddParameter(command, "$senderDisplayName", senderDisplayName);
        AddParameter(command, "$localMessageId", localMessageId);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException(
                "Refreshing a message sender display name did not update exactly one row.");
        }
    }

    private static bool IsPendingCompatible(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LocalMessageRecord record,
        MessageDto message)
    {
        if (record.SendStatus == MessageSendStatus.Sent ||
            record.ClientMessageId != message.ClientMessageId ||
            record.ConversationId != message.ConversationId ||
            record.SenderId != message.SenderId ||
            record.Type != message.Type ||
            !string.Equals(record.Content, message.Content, StringComparison.Ordinal) ||
            record.ReplyToMessageId != message.ReplyToMessageId)
        {
            return false;
        }

        return MentionSetsEqual(
            LoadMentions(connection, transaction, record.LocalId),
            message.MentionUserIds);
    }

    private static bool IsExactMatch(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LocalMessageRecord record,
        MessageDto message)
    {
        if (!IsScalarExactMatch(record, message))
        {
            return false;
        }

        return MentionSetsEqual(
            LoadMentions(connection, transaction, record.LocalId),
            message.MentionUserIds);
    }

    private static IReadOnlyList<Guid> LoadMentions(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        long localMessageId)
    {
        using var command = CreateCommand(connection, transaction, """
            SELECT MentionedUserId
            FROM LocalMessageMentions
            WHERE LocalMessageId = $localMessageId
            ORDER BY MentionedUserId;
            """);
        AddParameter(command, "$localMessageId", localMessageId);
        var mentions = new List<Guid>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            mentions.Add(Guid.ParseExact(reader.GetString(0), "D"));
        }

        return mentions;
    }

    private static bool MentionSetsEqual(
        IReadOnlyList<Guid> first,
        IReadOnlyList<Guid> second) =>
        first.Order().SequenceEqual(second.Distinct().Order());

    private static void InsertMentions(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long localMessageId,
        IReadOnlyList<Guid> mentionUserIds)
    {
        foreach (var mentionUserId in mentionUserIds.Distinct())
        {
            using var command = CreateCommand(connection, transaction, """
                INSERT INTO LocalMessageMentions (LocalMessageId, MentionedUserId)
                VALUES ($localMessageId, $mentionedUserId);
                """);
            AddParameter(command, "$localMessageId", localMessageId);
            AddParameter(command, "$mentionedUserId", FormatGuid(mentionUserId));
            command.ExecuteNonQuery();
        }
    }

    private static long GetLastInsertRowId(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using var command = CreateCommand(connection, transaction, "SELECT last_insert_rowid();");
        return (long)command.ExecuteScalar()!;
    }

    private static void AddMessageParameters(SqliteCommand command, MessageDto message)
    {
        AddParameter(command, "$serverMessageId", message.Id);
        AddParameter(command, "$clientMessageId", FormatGuid(message.ClientMessageId));
        AddParameter(command, "$conversationId", FormatGuid(message.ConversationId));
        AddParameter(command, "$senderId", FormatGuid(message.SenderId));
        AddParameter(command, "$senderDisplayName", message.SenderDisplayName);
        AddParameter(command, "$type", (int)message.Type);
        AddParameter(command, "$content", message.Content);
        AddParameter(command, "$replyToMessageId", message.ReplyToMessageId);
        AddParameter(command, "$createdAt", FormatDateTime(message.CreatedAt));
        AddParameter(command, "$sendStatus", (int)MessageSendStatus.Sent);
    }

    private static void AddMessageParameters(SqliteCommand command, PendingMessage message)
    {
        AddParameter(command, "$clientMessageId", FormatGuid(message.ClientMessageId));
        AddParameter(command, "$conversationId", FormatGuid(message.ConversationId));
        AddParameter(command, "$senderId", FormatGuid(message.SenderId));
        AddParameter(command, "$senderDisplayName", message.SenderDisplayName);
        AddParameter(command, "$type", (int)message.Type);
        AddParameter(command, "$content", message.Content);
        AddParameter(command, "$replyToMessageId", message.ReplyToMessageId);
        AddParameter(command, "$createdAt", FormatDateTime(message.CreatedAt));
        AddParameter(command, "$sendStatus", (int)MessageSendStatus.Sending);
    }

    private static SqliteCommand CreateCommand(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string commandText)
    {
        var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.Transaction = transaction;
        command.CommandTimeout = 5;
        return command;
    }

    private static void ExecuteNonQuery(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string commandText)
    {
        using var command = CreateCommand(connection, transaction, commandText);
        command.ExecuteNonQuery();
    }

    private static void AddParameter(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static string FormatGuid(Guid value) => value.ToString("D").ToLowerInvariant();

    private static string FormatDateTime(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseStoredDateTime(string value) =>
        DateTimeOffset.ParseExact(
            value,
            "O",
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);

    private static bool TryValidateConversationSnapshot(ConversationListResponse? snapshot)
    {
        if (snapshot is null || !snapshot.Complete || snapshot.Conversations is null)
        {
            return false;
        }

        var ids = new HashSet<Guid>();
        try
        {
            foreach (var conversation in snapshot.Conversations)
            {
                ValidateConversation(conversation);
                if (!ids.Add(conversation.Id))
                {
                    return false;
                }
            }
        }
        catch (ArgumentException)
        {
            return false;
        }

        return true;
    }

    private static bool TryValidateSyncPage(
        SyncResponse? response,
        long expectedCursor,
        long? expectedSnapshotUpperBound)
    {
        if (response is null ||
            response.Messages is null ||
            response.Messages.Count > 200 ||
            expectedCursor < 0 ||
            (expectedSnapshotUpperBound is not null &&
             expectedSnapshotUpperBound.Value < expectedCursor) ||
            response.NextCursor < expectedCursor ||
            response.SnapshotUpperBound < response.NextCursor ||
            (expectedSnapshotUpperBound is not null &&
             response.SnapshotUpperBound != expectedSnapshotUpperBound.Value) ||
            response.HasMore != (response.NextCursor < response.SnapshotUpperBound) ||
            (response.SnapshotUpperBound > expectedCursor &&
             response.NextCursor <= expectedCursor) ||
            (response.HasMore &&
             (response.Messages.Count == 0 ||
              response.Messages[^1].Id != response.NextCursor)))
        {
            return false;
        }

        var previousMessageId = expectedCursor;
        try
        {
            foreach (var message in response.Messages)
            {
                ValidateIncomingMessage(message);
                if (message.Id <= previousMessageId || message.Id > response.NextCursor)
                {
                    return false;
                }

                previousMessageId = message.Id;
            }
        }
        catch (ArgumentException)
        {
            return false;
        }

        return true;
    }

    private static bool TryValidateHistoryMessages(
        Guid conversationId,
        IReadOnlyList<MessageDto> messages)
    {
        if (messages.Count > 100)
        {
            return false;
        }

        long previousMessageId = 0;
        try
        {
            foreach (var message in messages)
            {
                ValidateIncomingMessage(message);
                if (message.ConversationId != conversationId ||
                    message.Id <= previousMessageId)
                {
                    return false;
                }

                previousMessageId = message.Id;
            }
        }
        catch (ArgumentException)
        {
            return false;
        }

        return true;
    }

    private static void ValidateConversation(ConversationDto conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ValidateGuid(conversation.Id, nameof(conversation));
        ArgumentNullException.ThrowIfNull(conversation.Name);
        if (!Enum.IsDefined(conversation.Type) ||
            conversation.LastMessageId < 0 ||
            conversation.LastReadMessageId < 0 ||
            conversation.UnreadCount < 0)
        {
            throw new ArgumentException("Conversation contains invalid values.", nameof(conversation));
        }
    }

    private static void ValidateIncomingMessage(MessageDto message)
    {
        ArgumentNullException.ThrowIfNull(message);
        ValidateGuid(message.ClientMessageId, nameof(message));
        ValidateGuid(message.ConversationId, nameof(message));
        ValidateGuid(message.SenderId, nameof(message));
        ArgumentNullException.ThrowIfNull(message.SenderDisplayName);
        ArgumentNullException.ThrowIfNull(message.Attachments);
        ArgumentNullException.ThrowIfNull(message.MentionUserIds);
        if (message.Id <= 0 ||
            !Enum.IsDefined(message.Type) ||
            message.ReplyToMessageId is <= 0 ||
            message.Attachments.Count != 0 ||
            message.MentionUserIds.Any(id => id == Guid.Empty))
        {
            throw new ArgumentException("Message contains unsupported or invalid values.", nameof(message));
        }
    }

    private static void ValidateIngestionContext(
        LocalMessageIngestionContext context,
        IncomingMessageSource? requiredSource = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!Enum.IsDefined(context.Source))
        {
            throw new ArgumentOutOfRangeException(nameof(context));
        }

        if (requiredSource.HasValue && context.Source != requiredSource.Value)
        {
            throw new ArgumentException(
                $"This operation requires the {requiredSource.Value} message source.",
                nameof(context));
        }

        if (context.ForegroundConversationId == Guid.Empty)
        {
            throw new ArgumentException(
                "A foreground conversation ID cannot be empty.",
                nameof(context));
        }

        if (context.Source != IncomingMessageSource.History &&
            !context.IsHistoryObservationConfirmed)
        {
            throw new ArgumentException(
                "Only History ingestion may defer observation side effects.",
                nameof(context));
        }
    }

    private static void ValidatePendingMessage(PendingMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        ValidateGuid(message.ClientMessageId, nameof(message));
        ValidateGuid(message.ConversationId, nameof(message));
        ValidateGuid(message.SenderId, nameof(message));
        ArgumentNullException.ThrowIfNull(message.SenderDisplayName);
        ArgumentNullException.ThrowIfNull(message.MentionUserIds);
        if (!Enum.IsDefined(message.Type) ||
            message.MentionUserIds.Count > 20 ||
            message.MentionUserIds.Any(id => id == Guid.Empty) ||
            message.MentionUserIds.Distinct().Count() != message.MentionUserIds.Count ||
            message.ReplyToMessageId is <= 0 ||
            (message.Type == MessageType.Text &&
             !ClientTextMessageContentValidator.IsValid(message.Content)))
        {
            throw new ArgumentException("Pending message contains invalid values.", nameof(message));
        }
    }

    private static void ValidateGuid(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("GUID must not be empty.", parameterName);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
    }

    private sealed record ConversationAttentionState(
        long LastMessageId,
        long LastReadMessageId,
        long AuthoritativeLastMessageId,
        bool IsMuted);

    private sealed class ForegroundReadThroughAccumulator
    {
        private readonly Dictionary<Guid, ForegroundReadThrough> values = new();

        public IEnumerable<ForegroundReadThrough> Values => values.Values;

        public void Observe(
            MessageDto message,
            ConversationAttentionState conversationState,
            IncomingMessageMergeResult mergeResult)
        {
            if (!values.TryGetValue(message.ConversationId, out var readThrough))
            {
                readThrough = new ForegroundReadThrough(message, conversationState);
                values.Add(message.ConversationId, readThrough);
            }
            else if (message.Id > readThrough.LatestMessage.Id)
            {
                readThrough.LatestMessage = message;
            }

            if (mergeResult == IncomingMessageMergeResult.Inserted &&
                message.Id > readThrough.AuthoritativeLastMessageId)
            {
                readThrough.UncountedMessageIds.Add(message.Id);
            }
        }
    }

    private sealed class ForegroundReadThrough(
        MessageDto latestMessage,
        ConversationAttentionState initialState)
    {
        public MessageDto LatestMessage { get; set; } = latestMessage;

        public long AuthoritativeLastMessageId { get; } =
            initialState.AuthoritativeLastMessageId;

        public HashSet<long> UncountedMessageIds { get; } = [];
    }

    private sealed record LocalMessageRecord(
        long LocalId,
        long? ServerMessageId,
        Guid ClientMessageId,
        Guid ConversationId,
        Guid SenderId,
        string SenderDisplayName,
        MessageType Type,
        string? Content,
        long? ReplyToMessageId,
        DateTimeOffset CreatedAt,
        MessageSendStatus SendStatus);

    private sealed class ScopeAccessState
    {
        public SemaphoreSlim OperationGate { get; } = new(1, 1);

        public ConcurrentDictionary<Guid, byte> DeniedConversations { get; } = new();

        public int FatalScope;

        public int PendingRecoveryCompleted;
    }

    private readonly record struct TransactionResult<T>(T Value, bool ShouldCommit)
    {
        public static TransactionResult<T> Commit(T value) => new(value, true);

        public static TransactionResult<T> Rollback(T value) => new(value, false);
    }

    private const string MessageSelectSql = """
        SELECT LocalId, ServerMessageId, ClientMessageId, ConversationId, SenderId,
               SenderDisplayName, Type, Content, ReplyToMessageId, CreatedAt, LocalSendStatus
        FROM LocalMessages
        """;

    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS LocalConversations (
            Id TEXT PRIMARY KEY,
            Type INTEGER NOT NULL,
            Name TEXT NOT NULL,
            AvatarUrl TEXT NULL,
            LastMessageId INTEGER NOT NULL DEFAULT 0,
            LastReadMessageId INTEGER NOT NULL DEFAULT 0,
            PendingReadThroughMessageId INTEGER NULL,
            UnreadCount INTEGER NOT NULL DEFAULT 0,
            IsMuted INTEGER NOT NULL DEFAULT 0,
            LastOpenedAt TEXT NULL,
            UpdatedAt TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS LocalMessages (
            LocalId INTEGER PRIMARY KEY AUTOINCREMENT,
            ServerMessageId INTEGER NULL UNIQUE,
            ClientMessageId TEXT NOT NULL,
            ConversationId TEXT NOT NULL,
            SenderId TEXT NOT NULL,
            SenderDisplayName TEXT NOT NULL,
            Type INTEGER NOT NULL,
            Content TEXT NULL,
            ReplyToMessageId INTEGER NULL,
            CreatedAt TEXT NOT NULL,
            IsRead INTEGER NOT NULL DEFAULT 0,
            IsNotificationHandled INTEGER NOT NULL DEFAULT 0,
            LocalSendStatus INTEGER NOT NULL,
            UNIQUE(SenderId, ClientMessageId),
            FOREIGN KEY(ConversationId) REFERENCES LocalConversations(Id) ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS LocalMessageMentions (
            LocalMessageId INTEGER NOT NULL,
            MentionedUserId TEXT NOT NULL,
            PRIMARY KEY(LocalMessageId, MentionedUserId),
            FOREIGN KEY(LocalMessageId) REFERENCES LocalMessages(LocalId) ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS RevokedConversations (
            ConversationId TEXT PRIMARY KEY,
            RevokedAt TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS LocalAppState (
            Key TEXT PRIMARY KEY,
            Value TEXT NOT NULL,
            UpdatedAt TEXT NOT NULL
        );

        INSERT INTO LocalAppState (Key, Value, UpdatedAt)
        VALUES ('SchemaVersion', '1', CURRENT_TIMESTAMP)
        ON CONFLICT(Key) DO NOTHING;

        PRAGMA user_version=1;
        """;
}
