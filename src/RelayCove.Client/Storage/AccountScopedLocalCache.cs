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
    private const string RevocationIntentPrefix = "RevocationIntent/";
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
    private readonly ConcurrentDictionary<Guid, byte> deniedConversations;
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
        try
        {
            return await Task.Run(() => RegisterAuthoritativeConversation(conversation)).ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<LocalCacheOperationStatus> ApplyAuthoritativeConversationSnapshotAsync(
        ConversationListResponse snapshot,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!TryValidateConversationSnapshot(snapshot))
        {
            logger.LogWarning("An authoritative conversation snapshot failed protocol validation.");
            return LocalCacheOperationStatus.ProtocolError;
        }

        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => ApplyAuthoritativeConversationSnapshot(snapshot))
                .ConfigureAwait(false);
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
        try
        {
            return await Task.Run(() => ApplySyncPage(response, expectedCursor, context))
                .ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<LocalCacheOperationStatus> AddPendingMessageAsync(
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
            return initialStatus;
        }

        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => AddPendingMessage(message)).ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }
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
        try
        {
            return await Task.Run(() => MergeIncomingMessage(message, context))
                .ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }
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

    public async Task<LocalCacheOperationStatus> RevokeConversationAccessAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        ValidateGuid(conversationId, nameof(conversationId));
        ThrowIfDisposed();

        deniedConversations.TryAdd(conversationId, 0);
        authorizedConversations.TryRemove(conversationId, out _);
        authoritativeLastMessageIds.TryRemove(conversationId, out _);

        // Once a revocation reaches this boundary, caller cancellation must not drop
        // the durable intent or tombstone work.
        await operationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (IsFatal)
            {
                return LocalCacheOperationStatus.FatalScope;
            }

            return await Task.Run(() => PersistRevocation(conversationId)).ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref disposed, 1);

        return ValueTask.CompletedTask;
    }

    private void Initialize()
    {
        Directory.CreateDirectory(identity.ScopeDirectory);
        using (var connection = OpenConnection())
        {
            ExecuteNonQuery(connection, null, "PRAGMA journal_mode=WAL;");
            ExecuteNonQuery(connection, null, "PRAGMA synchronous=NORMAL;");
            using var versionCommand = CreateCommand(connection, null, "PRAGMA user_version;");
            var schemaVersion = Convert.ToInt32(versionCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
            if (schemaVersion is not 0 and not 1)
            {
                throw new InvalidDataException("The local cache schema version is not supported.");
            }

            ExecuteNonQuery(connection, null, SchemaSql);
        }

        ReplayRevocationIntents();
        LoadPersistedTombstones();
    }

    private LocalCacheOperationStatus ApplyAuthoritativeConversationSnapshot(
        ConversationListResponse snapshot)
    {
        var conversationsById = snapshot.Conversations.ToDictionary(conversation => conversation.Id);
        try
        {
            var missingConversationIds = LoadLocalConversationIds()
                .Concat(LoadRevocationIntentIds())
                .Concat(authorizedConversations.Keys)
                .Distinct()
                .Where(conversationId => !conversationsById.ContainsKey(conversationId))
                .ToArray();

            foreach (var conversationId in missingConversationIds)
            {
                deniedConversations.TryAdd(conversationId, 0);
                authorizedConversations.TryRemove(conversationId, out _);
                authoritativeLastMessageIds.TryRemove(conversationId, out _);
            }

            PersistRevocationIntents(missingConversationIds);
            faultInjector?.BeforeAuthoritativeSnapshotCommit();
            ExecuteWriteWithRetry((connection, transaction) =>
            {
                foreach (var conversation in conversationsById.Values)
                {
                    DeleteRevocationState(connection, transaction, conversation.Id);
                    UpsertConversation(connection, transaction, conversation);
                }

                foreach (var conversationId in missingConversationIds)
                {
                    WriteTombstoneAndDeleteConversation(connection, transaction, conversationId);
                    DeleteRevocationIntent(connection, transaction, conversationId);
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
            logger.LogInformation("An authoritative conversation snapshot was committed.");
            return LocalCacheOperationStatus.Ready;
        }
        catch (Exception exception)
        {
            Interlocked.Exchange(ref scopeState.FatalScope, 1);
            Volatile.Write(ref authoritativeSnapshotApplied, 0);
            logger.LogCritical(
                "Local cache scope entered fatal fail-closed state after an authoritative snapshot failure of type {ExceptionType}.",
                exception.GetType().Name);
            return LocalCacheOperationStatus.FatalScope;
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
            Interlocked.Exchange(ref scopeState.FatalScope, 1);
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

        var outcome = ExecuteWriteWithRetry((connection, transaction) =>
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
                    readThrough.LatestMessage,
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

        if (outcome.Status == LocalCacheOperationStatus.Conflict)
        {
            logger.LogWarning("A sync page was rolled back because an immutable message payload conflicted.");
        }

        return outcome;
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

    private LocalCacheOperationStatus AddPendingMessage(PendingMessage message)
    {
        var status = GetAccessStatus(message.ConversationId);
        if (status != LocalCacheOperationStatus.Ready)
        {
            return status;
        }

        return ExecuteWriteWithRetry((connection, transaction) =>
        {
            var databaseStatus = GetDatabaseAccessStatus(connection, transaction, message.ConversationId);
            if (databaseStatus != LocalCacheOperationStatus.Ready)
            {
                return TransactionResult<LocalCacheOperationStatus>.Rollback(databaseStatus);
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
                ON CONFLICT(SenderId, ClientMessageId) DO NOTHING;
                """);
            AddMessageParameters(command, message);
            var inserted = command.ExecuteNonQuery() == 1;
            if (inserted)
            {
                var localId = GetLastInsertRowId(connection, transaction);
                InsertMentions(connection, transaction, localId, message.MentionUserIds);
            }

            return TransactionResult<LocalCacheOperationStatus>.Commit(LocalCacheOperationStatus.Ready);
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
            Interlocked.Exchange(ref scopeState.FatalScope, 1);
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
        var suppressNotification = ownMessage ||
            atOrBelowReadBoundary ||
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
                    message,
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
                context.Source == IncomingMessageSource.History;
            var consumeObservedUnread =
                context.Source == IncomingMessageSource.History &&
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
            SELECT LastMessageId, LastReadMessageId
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
            authoritativeLastMessageId);
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

    private void AdvanceForegroundReadThrough(
        SqliteConnection connection,
        SqliteTransaction transaction,
        MessageDto message,
        IReadOnlyCollection<long> uncountedMessageIds)
    {
        // A realtime ID can be ahead of unseen sync gaps. The message row is safe to
        // mark read immediately, but the contiguous conversation boundary must not
        // advance beyond the cursor already committed before this transaction.
        var committedSyncCursor = ReadLastSyncCursor(connection, transaction);
        var safeReadBoundary = Math.Min(message.Id, committedSyncCursor);
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
              AND ServerMessageId <= $serverMessageId
              AND IsRead = 0
              AND SenderId <> $currentUserId;
            """);
        AddParameter(countCommand, "$conversationId", FormatGuid(message.ConversationId));
        AddParameter(countCommand, "$serverMessageId", message.Id);
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
            AddParameter(messagesCommand, "$conversationId", FormatGuid(message.ConversationId));
            AddParameter(messagesCommand, "$serverMessageId", message.Id);
            messagesCommand.ExecuteNonQuery();
        }

        using var conversationCommand = CreateCommand(connection, transaction, """
            UPDATE LocalConversations
            SET LastMessageId = MAX(LastMessageId, $serverMessageId),
                LastReadMessageId = MAX(LastReadMessageId, $safeReadBoundary),
                PendingReadThroughMessageId = MAX(
                    COALESCE(PendingReadThroughMessageId, 0),
                    $serverMessageId),
                UnreadCount = MAX(UnreadCount - $newlyReadCount, 0),
                UpdatedAt = CASE
                    WHEN LastMessageId < $serverMessageId THEN $updatedAt
                    ELSE UpdatedAt
                END
            WHERE Id = $conversationId;
            """);
        AddParameter(conversationCommand, "$serverMessageId", message.Id);
        AddParameter(conversationCommand, "$safeReadBoundary", safeReadBoundary);
        AddParameter(conversationCommand, "$newlyReadCount", newlyReadCount);
        AddParameter(conversationCommand, "$updatedAt", FormatDateTime(message.CreatedAt));
        AddParameter(conversationCommand, "$conversationId", FormatGuid(message.ConversationId));
        conversationCommand.ExecuteNonQuery();
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
            Interlocked.Exchange(ref scopeState.FatalScope, 1);
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
                UnreadCount, UpdatedAt)
            VALUES (
                $id, $type, $name, $avatarUrl, $lastMessageId, $lastReadMessageId,
                $unreadCount, $updatedAt)
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
        AddParameter(command, "$updatedAt", FormatDateTime(conversation.UpdatedAt));
        AddParameter(command, "$currentUserId", FormatGuid(identity.UserId));
        command.ExecuteNonQuery();
    }

    private static void WriteRevocationIntent(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid conversationId)
    {
        using var command = CreateCommand(connection, transaction, """
            INSERT INTO LocalAppState (Key, Value, UpdatedAt)
            VALUES ($key, $value, $updatedAt)
            ON CONFLICT(Key) DO UPDATE SET
                Value = excluded.Value,
                UpdatedAt = excluded.UpdatedAt;
            """);
        AddParameter(command, "$key", RevocationIntentPrefix + FormatGuid(conversationId));
        AddParameter(command, "$value", "pending");
        AddParameter(command, "$updatedAt", FormatDateTime(DateTimeOffset.UtcNow));
        command.ExecuteNonQuery();
    }

    private static void WriteTombstoneAndDeleteConversation(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid conversationId)
    {
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
        Guid conversationId)
    {
        using var command = CreateCommand(
            connection,
            transaction,
            "DELETE FROM LocalAppState WHERE Key = $key;");
        AddParameter(command, "$key", RevocationIntentPrefix + FormatGuid(conversationId));
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

    private IReadOnlyList<Guid> LoadRevocationIntentIds()
    {
        using var connection = OpenConnection();
        using var command = CreateCommand(connection, null, """
            SELECT Key FROM LocalAppState
            WHERE Key LIKE $prefix ESCAPE '\';
            """);
        AddParameter(command, "$prefix", RevocationIntentPrefix + "%");
        var ids = new List<Guid>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var key = reader.GetString(0);
            if (!Guid.TryParseExact(key[RevocationIntentPrefix.Length..], "D", out var conversationId) ||
                conversationId == Guid.Empty)
            {
                throw new InvalidDataException("The local cache contains an invalid revocation intent.");
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
        Interlocked.Exchange(ref scopeState.FatalScope, 1);
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
        LocalMessageRecord record) => new(
        record.ServerMessageId!.Value,
        record.ClientMessageId,
        record.ConversationId,
        record.SenderId,
        record.SenderDisplayName,
        record.Type,
        record.Content,
        record.ReplyToMessageId,
        Array.Empty<AttachmentDto>(),
        LoadMentions(connection, null, record.LocalId),
        record.CreatedAt);

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
    }

    private static void ValidatePendingMessage(PendingMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        ValidateGuid(message.ClientMessageId, nameof(message));
        ValidateGuid(message.ConversationId, nameof(message));
        ValidateGuid(message.SenderId, nameof(message));
        ArgumentNullException.ThrowIfNull(message.SenderDisplayName);
        ArgumentNullException.ThrowIfNull(message.MentionUserIds);
        if (!Enum.IsDefined(message.Type) || message.MentionUserIds.Any(id => id == Guid.Empty))
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
        long AuthoritativeLastMessageId);

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
