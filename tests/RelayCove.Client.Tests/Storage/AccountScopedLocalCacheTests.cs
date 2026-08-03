using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RelayCove.Client.Notifications;
using RelayCove.Client.Storage;
using RelayCove.Client.Sync;
using RelayCove.Shared.Conversations;
using RelayCove.Shared.Messages;

namespace RelayCove.Client.Tests.Storage;

[Collection(SqliteTestCollection.Name)]
public sealed class AccountScopedLocalCacheTests : IDisposable
{
    private static readonly Guid UserId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly Guid OtherUserId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private readonly string rootDirectory = Path.Combine(
        Path.GetTempPath(),
        "RelayCove.Client.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task MergeIncomingMessage_WhenRepeatedOrConflicting_ReturnsDeterministicResult()
    {
        var identity = CreateIdentity(UserId);
        await using var cache = await CreateCacheAsync(identity);
        var conversation = CreateConversation();
        var message = CreateMessage(conversation.Id);
        await RegisterAsync(cache, conversation);

        var inserted = await cache.MergeIncomingMessageAsync(message);
        var duplicate = await cache.MergeIncomingMessageAsync(message);
        var conflict = await cache.MergeIncomingMessageAsync(message with { Content = "different" });
        var read = await cache.ReadMessagesAsync(conversation.Id);

        Assert.Equal(IncomingMessageMergeResult.Inserted, inserted.Result);
        Assert.Equal(IncomingMessageMergeResult.Duplicate, duplicate.Result);
        Assert.Equal(IncomingMessageMergeResult.Conflict, conflict.Result);
        Assert.Equal(LocalCacheOperationStatus.Ready, read.Status);
        AssertMessage(message, Assert.Single(read.Messages));
        Assert.Equal(1, Scalar(identity, "SELECT COUNT(*) FROM LocalMessages;"));
        Assert.Equal(2, Scalar(identity, "SELECT COUNT(*) FROM LocalMessageMentions;"));
    }

    [Fact]
    public async Task MergeIncomingMessage_WhenPendingExists_PromotesThenDuplicates()
    {
        var identity = CreateIdentity(UserId);
        await using var cache = await CreateCacheAsync(identity);
        var conversation = CreateConversation();
        var message = CreateMessage(conversation.Id) with { SenderId = UserId };
        await RegisterAsync(cache, conversation);
        var pending = new PendingMessage(
            message.ClientMessageId,
            message.ConversationId,
            message.SenderId,
            "local display",
            message.Type,
            message.Content,
            message.ReplyToMessageId,
            message.MentionUserIds,
            message.CreatedAt.AddMinutes(-1));

        Assert.DoesNotContain(message.Content!, pending.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(pending.SenderDisplayName, pending.ToString(), StringComparison.Ordinal);

        Assert.Equal(LocalCacheOperationStatus.Ready, await cache.AddPendingMessageAsync(pending));
        var promoted = await cache.MergeIncomingMessageAsync(message);
        var duplicate = await cache.MergeIncomingMessageAsync(message);
        var read = await cache.ReadMessagesAsync(conversation.Id);

        Assert.Equal(IncomingMessageMergeResult.PendingPromoted, promoted.Result);
        Assert.Equal(IncomingMessageMergeResult.Duplicate, duplicate.Result);
        AssertMessage(message, Assert.Single(read.Messages));
        Assert.Equal((long)MessageSendStatus.Sent, Scalar(
            identity,
            "SELECT LocalSendStatus FROM LocalMessages LIMIT 1;"));
    }

    [Fact]
    public async Task MergeIncomingMessage_WhenConcurrentDuplicates_InsertsOneRow()
    {
        var identity = CreateIdentity(UserId);
        await using var firstCache = await CreateCacheAsync(identity);
        await using var secondCache = await CreateCacheAsync(identity);
        var conversation = CreateConversation();
        var message = CreateMessage(conversation.Id);
        await RegisterAsync(firstCache, conversation);
        await RegisterAsync(secondCache, conversation);

        var outcomes = await Task.WhenAll(
            Enumerable.Range(0, 12)
                .Select(index => (index & 1) == 0
                    ? firstCache.MergeIncomingMessageAsync(message)
                    : secondCache.MergeIncomingMessageAsync(message)));

        Assert.Single(outcomes, outcome => outcome.Result == IncomingMessageMergeResult.Inserted);
        Assert.Equal(11, outcomes.Count(outcome => outcome.Result == IncomingMessageMergeResult.Duplicate));
        Assert.DoesNotContain(outcomes, outcome => outcome.Result == IncomingMessageMergeResult.Conflict);
        Assert.Equal(1, Scalar(identity, "SELECT COUNT(*) FROM LocalMessages;"));
    }

    [Fact]
    public async Task LocalCacheRealtimeEventSink_WhenConversationIsUnknown_RejectsAndRequestsReconciliation()
    {
        var identity = CreateIdentity(UserId);
        await using var cache = await CreateCacheAsync(identity);
        var requested = new ConcurrentQueue<Guid>();
        var sink = new LocalCacheRealtimeEventSink(
            cache,
            (conversationId, _) =>
            {
                requested.Enqueue(conversationId);
                return Task.CompletedTask;
            },
            NullLogger<LocalCacheRealtimeEventSink>.Instance);
        var message = CreateMessage(Guid.NewGuid());

        await sink.OnNewMessageAsync(message, CancellationToken.None);

        Assert.Equal(message.ConversationId, Assert.Single(requested));
        Assert.Equal(0, Scalar(identity, "SELECT COUNT(*) FROM LocalConversations;"));
        Assert.Equal(0, Scalar(identity, "SELECT COUNT(*) FROM LocalMessages;"));
    }

    [Fact]
    public async Task LocalCacheRealtimeEventSink_WhenForegroundMergeCommits_RequestsReadThroughUpload()
    {
        var identity = CreateIdentity(UserId);
        await using var cache = await CreateCacheAsync(identity);
        var conversation = CreateConversation();
        await RegisterAsync(cache, conversation);
        var uploadRequests = 0;
        var sink = new LocalCacheRealtimeEventSink(
            cache,
            (_, _) => Task.CompletedTask,
            () => conversation.Id,
            NullLogger<LocalCacheRealtimeEventSink>.Instance,
            () => Interlocked.Increment(ref uploadRequests));

        await sink.OnNewMessageAsync(CreateMessage(conversation.Id), CancellationToken.None);

        Assert.Equal(1, uploadRequests);
        Assert.Equal(
            1,
            Scalar(
                identity,
                "SELECT COUNT(*) FROM LocalConversations WHERE PendingReadThroughMessageId IS NOT NULL;"));
    }

    [Fact]
    public async Task LocalCacheRealtimeEventSink_WhenCandidateCommits_ForwardsExplicitId()
    {
        var identity = CreateIdentity(UserId);
        await using var cache = await CreateCacheAsync(identity);
        var conversation = CreateConversation();
        await RegisterAsync(cache, conversation);
        var notifications = new RecordingNotificationRoundCoordinator();
        var sink = new LocalCacheRealtimeEventSink(
            cache,
            (_, _) => Task.CompletedTask,
            foregroundConversationIdProvider: null,
            NullLogger<LocalCacheRealtimeEventSink>.Instance,
            requestReadThroughUpload: null,
            notifications);

        await sink.OnNewMessageAsync(CreateMessage(conversation.Id), CancellationToken.None);

        Assert.Equal([101L], notifications.RealtimeCandidates);
    }

    [Fact]
    public async Task LocalCacheRealtimeEventSink_WhenRevocationCommits_ForwardsPlatformClear()
    {
        var identity = CreateIdentity(UserId);
        await using var cache = await CreateCacheAsync(identity);
        var conversation = CreateConversation();
        await RegisterAsync(cache, conversation);
        var notifications = new RecordingNotificationRoundCoordinator();
        var sink = new LocalCacheRealtimeEventSink(
            cache,
            (_, _) => Task.CompletedTask,
            foregroundConversationIdProvider: null,
            NullLogger<LocalCacheRealtimeEventSink>.Instance,
            requestReadThroughUpload: null,
            notifications);

        await sink.OnConversationAccessRevokedAsync(conversation.Id, CancellationToken.None);

        Assert.Equal([conversation.Id], notifications.RevokedConversations);
    }

    [Fact]
    public async Task RevokeConversationAccess_WhenPersisted_DeletesCascadeAndSurvivesRestart()
    {
        var identity = CreateIdentity(UserId);
        var conversation = CreateConversation();
        var message = CreateMessage(conversation.Id);
        await using (var cache = await CreateCacheAsync(identity))
        {
            await RegisterAsync(cache, conversation);
            Assert.Equal(
                IncomingMessageMergeResult.Inserted,
                (await cache.MergeIncomingMessageAsync(message)).Result);

            Assert.Equal(
                LocalCacheOperationStatus.RevokedConversation,
                await cache.RevokeConversationAccessAsync(conversation.Id));
            Assert.Equal(
                LocalCacheOperationStatus.RevokedConversation,
                (await cache.MergeIncomingMessageAsync(message with { Id = message.Id + 1 })).Status);
            Assert.Equal(
                LocalCacheOperationStatus.RevokedConversation,
                (await cache.ReadMessagesAsync(conversation.Id)).Status);
        }

        Assert.Equal(0, Scalar(identity, "SELECT COUNT(*) FROM LocalConversations;"));
        Assert.Equal(0, Scalar(identity, "SELECT COUNT(*) FROM LocalMessages;"));
        Assert.Equal(0, Scalar(identity, "SELECT COUNT(*) FROM LocalMessageMentions;"));
        Assert.Equal(1, Scalar(identity, "SELECT COUNT(*) FROM RevokedConversations;"));

        await using var restarted = await CreateCacheAsync(identity);
        Assert.Equal(
            LocalCacheOperationStatus.RevokedConversation,
            await restarted.RegisterAuthoritativeConversationAsync(conversation));
        Assert.Equal(
            LocalCacheOperationStatus.RevokedConversation,
            (await restarted.ReadMessagesAsync(conversation.Id)).Status);
    }

    [Fact]
    public async Task RevokeConversationAccess_WhenCallerIsAlreadyCanceled_StillPersistsTombstone()
    {
        var identity = CreateIdentity(UserId);
        var conversation = CreateConversation();
        await using var cache = await CreateCacheAsync(identity);
        await RegisterAsync(cache, conversation);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var status = await cache.RevokeConversationAccessAsync(
            conversation.Id,
            cancellation.Token);

        Assert.Equal(LocalCacheOperationStatus.RevokedConversation, status);
        Assert.Equal(1, Scalar(identity, "SELECT COUNT(*) FROM RevokedConversations;"));
    }

    [Fact]
    public async Task RevokeConversationAccess_WhenTombstoneFails_ReplaysIntentAfterRestart()
    {
        var identity = CreateIdentity(UserId);
        var conversation = CreateConversation();
        var message = CreateMessage(conversation.Id);
        var logger = new RecordingLogger<AccountScopedLocalCache>();
        await using (var cache = await AccountScopedLocalCache.CreateAsync(
            identity,
            logger,
            new ThrowingFaultInjector()))
        {
            await RegisterAsync(cache, conversation);
            await cache.MergeIncomingMessageAsync(message);

            Assert.Equal(
                LocalCacheOperationStatus.FatalScope,
                await cache.RevokeConversationAccessAsync(conversation.Id));
            Assert.True(cache.IsFatal);
            Assert.Equal(
                LocalCacheOperationStatus.FatalScope,
                (await cache.ReadMessagesAsync(conversation.Id)).Status);
            Assert.Equal(
                LocalCacheOperationStatus.FatalScope,
                (await cache.MergeIncomingMessageAsync(message with { Id = message.Id + 1 })).Status);
        }

        Assert.Equal(1, Scalar(
            identity,
            "SELECT COUNT(*) FROM LocalAppState WHERE Key LIKE 'RevocationIntent/%';"));

        AccountScopedLocalCache.ResetProcessStateForTest(identity);
        await using var restarted = await CreateCacheAsync(identity);
        Assert.False(restarted.IsFatal);
        Assert.Equal(
            LocalCacheOperationStatus.RevokedConversation,
            await restarted.RegisterAuthoritativeConversationAsync(conversation));
        Assert.Equal(1, Scalar(identity, "SELECT COUNT(*) FROM RevokedConversations;"));
        Assert.Equal(0, Scalar(identity, "SELECT COUNT(*) FROM LocalMessages;"));
        Assert.Equal(0, Scalar(
            identity,
            "SELECT COUNT(*) FROM LocalAppState WHERE Key LIKE 'RevocationIntent/%';"));
        Assert.DoesNotContain(identity.DatabasePath, string.Join(' ', logger.Messages), StringComparison.Ordinal);
        Assert.DoesNotContain(conversation.Id.ToString(), string.Join(' ', logger.Messages), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RevokeConversationAccess_WhileTombstoneIsPending_BlocksReadAndLateMessage()
    {
        var identity = CreateIdentity(UserId);
        var faultInjector = new BlockingFaultInjector();
        await using var cache = await AccountScopedLocalCache.CreateAsync(
            identity,
            NullLogger<AccountScopedLocalCache>.Instance,
            faultInjector);
        await using var secondCache = await CreateCacheAsync(identity);
        var conversation = CreateConversation();
        var message = CreateMessage(conversation.Id);
        await RegisterAsync(cache, conversation);
        await RegisterAsync(secondCache, conversation);
        await cache.MergeIncomingMessageAsync(message);

        var revokeTask = cache.RevokeConversationAccessAsync(conversation.Id);
        Assert.True(faultInjector.Entered.Wait(TimeSpan.FromSeconds(5)));

        Assert.Equal(
            LocalCacheOperationStatus.RevokedConversation,
            (await secondCache.ReadMessagesAsync(conversation.Id)).Status);
        Assert.Equal(
            LocalCacheOperationStatus.RevokedConversation,
            (await cache.MergeIncomingMessageAsync(message with { Id = message.Id + 1 })).Status);

        faultInjector.Release.Set();
        Assert.Equal(LocalCacheOperationStatus.RevokedConversation, await revokeTask);
        Assert.Equal(0, Scalar(identity, "SELECT COUNT(*) FROM LocalMessages;"));
    }

    [Fact]
    public async Task CacheScopes_WhenConversationIdsMatch_IsolateRevocationByAccount()
    {
        var firstIdentity = CreateIdentity(UserId);
        var secondIdentity = CreateIdentity(OtherUserId);
        await using var firstCache = await CreateCacheAsync(firstIdentity);
        await using var secondCache = await CreateCacheAsync(secondIdentity);
        var conversation = CreateConversation();
        var firstMessage = CreateMessage(conversation.Id);
        var secondMessage = CreateMessage(conversation.Id) with { Id = 200 };
        await RegisterAsync(firstCache, conversation);
        await RegisterAsync(secondCache, conversation);
        await firstCache.MergeIncomingMessageAsync(firstMessage);
        await secondCache.MergeIncomingMessageAsync(secondMessage);

        await firstCache.RevokeConversationAccessAsync(conversation.Id);

        Assert.Equal(
            LocalCacheOperationStatus.RevokedConversation,
            (await firstCache.ReadMessagesAsync(conversation.Id)).Status);
        var secondRead = await secondCache.ReadMessagesAsync(conversation.Id);
        Assert.Equal(LocalCacheOperationStatus.Ready, secondRead.Status);
        AssertMessage(secondMessage, Assert.Single(secondRead.Messages));
        Assert.NotEqual(firstIdentity.DatabasePath, secondIdentity.DatabasePath);
    }

    [Fact]
    public async Task CacheRestart_UntilConversationIsAuthoritativelyRegistered_DoesNotExposeOldMessages()
    {
        var identity = CreateIdentity(UserId);
        var conversation = CreateConversation();
        var message = CreateMessage(conversation.Id);
        await using (var cache = await CreateCacheAsync(identity))
        {
            await RegisterAsync(cache, conversation);
            await cache.MergeIncomingMessageAsync(message);
        }

        await using var restarted = await CreateCacheAsync(identity);
        Assert.Equal(
            LocalCacheOperationStatus.UnknownConversation,
            (await restarted.ReadMessagesAsync(conversation.Id)).Status);

        await RegisterAsync(restarted, conversation);
        AssertMessage(
            message,
            Assert.Single((await restarted.ReadMessagesAsync(conversation.Id)).Messages));
    }

    [Fact]
    public async Task CreateAsync_WhenSchemaVersionIsNewer_RejectsDatabaseWithoutDowngrade()
    {
        var identity = CreateIdentity(UserId);
        await using (var cache = await CreateCacheAsync(identity))
        {
        }

        Execute(identity, "PRAGMA user_version=2;");

        await Assert.ThrowsAsync<InvalidDataException>(() => CreateCacheAsync(identity));
        Assert.Equal(2, Scalar(identity, "PRAGMA user_version;"));
    }

    [Fact]
    public async Task CreateAsync_UsesNativeSQLiteVersionWithSecurityFix()
    {
        var identity = CreateIdentity(UserId);
        await using var cache = await CreateCacheAsync(identity);

        var nativeVersion = Version.Parse(TextScalar(identity, "SELECT sqlite_version();"));

        Assert.True(
            nativeVersion >= new Version(3, 50, 2),
            $"Expected SQLite 3.50.2 or newer, but resolved {nativeVersion}.");
    }

    [Fact]
    public async Task LocalCacheRealtimeEventSink_WhenConflictOrFatal_DoesNotLogSensitiveValues()
    {
        var identity = CreateIdentity(UserId);
        var cacheLogger = new RecordingLogger<AccountScopedLocalCache>();
        var sinkLogger = new RecordingLogger<LocalCacheRealtimeEventSink>();
        await using var cache = await AccountScopedLocalCache.CreateAsync(
            identity,
            cacheLogger,
            new ThrowingFaultInjector());
        var conversation = CreateConversation();
        var message = CreateMessage(conversation.Id) with { Content = "token-secret-content" };
        await RegisterAsync(cache, conversation);
        await cache.MergeIncomingMessageAsync(message);
        var notifications = new RecordingNotificationRoundCoordinator();
        var sink = new LocalCacheRealtimeEventSink(
            cache,
            (_, _) => Task.CompletedTask,
            foregroundConversationIdProvider: null,
            sinkLogger,
            requestReadThroughUpload: null,
            notifications);

        await sink.OnNewMessageAsync(message with { Content = "conflicting-token-secret" }, CancellationToken.None);
        await sink.OnConversationAccessRevokedAsync(conversation.Id, CancellationToken.None);

        var logs = string.Join(' ', cacheLogger.Messages.Concat(sinkLogger.Messages));
        Assert.DoesNotContain("token-secret", logs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(conversation.Id.ToString(), logs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(message.Id.ToString(), logs, StringComparison.Ordinal);
        Assert.DoesNotContain(identity.DatabasePath, logs, StringComparison.OrdinalIgnoreCase);
        Assert.Equal([conversation.Id], notifications.RevokedConversations);
    }

    [Fact]
    public async Task ReadConversationListAsync_WhenSnapshotMissing_DoesNotExposePersistedRows()
    {
        var identity = CreateIdentity(UserId);
        var conversation = CreateConversation();
        await using (var firstCache = await CreateCacheAsync(identity))
        {
            Assert.Equal(
                LocalCacheOperationStatus.Ready,
                await firstCache.ApplyAuthoritativeConversationSnapshotAsync(
                    new ConversationListResponse([conversation], Complete: true)));
            Assert.Single((await firstCache.ReadConversationListAsync()).Conversations);
        }

        await using var restartedCache = await CreateCacheAsync(identity);

        var outcome = await restartedCache.ReadConversationListAsync();

        Assert.Equal(
            LocalCacheOperationStatus.AuthoritativeSnapshotRequired,
            outcome.Status);
        Assert.Empty(outcome.Conversations);
        Assert.Equal(0, outcome.TotalUnreadCount);
    }

    [Fact]
    public async Task ReadConversationListAsync_WhenReady_ReturnsStablePreviewAndSaturatedUnread()
    {
        var identity = CreateIdentity(UserId);
        await using var cache = await CreateCacheAsync(identity);
        var updatedAt = DateTimeOffset.Parse("2026-08-03T12:00:00Z");
        var firstId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var secondId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var olderId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var first = new ConversationDto(
            firstId,
            ConversationType.PrivateChannel,
            "First",
            "https://images.example/first.png",
            updatedAt.AddDays(-1),
            updatedAt,
            LastMessageId: 10,
            LastReadMessageId: 0,
            UnreadCount: int.MaxValue,
            IsMuted: true);
        var second = new ConversationDto(
            secondId,
            ConversationType.Direct,
            "Second",
            null,
            updatedAt.AddDays(-1),
            updatedAt,
            LastMessageId: 11,
            LastReadMessageId: 0,
            UnreadCount: 1);
        var older = new ConversationDto(
            olderId,
            ConversationType.PublicChannel,
            "Older",
            null,
            updatedAt.AddDays(-2),
            updatedAt.AddMinutes(-1),
            LastMessageId: 0,
            LastReadMessageId: 0,
            UnreadCount: 0);
        Assert.Equal(
            LocalCacheOperationStatus.Ready,
            await cache.ApplyAuthoritativeConversationSnapshotAsync(
                new ConversationListResponse([older, second, first], Complete: true)));
        Assert.Equal(
            IncomingMessageMergeResult.Inserted,
            (await cache.MergeIncomingMessageAsync(CreateMessage(firstId) with
            {
                Id = 10,
                Content = "latest\ntext",
                CreatedAt = updatedAt,
            })).Result);
        Assert.Equal(
            IncomingMessageMergeResult.Inserted,
            (await cache.MergeIncomingMessageAsync(CreateMessage(secondId) with
            {
                Id = 11,
                Type = MessageType.Image,
                Content = null,
                CreatedAt = updatedAt,
            })).Result);

        var outcome = await cache.ReadConversationListAsync();

        Assert.Equal(LocalCacheOperationStatus.Ready, outcome.Status);
        Assert.Equal([firstId, secondId, olderId], outcome.Conversations.Select(x => x.Id));
        Assert.Equal(int.MaxValue, outcome.TotalUnreadCount);
        var firstItem = outcome.Conversations[0];
        Assert.Equal(MessageType.Text, firstItem.LastMessageType);
        Assert.Equal("latest\ntext", firstItem.LastMessageContent);
        Assert.Equal(updatedAt, firstItem.LastMessageCreatedAt);
        Assert.True(firstItem.IsMuted);
        Assert.Equal(MessageType.Image, outcome.Conversations[1].LastMessageType);
        Assert.Null(outcome.Conversations[2].LastMessageType);
        Assert.Null(outcome.Conversations[2].LastMessageContent);
        var listView = Assert.IsAssignableFrom<IList<LocalConversationListItem>>(
            outcome.Conversations);
        Assert.Throws<NotSupportedException>(() => listView.Add(firstItem));
    }

    [Fact]
    public async Task ReadConversationListAsync_WhenLastMessagePointsAcrossConversation_DoesNotLeakPreview()
    {
        var identity = CreateIdentity(UserId);
        await using var cache = await CreateCacheAsync(identity);
        var source = CreateConversation() with { Id = Guid.NewGuid(), LastMessageId = 101 };
        var target = CreateConversation() with { Id = Guid.NewGuid(), LastMessageId = 0 };
        Assert.Equal(
            LocalCacheOperationStatus.Ready,
            await cache.ApplyAuthoritativeConversationSnapshotAsync(
                new ConversationListResponse([source, target], Complete: true)));
        Assert.Equal(
            IncomingMessageMergeResult.Inserted,
            (await cache.MergeIncomingMessageAsync(CreateMessage(source.Id))).Result);
        Execute(
            identity,
            $"UPDATE LocalConversations SET LastMessageId = 101 WHERE Id = '{target.Id:D}';");

        var outcome = await cache.ReadConversationListAsync();

        var targetItem = Assert.Single(outcome.Conversations, item => item.Id == target.Id);
        Assert.Null(targetItem.LastMessageType);
        Assert.Null(targetItem.LastMessageContent);
    }

    [Fact]
    public async Task ReadConversationListAsync_WhenOneRowIsCorrupt_ExcludesItWithoutFatalScope()
    {
        var identity = CreateIdentity(UserId);
        await using var cache = await CreateCacheAsync(identity);
        var conversation = CreateConversation();
        Assert.Equal(
            LocalCacheOperationStatus.Ready,
            await cache.ApplyAuthoritativeConversationSnapshotAsync(
                new ConversationListResponse([conversation], Complete: true)));
        Execute(
            identity,
            $"UPDATE LocalConversations SET UpdatedAt = 'not-a-date' " +
            $"WHERE Id = '{conversation.Id:D}';");

        var outcome = await cache.ReadConversationListAsync();

        Assert.Equal(LocalCacheOperationStatus.Ready, outcome.Status);
        Assert.Empty(outcome.Conversations);
        Assert.Equal(0, outcome.TotalUnreadCount);
        Assert.False(cache.IsFatal);
    }

    [Fact]
    public async Task ConversationStateChanged_WhenObserverReadsOrThrows_DoesNotDeadlockOrPoisonCommit()
    {
        var identity = CreateIdentity(UserId);
        await using var cache = await CreateCacheAsync(identity);
        var observedStatuses = new ConcurrentQueue<LocalCacheOperationStatus>();
        var trailingObserverCount = 0;
        cache.ConversationStateChanged += _ => observedStatuses.Enqueue(
            cache.ReadConversationListAsync().GetAwaiter().GetResult().Status);
        cache.ConversationStateChanged += _ => throw new InvalidOperationException(
            "observer failure");
        cache.ConversationStateChanged += _ => Interlocked.Increment(
            ref trailingObserverCount);

        var status = await cache.ApplyAuthoritativeConversationSnapshotAsync(
            new ConversationListResponse([CreateConversation()], Complete: true));

        Assert.Equal(LocalCacheOperationStatus.Ready, status);
        Assert.Equal([LocalCacheOperationStatus.Ready], observedStatuses);
        Assert.Equal(1, trailingObserverCount);
        Assert.False(cache.IsFatal);
    }

    [Fact]
    public async Task ConversationStateChanged_WhenListMutationsCommit_RaisesMonotonicRevisions()
    {
        var identity = CreateIdentity(UserId);
        await using var cache = await CreateCacheAsync(identity);
        var conversation = CreateConversation() with
        {
            LastMessageId = 0,
            LastReadMessageId = 0,
            UnreadCount = 0,
        };
        var message = CreateMessage(conversation.Id) with { Id = 1 };
        var revisions = new ConcurrentQueue<long>();
        cache.ConversationStateChanged += revisions.Enqueue;

        Assert.Equal(
            LocalCacheOperationStatus.Ready,
            await cache.ApplyAuthoritativeConversationSnapshotAsync(
                new ConversationListResponse([conversation], Complete: true)));
        Assert.Equal(
            LocalCacheOperationStatus.Ready,
            (await cache.ApplySyncPageAsync(
                new SyncResponse([message], NextCursor: 1, SnapshotUpperBound: 1, HasMore: false),
                expectedCursor: 0,
                expectedSnapshotUpperBound: null)).Status);
        Assert.Equal(
            IncomingMessageMergeResult.Duplicate,
            (await cache.MergeIncomingMessageAsync(message)).Result);
        Assert.Equal(
            LocalCacheOperationStatus.RevokedConversation,
            await cache.RevokeConversationAccessAsync(conversation.Id));

        Assert.Equal([1L, 2L, 3L, 4L], revisions);
        var outcome = await cache.ReadConversationListAsync();
        Assert.Equal(LocalCacheOperationStatus.Ready, outcome.Status);
        Assert.Empty(outcome.Conversations);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(rootDirectory))
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    private AccountScopeIdentity CreateIdentity(Guid userId) => AccountScopeIdentity.Create(
        new Uri("https://relaycove.example/team/"),
        userId,
        rootDirectory);

    private static Task<AccountScopedLocalCache> CreateCacheAsync(AccountScopeIdentity identity) =>
        AccountScopedLocalCache.CreateAsync(
            identity,
            NullLogger<AccountScopedLocalCache>.Instance);

    private static async Task RegisterAsync(
        AccountScopedLocalCache cache,
        ConversationDto conversation) =>
        Assert.Equal(
            LocalCacheOperationStatus.Ready,
            await cache.RegisterAuthoritativeConversationAsync(conversation));

    private static ConversationDto CreateConversation() => new(
        Guid.NewGuid(),
        ConversationType.PrivateChannel,
        "Private channel",
        null,
        DateTimeOffset.Parse("2026-08-03T01:00:00Z"),
        DateTimeOffset.Parse("2026-08-03T02:00:00Z"),
        100,
        90,
        2);

    private static MessageDto CreateMessage(Guid conversationId) => new(
        101,
        Guid.NewGuid(),
        conversationId,
        Guid.NewGuid(),
        "Sender",
        MessageType.Text,
        "hello",
        null,
        Array.Empty<AttachmentDto>(),
        new[] { Guid.NewGuid(), Guid.NewGuid() },
        DateTimeOffset.Parse("2026-08-03T03:00:00Z"));

    private static long Scalar(AccountScopeIdentity identity, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = identity.DatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            ForeignKeys = true,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static void Execute(AccountScopeIdentity identity, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = identity.DatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            ForeignKeys = true,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string TextScalar(AccountScopeIdentity identity, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = identity.DatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            ForeignKeys = true,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar())!;
    }

    private static void AssertMessage(MessageDto expected, MessageDto actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.ClientMessageId, actual.ClientMessageId);
        Assert.Equal(expected.ConversationId, actual.ConversationId);
        Assert.Equal(expected.SenderId, actual.SenderId);
        Assert.Equal(expected.SenderDisplayName, actual.SenderDisplayName);
        Assert.Equal(expected.Type, actual.Type);
        Assert.Equal(expected.Content, actual.Content);
        Assert.Equal(expected.ReplyToMessageId, actual.ReplyToMessageId);
        Assert.Equal(expected.CreatedAt, actual.CreatedAt);
        Assert.Equal(expected.Attachments, actual.Attachments);
        Assert.Equal(expected.MentionUserIds.Order(), actual.MentionUserIds.Order());
    }

    private sealed class ThrowingFaultInjector : ILocalCacheFaultInjector
    {
        public void BeforeRevocationTombstone(Guid conversationId) =>
            throw new IOException("Injected revocation failure with token-secret-content.");
    }

    private sealed class BlockingFaultInjector : ILocalCacheFaultInjector
    {
        public ManualResetEventSlim Entered { get; } = new(initialState: false);

        public ManualResetEventSlim Release { get; } = new(initialState: false);

        public void BeforeRevocationTombstone(Guid conversationId)
        {
            Entered.Set();
            if (!Release.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("The revocation test gate timed out.");
            }
        }
    }

    private sealed class RecordingNotificationRoundCoordinator :
        IClientNotificationRoundCoordinator
    {
        public List<long> RealtimeCandidates { get; } = [];

        public List<Guid> RevokedConversations { get; } = [];

        public ClientNotificationRoundToken OpenRound(SyncReason reason) =>
            new(1, reason);

        public Task SnapshotCommittedAsync(
            ClientNotificationRoundToken token,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public void SubmitSyncCandidates(
            ClientNotificationRoundToken token,
            IReadOnlyCollection<long> messageIds)
        {
        }

        public Task SubmitRealtimeCandidateAsync(
            long messageId,
            CancellationToken cancellationToken)
        {
            RealtimeCandidates.Add(messageId);
            return Task.CompletedTask;
        }

        public Task CloseRoundAsync(
            ClientNotificationRoundToken token,
            ClientSyncRunStatus status) => Task.CompletedTask;

        public Task ConversationRevokedAsync(
            Guid conversationId,
            CancellationToken cancellationToken)
        {
            RevokedConversations.Add(conversationId);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Enqueue(formatter(state, exception));
    }
}
