using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using RelayCove.Core;

namespace RelayCove.Data;

public sealed partial class SqliteAccountStore : IAccountStore, IAsyncDisposable
{
    public const int CurrentSchemaVersion = 3;

    private readonly string _accountsRoot;
    private readonly Channel<IWorkItem> _mutations;
    private readonly Task _worker;
    private int _disposed;

    public SqliteAccountStore(string appDataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataRoot);
        var fullRoot = Path.GetFullPath(appDataRoot);
        _accountsRoot = Path.Combine(fullRoot, "accounts");
        _mutations = Channel.CreateUnbounded<IWorkItem>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        _worker = Task.Run(ProcessMutationsAsync);
    }

    public Task<IReadOnlyList<StoredAccount>> ListAsync(CancellationToken cancellationToken = default) =>
        EnqueueAsync(ListCoreAsync, cancellationToken);

    public Task InitializeAsync(StoredAccount account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ValidateAccount(account);
        return EnqueueAsync(token => InitializeCoreAsync(account, token), cancellationToken);
    }

    public Task MigrateAsync(AccountId accountId, CancellationToken cancellationToken = default) =>
        EnqueueAsync(token => MigrateCoreAsync(accountId, token), cancellationToken);

    public Task<AccountSnapshot?> LoadAsync(AccountId accountId, CancellationToken cancellationToken = default) =>
        EnqueueAsync(token => LoadCoreAsync(accountId, token), cancellationToken);

    public Task<IReadOnlyList<ChatMessage>> QueryMessagesAsync(
        AccountId accountId,
        ConversationKey conversation,
        long? beforeMessageId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        if (beforeMessageId <= 0) throw new ArgumentOutOfRangeException(nameof(beforeMessageId));
        if (limit is < 1 or > 1_000) throw new ArgumentOutOfRangeException(nameof(limit));
        return EnqueueAsync(async token =>
            (await QueryMessagePageCoreAsync(accountId, conversation, beforeMessageId, limit, token).ConfigureAwait(false)).Messages,
            cancellationToken);
    }

    public Task<MessagePage> QueryMessagePageAsync(
        AccountId accountId,
        ConversationKey conversation,
        long? beforeMessageId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        if (beforeMessageId <= 0) throw new ArgumentOutOfRangeException(nameof(beforeMessageId));
        if (limit is < 1 or > 1_000) throw new ArgumentOutOfRangeException(nameof(limit));
        return EnqueueAsync(
            token => QueryMessagePageCoreAsync(accountId, conversation, beforeMessageId, limit, token),
            cancellationToken);
    }

    public Task StoreMessagePageAsync(
        AccountId accountId,
        IReadOnlyCollection<ChatMessage> messages,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        return EnqueueAsync(token => StoreMessagePageCoreAsync(accountId, messages, token), cancellationToken);
    }

    public Task ReplaceRegisterSnapshotAsync(
        AccountId accountId,
        RegisterResult snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return EnqueueAsync(token => ReplaceRegisterSnapshotCoreAsync(accountId, snapshot, token), cancellationToken);
    }

    public Task ApplyBatchAsync(
        AccountId accountId,
        IReadOnlyCollection<DomainEvent> events,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);
        return EnqueueAsync(token => ApplyBatchCoreAsync(accountId, events, token), cancellationToken);
    }

    public Task PurgeSubscriptionAsync(
        AccountId accountId,
        long channelId,
        CancellationToken cancellationToken = default)
    {
        if (channelId <= 0) throw new ArgumentOutOfRangeException(nameof(channelId));
        return EnqueueAsync(token => PurgeSubscriptionCoreAsync(accountId, channelId, token), cancellationToken);
    }

    public Task<bool> IsCacheUnlockedAsync(
        AccountId accountId,
        CancellationToken cancellationToken = default) =>
        EnqueueAsync(token => IsCacheUnlockedCoreAsync(accountId, token), cancellationToken);

    public Task SetCacheUnlockedAsync(
        AccountId accountId,
        bool isUnlocked,
        CancellationToken cancellationToken = default) =>
        EnqueueAsync(token => SetCacheUnlockedCoreAsync(accountId, isUnlocked, token), cancellationToken);

    public Task ClearAsync(AccountId accountId, CancellationToken cancellationToken = default) =>
        EnqueueAsync(token => ClearCoreAsync(accountId, token), cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _mutations.Writer.TryComplete();
        await _worker.ConfigureAwait(false);
    }

    private async Task ProcessMutationsAsync()
    {
        await foreach (var item in _mutations.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            await item.RunAsync().ConfigureAwait(false);
        }
    }

    private Task EnqueueAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken)
    {
        return EnqueueAsync(async token =>
        {
            await operation(token).ConfigureAwait(false);
            return true;
        }, cancellationToken);
    }

    private Task<T> EnqueueAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        var item = new WorkItem<T>(operation, cancellationToken);
        if (!_mutations.Writer.TryWrite(item)) throw new ObjectDisposedException(nameof(SqliteAccountStore));
        return item.Task;
    }

    private async Task<IReadOnlyList<StoredAccount>> ListCoreAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_accountsRoot)) return [];
        var accounts = new List<StoredAccount>();
        foreach (var directory in Directory.EnumerateDirectories(_accountsRoot).OrderBy(path => path, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(directory);
            if (!AccountDirectoryName().IsMatch(name)) continue;
            var databasePath = Path.Combine(directory, "relaycove.db");
            if (!File.Exists(databasePath)) continue;
            try
            {
                await using var connection = await OpenAsync(databasePath, create: false, cancellationToken).ConfigureAwait(false);
                var account = await ReadAccountAsync(connection, null, cancellationToken).ConfigureAwait(false);
                if (account is not null && account.AccountId.Value == name) accounts.Add(account);
            }
            catch (SqliteException)
            {
                // One damaged account must not hide the other valid accounts.
            }
            catch (InvalidDataException)
            {
                // Ignore metadata that cannot prove it belongs to this account directory.
            }
            catch (ArgumentException)
            {
                // Ignore malformed realm or account metadata.
            }
        }
        return accounts;
    }

    private async Task InitializeCoreAsync(StoredAccount account, CancellationToken cancellationToken)
    {
        var databasePath = GetDatabasePath(account.AccountId);
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await using var connection = await OpenAsync(databasePath, create: true, cancellationToken).ConfigureAwait(false);
        await MigrateConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO account_metadata(singleton, account_id, realm, email, user_id, cache_unlocked)
            VALUES(1, $account_id, $realm, $email, $user_id, 1)
            ON CONFLICT(singleton) DO UPDATE SET
                account_id = excluded.account_id,
                realm = excluded.realm,
                email = excluded.email,
                user_id = excluded.user_id;
            """;
        command.Parameters.AddWithValue("$account_id", account.AccountId.Value);
        command.Parameters.AddWithValue("$realm", account.Realm.AbsoluteUri);
        command.Parameters.AddWithValue("$email", account.Email);
        command.Parameters.AddWithValue("$user_id", account.UserId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task MigrateCoreAsync(AccountId accountId, CancellationToken cancellationToken)
    {
        var databasePath = RequireDatabasePath(accountId);
        await using var connection = await OpenAsync(databasePath, create: false, cancellationToken).ConfigureAwait(false);
        await MigrateConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AccountSnapshot?> LoadCoreAsync(AccountId accountId, CancellationToken cancellationToken)
    {
        var databasePath = GetDatabasePath(accountId);
        if (!File.Exists(databasePath)) return null;
        await using var connection = await OpenAsync(databasePath, create: false, cancellationToken).ConfigureAwait(false);
        var account = await ReadAccountAsync(connection, accountId, cancellationToken).ConfigureAwait(false);
        if (account is null) return null;
        var unlocked = await ReadCacheUnlockedAsync(connection, cancellationToken).ConfigureAwait(false);
        var state = unlocked
            ? await ReadStateAsync(connection, null, cancellationToken).ConfigureAwait(false)
            : ClientState.Empty with { Connection = new ConnectionState(ConnectionStatus.Locked) };
        var recentDirectMessages = unlocked
            ? await ReadRecentDirectMessagesAsync(connection, cancellationToken).ConfigureAwait(false)
            : [];
        return new AccountSnapshot(account, unlocked, state, recentDirectMessages);
    }

    private async Task<MessagePage> QueryMessagePageCoreAsync(
        AccountId accountId,
        ConversationKey conversation,
        long? beforeMessageId,
        int limit,
        CancellationToken cancellationToken)
    {
        var databasePath = RequireDatabasePath(accountId);
        await using var connection = await OpenAsync(databasePath, create: false, cancellationToken).ConfigureAwait(false);
        await EnsureUnlockedAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, conversation_kind, channel_id, topic, dm_user_ids, sender_id,
                   content, timestamp_utc, is_read, sender_display_name, sender_avatar_url, is_starred
            FROM messages
            WHERE conversation_key = $conversation
              AND ($before IS NULL OR id < $before)
            ORDER BY id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$conversation", conversation.CanonicalKey);
        command.Parameters.AddWithValue("$before", beforeMessageId is null ? DBNull.Value : beforeMessageId.Value);
        command.Parameters.AddWithValue("$limit", checked(limit + 1));
        var messages = new List<ChatMessage>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) messages.Add(ReadMessage(reader));
        }
        var hasOlderInCache = messages.Count > limit;
        if (hasOlderInCache) messages.RemoveAt(messages.Count - 1);
        var messagesById = messages.ToDictionary(message => message.Id);
        await PopulateReactionsAsync(connection, null, messagesById, cancellationToken).ConfigureAwait(false);
        for (var index = 0; index < messages.Count; index++) messages[index] = messagesById[messages[index].Id];
        messages.Reverse();
        return new MessagePage(messages, hasOlderInCache);
    }

    private async Task StoreMessagePageCoreAsync(
        AccountId accountId,
        IReadOnlyCollection<ChatMessage> messages,
        CancellationToken cancellationToken)
    {
        if (messages.Count == 0) return;
        var databasePath = RequireDatabasePath(accountId);
        await using var connection = await OpenAsync(databasePath, create: false, cancellationToken).ConfigureAwait(false);
        await EnsureUnlockedAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var message in messages)
        {
            if (!await CanStoreMessageAsync(connection, transaction, message, cancellationToken).ConfigureAwait(false)) continue;
            await UpsertMessageAsync(connection, transaction, message, cancellationToken).ConfigureAwait(false);
            await ReplaceMessageReactionsAsync(connection, transaction, message, cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ReplaceRegisterSnapshotCoreAsync(
        AccountId accountId,
        RegisterResult snapshot,
        CancellationToken cancellationToken)
    {
        var databasePath = RequireDatabasePath(accountId);
        await using var connection = await OpenAsync(databasePath, create: false, cancellationToken).ConfigureAwait(false);
        await EnsureUnlockedAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await ReplaceSubscriptionsAsync(connection, transaction, snapshot.Subscriptions, cancellationToken).ConfigureAwait(false);
        await ReplaceUsersAsync(connection, transaction, snapshot.Users, cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, "DELETE FROM topics;", transaction, cancellationToken).ConfigureAwait(false);
        await ApplyEventsIncrementallyAsync(connection, transaction, snapshot.Events, updateUnread: false, cancellationToken).ConfigureAwait(false);
        await ReplaceUnreadAsync(connection, transaction, snapshot.Unread, cancellationToken).ConfigureAwait(false);
        await ReplaceRecentDirectMessagesAsync(connection, transaction, snapshot.RecentDirectMessages, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ApplyBatchCoreAsync(
        AccountId accountId,
        IReadOnlyCollection<DomainEvent> events,
        CancellationToken cancellationToken)
    {
        var databasePath = RequireDatabasePath(accountId);
        await using var connection = await OpenAsync(databasePath, create: false, cancellationToken).ConfigureAwait(false);
        await EnsureUnlockedAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await ApplyEventsIncrementallyAsync(connection, transaction, events, updateUnread: true, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task PurgeSubscriptionCoreAsync(AccountId accountId, long channelId, CancellationToken cancellationToken)
    {
        var databasePath = RequireDatabasePath(accountId);
        await using var connection = await OpenAsync(databasePath, create: false, cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var unread = await ReadUnreadAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await DeleteSubscriptionAsync(connection, transaction, channelId, cancellationToken).ConfigureAwait(false);
        await ReplaceUnreadAsync(connection, transaction, unread.RemoveChannel(channelId), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task SetCacheUnlockedCoreAsync(AccountId accountId, bool isUnlocked, CancellationToken cancellationToken)
    {
        var databasePath = RequireDatabasePath(accountId);
        await using var connection = await OpenAsync(databasePath, create: false, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE account_metadata SET cache_unlocked = $value WHERE singleton = 1;";
        command.Parameters.AddWithValue("$value", isUnlocked ? 1 : 0);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("Account metadata is missing.");
        }
    }

    private async Task<bool> IsCacheUnlockedCoreAsync(AccountId accountId, CancellationToken cancellationToken)
    {
        var databasePath = RequireDatabasePath(accountId);
        await using var connection = await OpenAsync(databasePath, create: false, cancellationToken).ConfigureAwait(false);
        return await ReadCacheUnlockedAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    private Task ClearCoreAsync(AccountId accountId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = GetAccountDirectory(accountId);
        if (!Directory.Exists(directory)) return Task.CompletedTask;
        var expectedParent = Path.GetFullPath(_accountsRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var resolved = Path.GetFullPath(directory);
        if (!resolved.StartsWith(expectedParent, StringComparison.OrdinalIgnoreCase) ||
            Path.GetDirectoryName(resolved) is not { } parent ||
            !string.Equals(Path.GetFullPath(parent), Path.GetFullPath(_accountsRoot), StringComparison.OrdinalIgnoreCase) ||
            !AccountDirectoryName().IsMatch(Path.GetFileName(resolved)) ||
            File.GetAttributes(resolved).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException("Refusing to clear an invalid account directory.");
        }
        Directory.Delete(resolved, recursive: true);
        return Task.CompletedTask;
    }

    private async Task MigrateConnectionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var version = await ExecuteScalarLongAsync(connection, "PRAGMA user_version;", null, cancellationToken).ConfigureAwait(false);
        if (version > CurrentSchemaVersion) throw new InvalidOperationException($"Unsupported schema version {version}.");
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var hasCoreSchema = await ExecuteScalarLongAsync(
            connection,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='messages';",
            transaction,
            cancellationToken).ConfigureAwait(false) > 0;
        if (version < 1 && !hasCoreSchema)
        {
            await ExecuteNonQueryAsync(connection, """
            CREATE TABLE IF NOT EXISTS schema_info(version INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS account_metadata(
                singleton INTEGER PRIMARY KEY CHECK(singleton = 1),
                account_id TEXT NOT NULL UNIQUE,
                realm TEXT NOT NULL,
                email TEXT NOT NULL,
                user_id INTEGER NOT NULL,
                cache_unlocked INTEGER NOT NULL DEFAULT 1 CHECK(cache_unlocked IN (0, 1))
            );
            CREATE TABLE IF NOT EXISTS users(
                user_id INTEGER PRIMARY KEY,
                full_name TEXT NOT NULL,
                email TEXT NULL,
                is_active INTEGER NOT NULL CHECK(is_active IN (0, 1))
            );
            CREATE TABLE IF NOT EXISTS subscriptions(
                channel_id INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                is_active INTEGER NOT NULL CHECK(is_active IN (0, 1))
            );
            CREATE TABLE IF NOT EXISTS topics(
                channel_id INTEGER NOT NULL,
                topic TEXT NOT NULL,
                max_message_id INTEGER NULL,
                PRIMARY KEY(channel_id, topic),
                FOREIGN KEY(channel_id) REFERENCES subscriptions(channel_id) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS recent_dm(
                canonical_key TEXT PRIMARY KEY,
                participant_ids TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS messages(
                id INTEGER PRIMARY KEY,
                conversation_key TEXT NOT NULL,
                conversation_kind TEXT NOT NULL CHECK(conversation_kind IN ('channel', 'dm')),
                channel_id INTEGER NULL,
                topic TEXT NULL,
                dm_user_ids TEXT NULL,
                sender_id INTEGER NOT NULL,
                sender_display_name TEXT NULL,
                content TEXT NOT NULL,
                timestamp_utc TEXT NOT NULL,
                is_read INTEGER NOT NULL CHECK(is_read IN (0, 1)),
                FOREIGN KEY(channel_id) REFERENCES subscriptions(channel_id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS ix_messages_conversation_id ON messages(conversation_key, id DESC);
            CREATE TABLE IF NOT EXISTS unread_counts(
                conversation_key TEXT PRIMARY KEY,
                unread_count INTEGER NOT NULL CHECK(unread_count >= 0)
            );
            CREATE TABLE IF NOT EXISTS unread_state(
                singleton INTEGER PRIMARY KEY CHECK(singleton = 1),
                reported_total INTEGER NULL CHECK(reported_total IS NULL OR reported_total >= 0),
                is_truncated INTEGER NOT NULL CHECK(is_truncated IN (0, 1))
            );
            DELETE FROM schema_info;
            INSERT INTO schema_info(version) VALUES(1);
            PRAGMA user_version = 1;
            """, transaction, cancellationToken).ConfigureAwait(false);
        }
        if (version < 2)
        {
            await ExecuteNonQueryAsync(connection, """
                ALTER TABLE users ADD COLUMN avatar_url TEXT NULL;
                ALTER TABLE users ADD COLUMN avatar_version INTEGER NULL;
                ALTER TABLE users ADD COLUMN is_bot INTEGER NOT NULL DEFAULT 0 CHECK(is_bot IN (0, 1));
                ALTER TABLE messages ADD COLUMN sender_avatar_url TEXT NULL;
                ALTER TABLE messages ADD COLUMN is_starred INTEGER NOT NULL DEFAULT 0 CHECK(is_starred IN (0, 1));
                CREATE TABLE message_reactions(
                    message_id INTEGER NOT NULL,
                    reaction_type TEXT NOT NULL,
                    emoji_code TEXT NOT NULL,
                    emoji_name TEXT NOT NULL,
                    user_id INTEGER NOT NULL,
                    user_full_name TEXT NULL,
                    PRIMARY KEY(message_id, reaction_type, emoji_code, user_id),
                    FOREIGN KEY(message_id) REFERENCES messages(id) ON DELETE CASCADE
                );
                UPDATE schema_info SET version = 2;
                PRAGMA user_version = 2;
                """, transaction, cancellationToken).ConfigureAwait(false);
        }
        if (version < 3)
        {
            await ExecuteNonQueryAsync(connection, """
                CREATE INDEX IF NOT EXISTS ix_message_reactions_message_id
                ON message_reactions(message_id);
                UPDATE schema_info SET version = 3;
                PRAGMA user_version = 3;
                """, transaction, cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ApplyEventsIncrementallyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IEnumerable<DomainEvent> events,
        bool updateUnread,
        CancellationToken cancellationToken)
    {
        var unread = updateUnread
            ? await ReadUnreadAsync(connection, transaction, cancellationToken).ConfigureAwait(false)
            : new UnreadState();
        var unreadChanged = false;
        foreach (var domainEvent in events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (domainEvent)
            {
                case MessageUpsertEvent upsert:
                    (unread, unreadChanged) = await PersistReplacementAsync(
                        connection, transaction, upsert.Message, upsert.Source, unread, unreadChanged, updateUnread, cancellationToken).ConfigureAwait(false);
                    break;
                case MessagesUpdatedEvent updated:
                    foreach (var message in updated.Messages)
                    {
                        (unread, unreadChanged) = await PersistReplacementAsync(
                            connection, transaction, message, updated.Source, unread, unreadChanged, updateUnread, cancellationToken).ConfigureAwait(false);
                    }
                    break;
                case SendConfirmedEvent sent:
                    (unread, unreadChanged) = await PersistReplacementAsync(
                        connection, transaction, sent.Message, DomainEventSource.Send, unread, unreadChanged, updateUnread, cancellationToken).ConfigureAwait(false);
                    break;
                case MessageContentChangedEvent changed:
                    await ExecuteAsync(connection, transaction,
                        "UPDATE messages SET content = $content WHERE id = $id;",
                        cancellationToken, ("$content", changed.Content), ("$id", changed.MessageId)).ConfigureAwait(false);
                    break;
                case MessageReactionChangedEvent changed when changed.Add:
                    await ExecuteAsync(connection, transaction, """
                        INSERT INTO message_reactions(message_id, reaction_type, emoji_code, emoji_name, user_id, user_full_name)
                        SELECT $message, $type, $code, $name, $user, $full_name
                        WHERE EXISTS(SELECT 1 FROM messages WHERE id = $message)
                        ON CONFLICT(message_id, reaction_type, emoji_code, user_id) DO UPDATE SET
                            emoji_name = excluded.emoji_name,
                            user_full_name = excluded.user_full_name;
                        """, cancellationToken,
                        ("$message", changed.MessageId),
                        ("$type", changed.Reaction.Identity.ReactionType),
                        ("$code", changed.Reaction.Identity.EmojiCode),
                        ("$name", changed.Reaction.Identity.EmojiName),
                        ("$user", changed.Reaction.UserId),
                        ("$full_name", changed.Reaction.UserFullName)).ConfigureAwait(false);
                    break;
                case MessageReactionChangedEvent changed:
                    await ExecuteAsync(connection, transaction, """
                        DELETE FROM message_reactions
                        WHERE message_id = $message AND reaction_type = $type AND emoji_code = $code AND user_id = $user;
                        """, cancellationToken,
                        ("$message", changed.MessageId),
                        ("$type", changed.Reaction.Identity.ReactionType),
                        ("$code", changed.Reaction.Identity.EmojiCode),
                        ("$user", changed.Reaction.UserId)).ConfigureAwait(false);
                    break;
                case MessageDeletedEvent deleted:
                    foreach (var message in await ReadMessagesByIdsAsync(connection, transaction, deleted.MessageIds, cancellationToken).ConfigureAwait(false))
                    {
                        if (updateUnread && !message.IsRead)
                        {
                            unread = unread.Adjust(message.Conversation.CanonicalKey, -1);
                            unreadChanged = true;
                        }
                    }
                    await DeleteMessagesAsync(connection, transaction, deleted.MessageIds, cancellationToken).ConfigureAwait(false);
                    break;
                case MessageMovedEvent moved:
                    foreach (var message in await ReadMessagesByIdsAsync(connection, transaction, moved.MessageIds, cancellationToken).ConfigureAwait(false))
                    {
                        if (updateUnread && !message.IsRead && message.Conversation != moved.Destination)
                        {
                            unread = unread.Adjust(message.Conversation.CanonicalKey, -1)
                                .Adjust(moved.Destination.CanonicalKey, 1);
                            unreadChanged = true;
                        }
                        var replacement = message with { Conversation = moved.Destination };
                        if (await CanStoreMessageAsync(connection, transaction, replacement, cancellationToken).ConfigureAwait(false))
                        {
                            await UpsertMessageAsync(connection, transaction, replacement, cancellationToken).ConfigureAwait(false);
                        }
                        else
                        {
                            await DeleteMessagesAsync(connection, transaction, [message.Id], cancellationToken).ConfigureAwait(false);
                        }
                    }
                    break;
                case MessageFlagsChangedEvent flags when string.Equals(flags.Flag, "read", StringComparison.OrdinalIgnoreCase):
                    if (flags.AllMessages)
                    {
                        var read = flags.Operation == MessageFlagOperation.Add;
                        await ExecuteAsync(connection, transaction, "UPDATE messages SET is_read = $read;", cancellationToken, ("$read", read ? 1 : 0)).ConfigureAwait(false);
                        if (updateUnread)
                        {
                            unread = read
                                ? new UnreadState()
                                : await BuildUnreadFromMessagesAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
                            unreadChanged = true;
                        }
                    }
                    else
                    {
                        var read = flags.Operation == MessageFlagOperation.Add;
                        foreach (var message in await ReadMessagesByIdsAsync(connection, transaction, flags.MessageIds, cancellationToken).ConfigureAwait(false))
                        {
                            if (message.IsRead == read) continue;
                            await ExecuteAsync(connection, transaction, "UPDATE messages SET is_read = $read WHERE id = $id;",
                                cancellationToken, ("$read", read ? 1 : 0), ("$id", message.Id)).ConfigureAwait(false);
                            if (updateUnread)
                            {
                                unread = unread.Adjust(message.Conversation.CanonicalKey, read ? -1 : 1);
                                unreadChanged = true;
                            }
                        }
                    }
                    break;
                case MessageFlagsChangedEvent flags when string.Equals(flags.Flag, "starred", StringComparison.OrdinalIgnoreCase):
                    var starred = flags.Operation == MessageFlagOperation.Add ? 1 : 0;
                    if (flags.AllMessages)
                    {
                        await ExecuteAsync(connection, transaction, "UPDATE messages SET is_starred = $starred;", cancellationToken, ("$starred", starred)).ConfigureAwait(false);
                    }
                    else
                    {
                        await UpdateMessagesFlagAsync(connection, transaction, flags.MessageIds, "is_starred", starred, cancellationToken).ConfigureAwait(false);
                    }
                    break;
                case SubscriptionChangedEvent changed when changed.IsRemoved:
                    if (updateUnread)
                    {
                        unread = unread.RemoveChannel(changed.Subscription.ChannelId);
                        unreadChanged = true;
                    }
                    await DeleteSubscriptionAsync(connection, transaction, changed.Subscription.ChannelId, cancellationToken).ConfigureAwait(false);
                    break;
                case SubscriptionRemovedEvent removed:
                    if (updateUnread)
                    {
                        unread = unread.RemoveChannel(removed.ChannelId);
                        unreadChanged = true;
                    }
                    await DeleteSubscriptionAsync(connection, transaction, removed.ChannelId, cancellationToken).ConfigureAwait(false);
                    break;
                case SubscriptionChangedEvent changed:
                    await UpsertSubscriptionAsync(connection, transaction, changed.Subscription, cancellationToken).ConfigureAwait(false);
                    break;
                case SubscriptionPatchedEvent patched:
                    await ExecuteAsync(connection, transaction, """
                        UPDATE subscriptions SET
                            name = COALESCE($name, name),
                            is_active = COALESCE($active, is_active)
                        WHERE channel_id = $id;
                        """, cancellationToken,
                        ("$name", patched.Name),
                        ("$active", patched.IsActive is null ? null : patched.IsActive.Value ? 1 : 0),
                        ("$id", patched.ChannelId)).ConfigureAwait(false);
                    break;
                case UserUpsertEvent upsert:
                    await UpsertUserAsync(connection, transaction, upsert.User, cancellationToken).ConfigureAwait(false);
                    break;
                case UserPatchedEvent patched:
                    await ExecuteAsync(connection, transaction, """
                        UPDATE users SET
                            full_name = COALESCE($name, full_name),
                            email = COALESCE($email, email),
                            is_active = COALESCE($active, is_active)
                        WHERE user_id = $id;
                        """, cancellationToken,
                        ("$name", patched.FullName),
                        ("$email", patched.Email),
                        ("$active", patched.IsActive is null ? null : patched.IsActive.Value ? 1 : 0),
                        ("$id", patched.UserId)).ConfigureAwait(false);
                    break;
                case TopicUpsertEvent upsert:
                    await ExecuteAsync(connection, transaction, """
                        INSERT INTO topics(channel_id, topic, max_message_id)
                        SELECT $channel, $topic, $max
                        WHERE EXISTS(SELECT 1 FROM subscriptions WHERE channel_id = $channel)
                        ON CONFLICT(channel_id, topic) DO UPDATE SET max_message_id = excluded.max_message_id;
                        """, cancellationToken,
                        ("$channel", upsert.Topic.ChannelId), ("$topic", upsert.Topic.Topic), ("$max", upsert.Topic.MaxMessageId)).ConfigureAwait(false);
                    break;
            }
        }

        if (updateUnread && unreadChanged)
        {
            await ReplaceUnreadAsync(connection, transaction, unread, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<(UnreadState Unread, bool Changed)> PersistReplacementAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ChatMessage message,
        DomainEventSource source,
        UnreadState unread,
        bool unreadChanged,
        bool updateUnread,
        CancellationToken cancellationToken)
    {
        var existing = await ReadMessageByIdAsync(connection, transaction, message.Id, cancellationToken).ConfigureAwait(false);
        if (!await CanStoreMessageAsync(connection, transaction, message, cancellationToken).ConfigureAwait(false))
        {
            return (unread, unreadChanged);
        }
        if (updateUnread)
        {
            if (existing is not null)
            {
                if (!existing.IsRead) unread = unread.Adjust(existing.Conversation.CanonicalKey, -1);
                if (!message.IsRead) unread = unread.Adjust(message.Conversation.CanonicalKey, 1);
                unreadChanged |= !existing.IsRead || !message.IsRead;
            }
            else if (source == DomainEventSource.Realtime && !message.IsRead)
            {
                unread = unread.Adjust(message.Conversation.CanonicalKey, 1);
                unreadChanged = true;
            }
        }
        await UpsertMessageAsync(connection, transaction, message, cancellationToken).ConfigureAwait(false);
        await ReplaceMessageReactionsAsync(connection, transaction, message, cancellationToken).ConfigureAwait(false);
        return (unread, unreadChanged);
    }

    private async Task<ClientState> ReadStateAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var messages = new Dictionary<long, ChatMessage>();

        var subscriptions = new Dictionary<long, Subscription>();
        await using (var command = CreateCommand(connection, transaction, "SELECT channel_id, name, is_active FROM subscriptions;"))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var subscription = new Subscription(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2) != 0);
                subscriptions[subscription.ChannelId] = subscription;
            }
        }

        var users = new Dictionary<long, UserProfile>();
        await using (var command = CreateCommand(connection, transaction, "SELECT user_id, full_name, email, is_active, avatar_url, avatar_version, is_bot FROM users;"))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var user = new UserProfile(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetInt64(3) != 0,
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetInt32(5),
                    reader.GetInt64(6) != 0);
                users[user.UserId] = user;
            }
        }

        var topics = new Dictionary<string, TopicSummary>(StringComparer.Ordinal);
        await using (var command = CreateCommand(connection, transaction, "SELECT channel_id, topic, max_message_id FROM topics;"))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var topic = new TopicSummary(reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetInt64(2));
                topics[new ChannelTopic(topic.ChannelId, topic.Topic).CanonicalKey] = topic;
            }
        }

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        await using (var command = CreateCommand(connection, transaction, "SELECT conversation_key, unread_count FROM unread_counts;"))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) counts[reader.GetString(0)] = reader.GetInt32(1);
        }
        int? reportedTotal = null;
        var truncated = false;
        await using (var command = CreateCommand(connection, transaction, "SELECT reported_total, is_truncated FROM unread_state WHERE singleton = 1;"))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                reportedTotal = reader.IsDBNull(0) ? null : reader.GetInt32(0);
                truncated = reader.GetInt64(1) != 0;
            }
        }
        return new ClientState(messages, subscriptions, users, topics, unread: new UnreadState(counts, reportedTotal, truncated));
    }

    private static async Task ReplaceSubscriptionsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<Subscription> subscriptions,
        CancellationToken cancellationToken)
    {
        foreach (var subscription in subscriptions)
        {
            await UpsertSubscriptionAsync(connection, transaction, subscription, cancellationToken).ConfigureAwait(false);
        }

        if (subscriptions.Count == 0)
        {
            await ExecuteNonQueryAsync(connection, "DELETE FROM subscriptions;", transaction, cancellationToken).ConfigureAwait(false);
            return;
        }

        await using var command = CreateCommand(connection, transaction, string.Empty);
        var parameterNames = new string[subscriptions.Count];
        for (var index = 0; index < subscriptions.Count; index++)
        {
            parameterNames[index] = $"$channel{index}";
            command.Parameters.AddWithValue(parameterNames[index], subscriptions[index].ChannelId);
        }
        command.CommandText = $"DELETE FROM subscriptions WHERE channel_id NOT IN ({string.Join(',', parameterNames)});";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ReplaceUsersAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<UserProfile> users,
        CancellationToken cancellationToken)
    {
        await ExecuteNonQueryAsync(connection, "DELETE FROM users;", transaction, cancellationToken).ConfigureAwait(false);
        foreach (var user in users)
        {
            await UpsertUserAsync(connection, transaction, user, cancellationToken).ConfigureAwait(false);
        }
    }

    private static Task UpsertSubscriptionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Subscription subscription,
        CancellationToken cancellationToken) =>
        ExecuteAsync(connection, transaction, """
            INSERT INTO subscriptions(channel_id, name, is_active) VALUES($id, $name, $active)
            ON CONFLICT(channel_id) DO UPDATE SET name = excluded.name, is_active = excluded.is_active;
            """, cancellationToken,
            ("$id", subscription.ChannelId), ("$name", subscription.Name), ("$active", subscription.IsActive ? 1 : 0));

    private static Task UpsertUserAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        UserProfile user,
        CancellationToken cancellationToken) =>
        ExecuteAsync(connection, transaction, """
            INSERT INTO users(user_id, full_name, email, is_active, avatar_url, avatar_version, is_bot)
            VALUES($id, $name, $email, $active, $avatar, $avatar_version, $bot)
            ON CONFLICT(user_id) DO UPDATE SET
                full_name = excluded.full_name,
                email = excluded.email,
                is_active = excluded.is_active,
                avatar_url = excluded.avatar_url,
                avatar_version = excluded.avatar_version,
                is_bot = excluded.is_bot;
            """, cancellationToken,
            ("$id", user.UserId), ("$name", user.FullName), ("$email", user.Email),
            ("$active", user.IsActive ? 1 : 0), ("$avatar", user.AvatarUrl),
            ("$avatar_version", user.AvatarVersion), ("$bot", user.IsBot ? 1 : 0));

    private static Task DeleteSubscriptionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long channelId,
        CancellationToken cancellationToken) =>
        ExecuteAsync(connection, transaction, "DELETE FROM subscriptions WHERE channel_id = $id;", cancellationToken, ("$id", channelId));

    private static async Task<bool> CanStoreMessageAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ChatMessage message,
        CancellationToken cancellationToken)
    {
        if (message.Conversation is not ChannelTopic channel) return true;
        await using var command = CreateCommand(connection, transaction,
            "SELECT EXISTS(SELECT 1 FROM subscriptions WHERE channel_id = $channel);");
        command.Parameters.AddWithValue("$channel", channel.ChannelId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) != 0;
    }

    private static async Task ReplaceMessageReactionsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ChatMessage message,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, transaction, "DELETE FROM message_reactions WHERE message_id = $message;",
            cancellationToken, ("$message", message.Id)).ConfigureAwait(false);
        foreach (var reaction in message.Reactions)
        {
            await ExecuteAsync(connection, transaction, """
                INSERT INTO message_reactions(message_id, reaction_type, emoji_code, emoji_name, user_id, user_full_name)
                VALUES($message, $type, $code, $name, $user, $full_name);
                """, cancellationToken,
                ("$message", message.Id), ("$type", reaction.Identity.ReactionType),
                ("$code", reaction.Identity.EmojiCode), ("$name", reaction.Identity.EmojiName),
                ("$user", reaction.UserId), ("$full_name", reaction.UserFullName)).ConfigureAwait(false);
        }
    }

    private static async Task<ChatMessage?> ReadMessageByIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long messageId,
        CancellationToken cancellationToken)
    {
        var messages = await ReadMessagesByIdsAsync(connection, transaction, [messageId], cancellationToken).ConfigureAwait(false);
        return messages.Count == 0 ? null : messages[0];
    }

    private static async Task<IReadOnlyList<ChatMessage>> ReadMessagesByIdsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IEnumerable<long> messageIds,
        CancellationToken cancellationToken)
    {
        var ids = messageIds.Distinct().ToArray();
        if (ids.Length == 0) return [];
        await using var command = CreateCommand(connection, transaction, string.Empty);
        var parameterNames = new string[ids.Length];
        for (var index = 0; index < ids.Length; index++)
        {
            parameterNames[index] = $"$id{index}";
            command.Parameters.AddWithValue(parameterNames[index], ids[index]);
        }
        command.CommandText = $"""
            SELECT id, conversation_kind, channel_id, topic, dm_user_ids, sender_id,
                   content, timestamp_utc, is_read, sender_display_name, sender_avatar_url, is_starred
            FROM messages WHERE id IN ({string.Join(',', parameterNames)});
            """;
        var messages = new List<ChatMessage>(ids.Length);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) messages.Add(ReadMessage(reader));
        return messages;
    }

    private static async Task DeleteMessagesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IEnumerable<long> messageIds,
        CancellationToken cancellationToken)
    {
        var ids = messageIds.Distinct().ToArray();
        if (ids.Length == 0) return;
        await using var command = CreateCommand(connection, transaction, string.Empty);
        var parameterNames = new string[ids.Length];
        for (var index = 0; index < ids.Length; index++)
        {
            parameterNames[index] = $"$id{index}";
            command.Parameters.AddWithValue(parameterNames[index], ids[index]);
        }
        command.CommandText = $"DELETE FROM messages WHERE id IN ({string.Join(',', parameterNames)});";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpdateMessagesFlagAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IEnumerable<long> messageIds,
        string column,
        int value,
        CancellationToken cancellationToken)
    {
        var ids = messageIds.Distinct().ToArray();
        if (ids.Length == 0) return;
        await using var command = CreateCommand(connection, transaction, string.Empty);
        var parameterNames = new string[ids.Length];
        for (var index = 0; index < ids.Length; index++)
        {
            parameterNames[index] = $"$id{index}";
            command.Parameters.AddWithValue(parameterNames[index], ids[index]);
        }
        command.Parameters.AddWithValue("$value", value);
        command.CommandText = $"UPDATE messages SET {column} = $value WHERE id IN ({string.Join(',', parameterNames)});";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<UnreadState> ReadUnreadAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        await using (var command = CreateCommand(connection, transaction, "SELECT conversation_key, unread_count FROM unread_counts;"))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) counts[reader.GetString(0)] = reader.GetInt32(1);
        }
        int? reportedTotal = null;
        var truncated = false;
        await using (var command = CreateCommand(connection, transaction, "SELECT reported_total, is_truncated FROM unread_state WHERE singleton = 1;"))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                reportedTotal = reader.IsDBNull(0) ? null : reader.GetInt32(0);
                truncated = reader.GetInt64(1) != 0;
            }
        }
        return new UnreadState(counts, reportedTotal, truncated);
    }

    private static async Task<UnreadState> BuildUnreadFromMessagesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        await using var command = CreateCommand(connection, transaction, """
            SELECT conversation_key, COUNT(*) FROM messages WHERE is_read = 0 GROUP BY conversation_key;
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) counts[reader.GetString(0)] = reader.GetInt32(1);
        return new UnreadState(counts, counts.Values.Sum(), false);
    }

    private static async Task ReplaceUnreadAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        UnreadState unread,
        CancellationToken cancellationToken)
    {
        await ExecuteNonQueryAsync(connection, "DELETE FROM unread_counts; DELETE FROM unread_state;", transaction, cancellationToken).ConfigureAwait(false);
        foreach (var pair in unread.Counts)
        {
            await ExecuteAsync(connection, transaction,
                "INSERT INTO unread_counts(conversation_key, unread_count) VALUES($key, $count);",
                cancellationToken, ("$key", pair.Key), ("$count", pair.Value)).ConfigureAwait(false);
        }
        await ExecuteAsync(connection, transaction,
            "INSERT INTO unread_state(singleton, reported_total, is_truncated) VALUES(1, $total, $truncated);",
            cancellationToken, ("$total", unread.ReportedTotal), ("$truncated", unread.IsTruncated ? 1 : 0)).ConfigureAwait(false);
    }

    private static async Task UpsertMessageAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ChatMessage message,
        CancellationToken cancellationToken)
    {
        string kind;
        long? channelId = null;
        string? topic = null;
        string? dmUserIds = null;
        switch (message.Conversation)
        {
            case ChannelTopic channel:
                kind = "channel";
                channelId = channel.ChannelId;
                topic = channel.Topic;
                break;
            case DirectMessage direct:
                kind = "dm";
                dmUserIds = string.Join(',', direct.OtherUserIds);
                break;
            default:
                throw new InvalidOperationException("Unsupported conversation type.");
        }
        await ExecuteAsync(connection, transaction, """
            INSERT INTO messages(
                id, conversation_key, conversation_kind, channel_id, topic, dm_user_ids,
                sender_id, sender_display_name, sender_avatar_url, content, timestamp_utc, is_read, is_starred)
            VALUES($id, $key, $kind, $channel, $topic, $dm, $sender, $sender_name, $sender_avatar, $content, $timestamp, $read, $starred)
            ON CONFLICT(id) DO UPDATE SET
                conversation_key = excluded.conversation_key,
                conversation_kind = excluded.conversation_kind,
                channel_id = excluded.channel_id,
                topic = excluded.topic,
                dm_user_ids = excluded.dm_user_ids,
                sender_id = excluded.sender_id,
                sender_display_name = excluded.sender_display_name,
                sender_avatar_url = excluded.sender_avatar_url,
                content = excluded.content,
                timestamp_utc = excluded.timestamp_utc,
                is_read = excluded.is_read,
                is_starred = excluded.is_starred;
            """, cancellationToken,
            ("$id", message.Id), ("$key", message.Conversation.CanonicalKey), ("$kind", kind),
            ("$channel", channelId), ("$topic", topic), ("$dm", dmUserIds), ("$sender", message.SenderId),
            ("$sender_name", message.SenderDisplayName), ("$sender_avatar", message.SenderAvatarUrl), ("$content", message.Content),
            ("$timestamp", message.Timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
            ("$read", message.IsRead ? 1 : 0), ("$starred", message.IsStarred ? 1 : 0)).ConfigureAwait(false);
    }

    private static ChatMessage ReadMessage(SqliteDataReader reader)
    {
        ConversationKey conversation = reader.GetString(1) switch
        {
            "channel" => new ChannelTopic(reader.GetInt64(2), reader.GetString(3)),
            "dm" => new DirectMessage(ParseIds(reader.IsDBNull(4) ? string.Empty : reader.GetString(4))),
            _ => throw new InvalidDataException("Unknown conversation kind.")
        };
        return new ChatMessage(
            reader.GetInt64(0), conversation, reader.GetInt64(5), reader.GetString(6),
            DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            reader.GetInt64(8) != 0,
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.GetInt64(11) != 0);
    }

    private static async Task PopulateReactionsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        IDictionary<long, ChatMessage> messages,
        CancellationToken cancellationToken)
    {
        if (messages.Count == 0) return;
        var reactions = messages.Keys.ToDictionary(id => id, _ => new List<EmojiReaction>());
        await using var command = CreateCommand(connection, transaction, string.Empty);
        var ids = messages.Keys.ToArray();
        var parameterNames = new string[ids.Length];
        for (var index = 0; index < ids.Length; index++)
        {
            parameterNames[index] = $"$message{index}";
            command.Parameters.AddWithValue(parameterNames[index], ids[index]);
        }
        command.CommandText = $"""
            SELECT message_id, reaction_type, emoji_code, emoji_name, user_id, user_full_name
            FROM message_reactions
            WHERE message_id IN ({string.Join(',', parameterNames)})
            ORDER BY message_id, reaction_type, emoji_code, user_id;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var messageId = reader.GetInt64(0);
            if (!reactions.TryGetValue(messageId, out var list)) continue;
            list.Add(new EmojiReaction(
                new EmojiReactionIdentity(reader.GetString(3), reader.GetString(2), reader.GetString(1)),
                reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        foreach (var pair in reactions)
        {
            messages[pair.Key] = messages[pair.Key] with { Reactions = pair.Value.ToArray() };
        }

    }

    private static IEnumerable<long> ParseIds(string value) =>
        string.IsNullOrEmpty(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(id => long.Parse(id, CultureInfo.InvariantCulture));

    private static async Task ReplaceRecentDirectMessagesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<ConversationKey> conversations,
        CancellationToken cancellationToken)
    {
        await ExecuteNonQueryAsync(connection, "DELETE FROM recent_dm;", transaction, cancellationToken).ConfigureAwait(false);
        foreach (var direct in conversations.OfType<DirectMessage>())
        {
            await ExecuteAsync(connection, transaction,
                "INSERT INTO recent_dm(canonical_key, participant_ids) VALUES($key, $ids);",
                cancellationToken, ("$key", direct.CanonicalKey), ("$ids", string.Join(',', direct.OtherUserIds))).ConfigureAwait(false);
        }
    }

    private static async Task<IReadOnlyList<ConversationKey>> ReadRecentDirectMessagesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT canonical_key, participant_ids FROM recent_dm ORDER BY canonical_key;";
        var conversations = new List<ConversationKey>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var direct = new DirectMessage(ParseIds(reader.GetString(1)));
            if (!string.Equals(reader.GetString(0), direct.CanonicalKey, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Recent direct-message identity is inconsistent.");
            }
            conversations.Add(direct);
        }
        return conversations;
    }

    private static async Task<StoredAccount?> ReadAccountAsync(
        SqliteConnection connection,
        AccountId? expected,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT account_id, realm, email, user_id FROM account_metadata WHERE singleton = 1;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        var realm = RealmEndpoint.Parse(reader.GetString(1));
        var generated = AccountId.Create(realm, reader.GetInt64(3));
        if (generated.Value != reader.GetString(0) || expected is { } expectedId && generated != expectedId)
        {
            throw new InvalidDataException("Account metadata identity does not match its database.");
        }
        return new StoredAccount(generated, realm, reader.GetString(2), reader.GetInt64(3));
    }

    private static async Task<bool> ReadCacheUnlockedAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT cache_unlocked FROM account_metadata WHERE singleton = 1;";
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is null) throw new InvalidOperationException("Account metadata is missing.");
        return Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0;
    }

    private static async Task EnsureUnlockedAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        if (!await ReadCacheUnlockedAsync(connection, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The account cache is locked.");
        }
    }

    private static async Task<SqliteConnection> OpenAsync(string path, bool create, CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ExecuteNonQueryAsync(connection, "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;", null, cancellationToken).ConfigureAwait(false);
            await ExecuteNonQueryAsync(connection, "PRAGMA journal_mode = WAL;", null, cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private string RequireDatabasePath(AccountId accountId)
    {
        var path = GetDatabasePath(accountId);
        return File.Exists(path) ? path : throw new InvalidOperationException("Account is not initialized.");
    }

    private string GetDatabasePath(AccountId accountId) => Path.Combine(GetAccountDirectory(accountId), "relaycove.db");

    private string GetAccountDirectory(AccountId accountId)
    {
        var value = accountId.Value;
        if (value is null || !AccountDirectoryName().IsMatch(value)) throw new ArgumentException("Invalid account id.", nameof(accountId));
        return Path.Combine(_accountsRoot, value);
    }

    private static void ValidateAccount(StoredAccount account)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(account.Email);
        if (account.UserId <= 0 || AccountId.Create(account.Realm, account.UserId) != account.AccountId)
        {
            throw new ArgumentException("Stored account identity is inconsistent.", nameof(account));
        }
    }

    private static SqliteCommand CreateCommand(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = CreateCommand(connection, transaction, sql);
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExecuteNonQueryAsync(
        SqliteConnection connection,
        string sql,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, sql);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<long> ExecuteScalarLongAsync(
        SqliteConnection connection,
        string sql,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, sql);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex AccountDirectoryName();

    private interface IWorkItem
    {
        Task RunAsync();
    }

    private sealed class WorkItem<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken) : IWorkItem
    {
        private readonly TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<T> Task => _completion.Task;

        public async Task RunAsync()
        {
            if (cancellationToken.IsCancellationRequested)
            {
                _completion.TrySetCanceled(cancellationToken);
                return;
            }
            try
            {
                _completion.TrySetResult(await operation(cancellationToken).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _completion.TrySetCanceled(cancellationToken);
            }
            catch (Exception exception)
            {
                _completion.TrySetException(exception);
            }
        }
    }
}
