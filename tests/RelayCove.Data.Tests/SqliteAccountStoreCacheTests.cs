using RelayCove.Core;

namespace RelayCove.Data.Tests;

public sealed class SqliteAccountStoreCacheTests
{
    [Fact]
    public async Task InitializeAsync_WhenExistingCacheIsLocked_PreservesFailClosedState()
    {
        await using var context = StoreTestContext.Create();
        var account = StoreTestData.Account();
        await context.Store.InitializeAsync(account);
        Assert.True(await context.Store.IsCacheUnlockedAsync(account.AccountId));
        await context.Store.SetCacheUnlockedAsync(account.AccountId, false);

        await context.Store.InitializeAsync(account);

        Assert.False(await context.Store.IsCacheUnlockedAsync(account.AccountId));
        Assert.False((await context.Store.LoadAsync(account.AccountId))!.IsCacheUnlocked);
    }

    [Fact]
    public async Task LoadAsync_WhenCacheIsLocked_HidesMessagesAndQueryFailsClosedUntilUnlocked()
    {
        await using var context = StoreTestContext.Create();
        var account = StoreTestData.Account();
        var conversation = new ChannelTopic(1, "general");
        await context.Store.InitializeAsync(account);
        await context.Store.ReplaceRegisterSnapshotAsync(account.AccountId, StoreTestData.Register(
            [new Subscription(1, "General")],
            [new MessageUpsertEvent(StoreTestData.Message(1, conversation), Source: DomainEventSource.Register)]));

        await context.Store.SetCacheUnlockedAsync(account.AccountId, false);

        var locked = await context.Store.LoadAsync(account.AccountId);
        Assert.False(locked!.IsCacheUnlocked);
        Assert.Empty(locked.State.Messages);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            context.Store.QueryMessagesAsync(account.AccountId, conversation, null, 20));

