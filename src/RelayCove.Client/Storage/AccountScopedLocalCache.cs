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
    private readonly ConcurrentDictionary<Guid, byte> deniedConversations;
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

    public async Task<LocalCacheOperationStatus> AddPendingMessageAsync(
        PendingMessage message,
        CancellationToken cancellationToken = default)
    {
        ValidatePendingMessage(message);
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

    public async Task<LocalCacheMergeOutcome> MergeIncomingMessageAsync(
        MessageDto message,
        CancellationToken cancellationToken = default)
    {
        ValidateIncomingMessage(message);
        ThrowIfDisposed();

        var initialStatus = GetAccessStatus(message.ConversationId);
        if (initialStatus != LocalCacheOperationStatus.Ready)
        {
            return new LocalCacheMergeOutcome(initialStatus, null);
        }

        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => MergeIncomingMessage(message)).ConfigureAwait(false);
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
                    LastMessageId = excluded.LastMessageId,
                    LastReadMessageId = MAX(LocalConversations.LastReadMessageId, excluded.LastReadMessageId),
                    UnreadCount = excluded.UnreadCount,
                    UpdatedAt = excluded.UpdatedAt;
                """);
            AddParameter(command, "$id", FormatGuid(conversation.Id));
            AddParameter(command, "$type", (int)conversation.Type);
            AddParameter(command, "$name", conversation.Name);
            AddParameter(command, "$avatarUrl", conversation.AvatarUrl);
            AddParameter(command, "$lastMessageId", conversation.LastMessageId);
            AddParameter(command, "$lastReadMessageId", conversation.LastReadMessageId);
            AddParameter(command, "$unreadCount", conversation.UnreadCount);
            AddParameter(command, "$updatedAt", FormatDateTime(conversation.UpdatedAt));
            command.ExecuteNonQuery();
            return TransactionResult<LocalCacheOperationStatus>.Commit(LocalCacheOperationStatus.Ready);
        });

        if (result == LocalCacheOperationStatus.RevokedConversation)
        {
            deniedConversations.TryAdd(conversation.Id, 0);
            authorizedConversations.TryRemove(conversation.Id, out _);
            return result;
        }

        if (deniedConversations.ContainsKey(conversation.Id) || IsFatal)
        {
            authorizedConversations.TryRemove(conversation.Id, out _);
            return GetRegistrationStatus(conversation.Id);
        }

        authorizedConversations.TryAdd(conversation.Id, 0);
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
                    SenderDisplayName, Type, Content, ReplyToMessageId, CreatedAt, LocalSendStatus)
                VALUES (
                    NULL, $clientMessageId, $conversationId, $senderId,
                    $senderDisplayName, $type, $content, $replyToMessageId, $createdAt, $sendStatus)
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

    private LocalCacheMergeOutcome MergeIncomingMessage(MessageDto message)
    {
        var status = GetAccessStatus(message.ConversationId);
        if (status != LocalCacheOperationStatus.Ready)
        {
            return new LocalCacheMergeOutcome(status, null);
        }

        var outcome = ExecuteWriteWithRetry((connection, transaction) =>
        {
            var databaseStatus = GetDatabaseAccessStatus(connection, transaction, message.ConversationId);
            if (databaseStatus != LocalCacheOperationStatus.Ready)
            {
                return TransactionResult<LocalCacheMergeOutcome>.Rollback(
                    new LocalCacheMergeOutcome(databaseStatus, null));
            }

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
                return isDuplicate
                    ? TransactionResult<LocalCacheMergeOutcome>.Commit(
                        new LocalCacheMergeOutcome(LocalCacheOperationStatus.Ready, result))
                    : TransactionResult<LocalCacheMergeOutcome>.Rollback(
                        new LocalCacheMergeOutcome(LocalCacheOperationStatus.Ready, result));
            }

            if (keyHit is not null)
            {
                if (keyHit.ServerMessageId is not null ||
                    !IsPendingCompatible(connection, transaction, keyHit, message))
                {
                    return TransactionResult<LocalCacheMergeOutcome>.Rollback(
                        new LocalCacheMergeOutcome(
                            LocalCacheOperationStatus.Ready,
                            IncomingMessageMergeResult.Conflict));
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

                return TransactionResult<LocalCacheMergeOutcome>.Commit(
                    new LocalCacheMergeOutcome(
                        LocalCacheOperationStatus.Ready,
                        IncomingMessageMergeResult.PendingPromoted));
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
            return TransactionResult<LocalCacheMergeOutcome>.Commit(
                new LocalCacheMergeOutcome(
                    LocalCacheOperationStatus.Ready,
                    IncomingMessageMergeResult.Inserted));
        });

        if (outcome.Status == LocalCacheOperationStatus.RevokedConversation)
        {
            deniedConversations.TryAdd(message.ConversationId, 0);
            authorizedConversations.TryRemove(message.ConversationId, out _);
        }

        if (outcome.Result == IncomingMessageMergeResult.Conflict)
        {
            logger.LogWarning(
                "An incoming message was rejected because its immutable payload conflicted.");
        }

        return outcome;
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

    private void PersistRevocationIntent(Guid conversationId)
    {
        ExecuteWriteWithRetry((connection, transaction) =>
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
            return TransactionResult<bool>.Commit(true);
        });
    }

    private void ExecuteRevocationTransaction(Guid conversationId)
    {
        ExecuteWriteWithRetry((connection, transaction) =>
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

            using (var deleteConversation = CreateCommand(
                connection,
                transaction,
                "DELETE FROM LocalConversations WHERE Id = $conversationId;"))
            {
                AddParameter(deleteConversation, "$conversationId", FormatGuid(conversationId));
                deleteConversation.ExecuteNonQuery();
            }

            using (var clearIntent = CreateCommand(
                connection,
                transaction,
                "DELETE FROM LocalAppState WHERE Key = $key;"))
            {
                AddParameter(clearIntent, "$key", RevocationIntentPrefix + FormatGuid(conversationId));
                clearIntent.ExecuteNonQuery();
            }

            return TransactionResult<bool>.Commit(true);
        });
    }

    private void ReplayRevocationIntents()
    {
        IReadOnlyList<Guid> conversationIds;
        try
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

                deniedConversations.TryAdd(conversationId, 0);
                ids.Add(conversationId);
            }

            conversationIds = ids;
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
        string.Equals(record.SenderDisplayName, message.SenderDisplayName, StringComparison.Ordinal) &&
        record.Type == message.Type &&
        string.Equals(record.Content, message.Content, StringComparison.Ordinal) &&
        record.ReplyToMessageId == message.ReplyToMessageId &&
        record.CreatedAt.Equals(message.CreatedAt);

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