        await context.Store.SetCacheUnlockedAsync(account.AccountId, true);
        var unlocked = await context.Store.LoadAsync(account.AccountId);
        Assert.True(unlocked!.IsCacheUnlocked);
        Assert.Single(unlocked.State.Messages);
    }

    [Fact]
    public async Task ReplaceRegisterSnapshotAsync_WhenSubscriptionDisappears_PurgesItsTopicsMessagesAndUnreadAtomically()
    {
        await using var context = StoreTestContext.Create();
        var account = StoreTestData.Account();
        var removed = new ChannelTopic(1, "old");
        var retained = new ChannelTopic(2, "keep");
        await context.Store.InitializeAsync(account);
        await context.Store.ReplaceRegisterSnapshotAsync(account.AccountId, StoreTestData.Register(
            [new Subscription(1, "Old"), new Subscription(2, "Keep")],
            [
                new TopicUpsertEvent(new TopicSummary(1, "old"), Source: DomainEventSource.Register),
                new TopicUpsertEvent(new TopicSummary(2, "keep"), Source: DomainEventSource.Register),
                new MessageUpsertEvent(StoreTestData.Message(1, removed), Source: DomainEventSource.Register),
                new MessageUpsertEvent(StoreTestData.Message(2, retained), Source: DomainEventSource.Register)
            ],
            unread: new UnreadState(new Dictionary<string, int>
            {
                [removed.CanonicalKey] = 1,
                [retained.CanonicalKey] = 1
            }, 2)));

        await context.Store.ReplaceRegisterSnapshotAsync(account.AccountId, StoreTestData.Register(
            [new Subscription(2, "Keep")],
            unread: new UnreadState(new Dictionary<string, int> { [retained.CanonicalKey] = 1 }, 1)));

        var state = (await context.Store.LoadAsync(account.AccountId))!.State;
        Assert.DoesNotContain(1, state.Subscriptions.Keys);
        Assert.DoesNotContain(1, state.Messages.Keys);
        Assert.DoesNotContain(removed.CanonicalKey, state.Topics.Keys);
        Assert.Equal(1, state.Unread.Total);
        Assert.Contains(2, state.Messages.Keys);
    }

    [Fact]
    public async Task ApplyBatchAsync_WhenEditingMovingDeletingAndFlagging_PersistsAllEventEffects()
    {
        await using var context = StoreTestContext.Create();
        var account = StoreTestData.Account();
        var source = new ChannelTopic(1, "source");
        var destination = new ChannelTopic(2, "destination");
        await context.Store.InitializeAsync(account);
        await context.Store.ReplaceRegisterSnapshotAsync(account.AccountId, StoreTestData.Register(
            [new Subscription(1, "One"), new Subscription(2, "Two")],
            [
                new MessageUpsertEvent(StoreTestData.Message(1, source), Source: DomainEventSource.Register),
                new MessageUpsertEvent(StoreTestData.Message(2, source), Source: DomainEventSource.Register),
                new MessageUpsertEvent(StoreTestData.Message(3, source), Source: DomainEventSource.Register)
            ],
            unread: new UnreadState(new Dictionary<string, int> { [source.CanonicalKey] = 3 }, 3)));

        await context.Store.ApplyBatchAsync(account.AccountId,
        [
            new MessageContentChangedEvent(1, "edited"),
            new MessageMovedEvent([1L, 2L], destination),
            new MessageFlagsChangedEvent([1L], false, MessageFlagOperation.Add, "read"),
            new MessageDeletedEvent([2L, 3L]),
            new SubscriptionPatchedEvent(2, "Renamed", false),
            new UserPatchedEvent(10, "New Sender", null, null)
        ]);

        var state = (await context.Store.LoadAsync(account.AccountId))!.State;
        Assert.Single(state.Messages);
        Assert.Equal("edited", state.Messages[1].Content);
        Assert.Equal(destination, state.Messages[1].Conversation);
        Assert.True(state.Messages[1].IsRead);
        Assert.Equal("Renamed", state.Subscriptions[2].Name);
        Assert.False(state.Subscriptions[2].IsActive);
        Assert.Equal("New Sender", state.Users[10].FullName);
        Assert.Equal(0, state.Unread.Total);
    }

    [Fact]
    public async Task ApplyBatchAsync_WhenWriteFails_RollsBackWholeBatch()
    {
        await using var context = StoreTestContext.Create();
        var account = StoreTestData.Account();
        var conversation = new DirectMessage([]);
        await context.Store.InitializeAsync(account);
        await context.Store.ApplyBatchAsync(account.AccountId,
            [new MessageUpsertEvent(StoreTestData.Message(1, conversation))]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Store.ApplyBatchAsync(account.AccountId,
            [
                new MessageContentChangedEvent(1, "must-roll-back"),
                new MessageUpsertEvent(StoreTestData.Message(2, new UnsupportedConversation()))
            ]));

        var state = (await context.Store.LoadAsync(account.AccountId))!.State;
        Assert.Single(state.Messages);
        Assert.Equal("message-1", state.Messages[1].Content);
    }

    [Fact]
    public async Task ApplyBatchAsync_WhenCallsAreConcurrent_SerializesWithoutLostMessages()
    {
        await using var context = StoreTestContext.Create();
        var account = StoreTestData.Account();
        await context.Store.InitializeAsync(account);

        var writes = Enumerable.Range(1, 30)
            .Select(id => context.Store.ApplyBatchAsync(account.AccountId,
                [new MessageUpsertEvent(StoreTestData.Message(id, new DirectMessage([])))]));
        await Task.WhenAll(writes);

        var state = (await context.Store.LoadAsync(account.AccountId))!.State;
        Assert.Equal(30, state.Messages.Count);
    }

    [Fact]
    public async Task ClearAsync_WhenOneAccountIsSelected_DeletesOnlyItsExactDirectory()
    {
        await using var context = StoreTestContext.Create();
        var one = StoreTestData.Account("https://one.example/", 10);
        var two = StoreTestData.Account("https://two.example/", 20);
        await context.Store.InitializeAsync(one);
        await context.Store.InitializeAsync(two);

        await context.Store.ClearAsync(one.AccountId);

        Assert.False(File.Exists(context.DatabasePath(one.AccountId)));
        Assert.True(File.Exists(context.DatabasePath(two.AccountId)));
        Assert.Null(await context.Store.LoadAsync(one.AccountId));
        Assert.NotNull(await context.Store.LoadAsync(two.AccountId));
    }

    [Fact]
    public async Task QueryMessagesAsync_WhenHistoryContainsMarkdownAndDirectMessages_ReturnsConversationPageAndPreservesUnreadMetadata()
    {
        await using var context = StoreTestContext.Create();
        var account = StoreTestData.Account();
        var channel = new ChannelTopic(1, "topic");
        var direct = new DirectMessage([20, 30]);
        await context.Store.InitializeAsync(account);
        await context.Store.ReplaceRegisterSnapshotAsync(account.AccountId, StoreTestData.Register(
            [new Subscription(1, "One")],
            [
                new MessageUpsertEvent(StoreTestData.Message(1, channel, content: "**raw** [link](https://example.test)"), Source: DomainEventSource.Register),
                new MessageUpsertEvent(StoreTestData.Message(2, channel), Source: DomainEventSource.Register),
                new MessageUpsertEvent(StoreTestData.Message(3, channel), Source: DomainEventSource.Register),
                new MessageUpsertEvent(StoreTestData.Message(4, direct), Source: DomainEventSource.Register)
            ],
            [direct],
            new UnreadState(new Dictionary<string, int> { [channel.CanonicalKey] = 3 }, 99, true)));

        var page = await context.Store.QueryMessagesAsync(account.AccountId, channel, 3, 2);
        var loaded = await context.Store.LoadAsync(account.AccountId);

        Assert.Equal([1L, 2L], page.Select(message => message.Id));
        Assert.Equal("**raw** [link](https://example.test)", page[0].Content);
        Assert.Equal(99, loaded!.State.Unread.ReportedTotal);
        Assert.True(loaded.State.Unread.IsTruncated);
        Assert.Equal(direct, Assert.Single(loaded.RecentDirectMessages));
        await using var connection = context.Open(account.AccountId);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM recent_dm;";
        Assert.Equal(1L, Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task ApplyBatchAsync_WhenCoreUpsertsAndOutboxEventsArrive_PersistsDomainRowsButNotEphemeralState()
    {
        await using var context = StoreTestContext.Create();
        var account = StoreTestData.Account();
        var conversation = new ChannelTopic(7, "build");
        await context.Store.InitializeAsync(account);

        await context.Store.ApplyBatchAsync(account.AccountId,
        [
            new SubscriptionChangedEvent(new Subscription(7, "Build"), false),
            new UserUpsertEvent(new UserProfile(70, "Builder")),
            new TopicUpsertEvent(new TopicSummary(7, "build", 50)),
            new MessageUpsertEvent(StoreTestData.Message(50, conversation)),
            new MessagesUpdatedEvent([StoreTestData.Message(50, conversation, content: "updated")]),
            new OutboxQueuedEvent(new OutboxEntry("123", conversation, "ephemeral", DateTimeOffset.UnixEpoch))
        ]);

        var state = (await context.Store.LoadAsync(account.AccountId))!.State;
        Assert.Equal("updated", state.Messages[50].Content);
        Assert.Equal("Builder", state.Users[70].FullName);
        Assert.Equal(50, state.Topics[conversation.CanonicalKey].MaxMessageId);
        Assert.Empty(state.Outbox);
        Assert.Null(state.LastEventId);
    }

    [Fact]
    public async Task ClientSession_WhenRealtimeBatchReplaysOldAndNullEvents_DoesNotRestoreThemFromDatabase()
    {
        await using var context = StoreTestContext.Create();
        var conversation = new DirectMessage([20]);
        var message = StoreTestData.Message(9, conversation);
        var gateway = new ReplayGateway(message);
        await using var session = new ClientSession(gateway, context.Store, new MemoryCredentialVault());

        await session.LoginAsync("https://one.example/", "user10@example.test", "password");
        await WaitUntilAsync(() => gateway.GetEventsCalls >= 3);

        var accountId = Assert.IsType<AccountId>(session.AccountId);
        Assert.Empty(session.State.Messages);
        Assert.Equal(2, session.State.LastEventId);
        await session.StopAsync();
        var restored = await context.Store.LoadAsync(accountId);
        Assert.Empty(restored!.State.Messages);
        Assert.Null(restored.State.LastEventId);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }

    private sealed class MemoryCredentialVault : ICredentialVault
    {
        private CredentialEnvelope? _credential;

        public Task<CredentialEnvelope?> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_credential);

        public Task SetAsync(CredentialEnvelope credentials, CancellationToken cancellationToken = default)
        {
            _credential = credentials;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(CancellationToken cancellationToken = default)
        {
            _credential = null;
            return Task.CompletedTask;
        }
    }

    private sealed class ReplayGateway(ChatMessage message) : IZulipGateway
    {
        private int _getEventsCalls;

        public int GetEventsCalls => Volatile.Read(ref _getEventsCalls);

        public Task<RealmProbeResult> ProbeRealmAsync(RealmEndpoint realm, CancellationToken cancellationToken = default) =>
            Task.FromResult(new RealmProbeResult(realm, "12.1", 500, false, true));

        public Task<AuthenticationResult> AuthenticateAsync(
            AuthenticationRequest request,
            CancellationToken cancellationToken = default)
        {
            var credentials = new CredentialEnvelope(request.Realm, request.Email, 10, "memory-only-key");
            return Task.FromResult(new AuthenticationResult(
                credentials,
                new UserProfile(10, "Sender", request.Email)));
        }

        public Task<RegisterResult> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new RegisterResult(
                "queue",
                1,
                TimeSpan.FromSeconds(30),
                10_000,
                200,
                [],
                [new UserProfile(10, "Sender", "user10@example.test")],
                [message.Conversation],
                new UnreadState(),
                [new MessageUpsertEvent(message, 1, DomainEventSource.Register)]));

        public Task<EventBatch> GetEventsAsync(GetEventsRequest request, CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _getEventsCalls);
            return call switch
            {
                1 => Task.FromResult(new EventBatch([new MessageDeletedEvent([message.Id], 2)], 2)),
                2 => Task.FromResult(new EventBatch(
                    [new MessageUpsertEvent(message, 1), new HeartbeatEvent()], 1)),
                _ => Never<EventBatch>(cancellationToken)
            };
        }

        public Task<HistoryResult> GetHistoryAsync(HistoryRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new HistoryResult([], false, false));

        public Task<TopicsResult> GetTopicsAsync(TopicsRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new TopicsResult([]));

        public Task<SendResult> SendAsync(SendRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task MarkReadAsync(MarkReadRequest request, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DeleteQueueAsync(DeleteQueueRequest request, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        private static Task<T> Never<T>(CancellationToken cancellationToken)
        {
            var source = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(() => source.TrySetCanceled(cancellationToken));
            return source.Task;
        }
    }

    private sealed record UnsupportedConversation : ConversationKey
    {
        public override string CanonicalKey => "unsupported";
    }
}
