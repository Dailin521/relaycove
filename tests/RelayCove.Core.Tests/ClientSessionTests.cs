using System.Collections.Concurrent;

namespace RelayCove.Core.Tests;

public sealed class ClientSessionTests
{
    [Fact]
    public async Task RestoreAsync_WhenCredentialVaultCannotBeRead_LocksKnownCachesAndExposesNoAccount()
    {
        var credential = Credential();
        var store = new FakeAccountStore { Account = Stored(credential), IsUnlocked = true };
        var vault = new FakeCredentialVault { GetFailure = new InvalidOperationException("safe failure") };
        await using var session = new ClientSession(new FakeGateway(), store, vault);

        var restored = await session.RestoreAsync();

        Assert.False(restored);
        Assert.False(store.IsUnlocked);
        Assert.Null(session.AccountId);
        Assert.Empty(session.State.Messages);
        Assert.Equal(ConnectionStatus.Locked, session.State.Connection.Status);
    }

    [Fact]
    public async Task LoginAsync_WhenRealmAndCredentialsAreValid_InitializesVaultBeforeRegisterAndPublishesSnapshot()
    {
        var log = new List<string>();
        var gateway = new FakeGateway(log);
        var store = new FakeAccountStore(log);
        var vault = new FakeCredentialVault(log);
        var recent = new DirectMessage([20, 30]);
        gateway.RegisterHandler = (_, _) => Task.FromResult(Register(recent: [recent]));
        await using var session = new ClientSession(gateway, store, vault);

        await session.LoginAsync("https://zulip.example/", "me@example.test", "password");

        Assert.Equal(ConnectionStatus.Connected, session.State.Connection.Status);
        Assert.NotNull(session.AccountId);
        Assert.Equal(recent, Assert.Single(session.RecentDirectMessages));
        Assert.True(log.IndexOf("vault:set") < log.IndexOf("gateway:register"));
        Assert.True(log.IndexOf("store:migrate") < log.IndexOf("vault:set"));
        await session.StopAsync();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LoginAsync_WhenVaultOrRegisterFails_LocksCacheAndRemovesPartialCredential(bool vaultFails)
    {
        var gateway = new FakeGateway();
        var store = new FakeAccountStore();
        var vault = new FakeCredentialVault { SetFailure = vaultFails ? new InvalidOperationException("vault") : null };
        if (!vaultFails)
        {
            gateway.RegisterHandler = (_, _) => Task.FromException<RegisterResult>(
                new GatewayException(GatewayErrorKind.Server, GatewayErrorCode.ServerError));
        }
        await using var session = new ClientSession(gateway, store, vault);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            session.LoginAsync("https://zulip.example/", "me@example.test", "password"));

        Assert.Equal(ConnectionStatus.Locked, session.State.Connection.Status);
        Assert.False(store.IsUnlocked);
        Assert.True(vault.RemoveCalls > 0);
        Assert.Null(vault.Credential);
    }

    [Theory]
    [InlineData("probe")]
    [InlineData("authenticate")]
    [InlineData("initialize")]
    [InlineData("migrate")]
    public async Task LoginAsync_WhenPreCredentialStageFails_ReturnsToSignedOut(string stage)
    {
        var gateway = new FakeGateway
        {
            ProbeFailure = stage == "probe" ? new InvalidOperationException(stage) : null,
            AuthenticateFailure = stage == "authenticate" ? new InvalidOperationException(stage) : null
        };
        var store = new FakeAccountStore
        {
            InitializeFailure = stage == "initialize" ? new InvalidOperationException(stage) : null,
            MigrateFailure = stage == "migrate" ? new InvalidOperationException(stage) : null
        };
        var vault = new FakeCredentialVault();
        await using var session = new ClientSession(gateway, store, vault);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.LoginAsync("https://zulip.example/", "me@example.test", "password"));

        Assert.Equal(ConnectionStatus.SignedOut, session.State.Connection.Status);
        Assert.Null(session.AccountId);
        Assert.Null(vault.Credential);
    }

    [Fact]
    public async Task LoginAsync_WhenAuthenticationIsCancelled_ReturnsToSignedOut()
    {
        var gateway = new FakeGateway { BlockAuthenticationUntilCancelled = true };
        await using var session = new ClientSession(gateway, new FakeAccountStore(), new FakeCredentialVault());
        using var cancellation = new CancellationTokenSource();

        var login = session.LoginAsync("https://zulip.example/", "me@example.test", "password", cancellation.Token);
        await gateway.AuthenticateEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => login);
        Assert.Equal(ConnectionStatus.SignedOut, session.State.Connection.Status);
        Assert.Null(session.AccountId);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task LoginAsync_WhenSecurityCleanupFails_PropagatesAndPreservesAnySuccessfulBarrier(
        bool removeFails,
        bool lockFails)
    {
        var gateway = new FakeGateway
        {
            RegisterHandler = (_, _) => Task.FromException<RegisterResult>(
                new GatewayException(GatewayErrorKind.Server, GatewayErrorCode.ServerError))
        };
        var store = new FakeAccountStore
        {
            LockFailure = lockFails ? new InvalidOperationException("lock") : null
        };
        var vault = new FakeCredentialVault
        {
            RemoveFailure = removeFails ? new InvalidOperationException("remove") : null
        };
        await using var session = new ClientSession(gateway, store, vault);

        await Assert.ThrowsAsync<AggregateException>(() =>
            session.LoginAsync("https://zulip.example/", "me@example.test", "password"));

        Assert.Equal(ConnectionStatus.Faulted, session.State.Connection.Status);
        Assert.True(vault.RemoveCalls > 0);
        Assert.True(store.LockAttempts > 0);
        if (removeFails && !lockFails)
        {
            await using var restored = new ClientSession(new FakeGateway(), store, vault);
            Assert.False(await restored.RestoreAsync());
            Assert.NotEqual(ConnectionStatus.Connected, restored.State.Connection.Status);
        }
        if (!removeFails && lockFails)
        {
            await using var restored = new ClientSession(new FakeGateway(), store, vault);
            Assert.False(await restored.RestoreAsync());
            Assert.Null(restored.AccountId);
        }
    }

    [Fact]
    public async Task RestoreAsync_WhenCredentialExists_PublishesUnlockedCacheBeforeRegisterAndDerivesRecentDm()
    {
        var credential = Credential();
        var dm = new DirectMessage([44]);
        var persistedDm = new DirectMessage([55]);
        var cachedMessage = Message(1, dm);
        var store = new FakeAccountStore
        {
            Account = Stored(credential),
            SnapshotState = new ClientState(messages: new Dictionary<long, ChatMessage> { [1] = cachedMessage }),
            RecentDirectMessages = [persistedDm],
            IsUnlocked = true
        };
        var vault = new FakeCredentialVault { Credential = credential };
        var registerSource = new TaskCompletionSource<RegisterResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = new FakeGateway
        {
            RegisterHandler = (_, _) => registerSource.Task
        };
        await using var session = new ClientSession(gateway, store, vault);

        var restore = session.RestoreAsync();
        await gateway.RegisterEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(ConnectionStatus.Offline, session.State.Connection.Status);
        Assert.Contains(1, session.State.Messages.Keys);
        Assert.Equal(2, session.RecentDirectMessages.Count);
        Assert.Contains(dm, session.RecentDirectMessages);
        Assert.Contains(persistedDm, session.RecentDirectMessages);
        Assert.True(store.IsUnlocked);

        registerSource.SetResult(Register(recent: [dm]));
        Assert.True(await restore);
        await session.StopAsync();
    }

    [Fact]
    public async Task RestoreAsync_WhenResidualCredentialTargetsLockedCache_RefusesToUnlockOrRegister()
    {
        var credential = Credential();
        var store = new FakeAccountStore
        {
            Account = Stored(credential),
            SnapshotState = new ClientState(messages: new Dictionary<long, ChatMessage>
            {
                [1] = Message(1, new DirectMessage([99]))
            }),
            IsUnlocked = false
        };
        var vault = new FakeCredentialVault { Credential = credential };
        var gateway = new FakeGateway();
        await using var session = new ClientSession(gateway, store, vault);

        var restored = await session.RestoreAsync();

        Assert.False(restored);
        Assert.False(store.IsUnlocked);
        Assert.Null(vault.Credential);
        Assert.Empty(session.State.Messages);
        Assert.Equal(ConnectionStatus.Locked, session.State.Connection.Status);
        Assert.Equal(0, gateway.RegisterCalls);
    }

    [Fact]
    public async Task LogoutAsync_WhenSessionIsActive_StopsLoopDeletesQueueLocksCacheAndClearsMemory()
    {
        var gateway = new FakeGateway();
        var store = new FakeAccountStore();
        var vault = new FakeCredentialVault();
        await using var session = new ClientSession(gateway, store, vault);
        await session.LoginAsync("https://zulip.example/", "me@example.test", "password");

        await session.LogoutAsync();
        await session.LogoutAsync();

        Assert.Equal(1, gateway.DeleteQueueCalls);
        Assert.True(vault.RemoveCalls >= 2);
        Assert.False(store.IsUnlocked);
        Assert.Null(session.AccountId);
        Assert.Empty(session.State.Messages);
        Assert.Equal(ConnectionStatus.SignedOut, session.State.Connection.Status);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task LogoutAsync_WhenSecurityCleanupFails_FaultsAndReportsEveryFailedBarrier(
        bool removeFails,
        bool lockFails)
    {
        var gateway = new FakeGateway();
        var store = new FakeAccountStore();
        var vault = new FakeCredentialVault();
        await using var session = new ClientSession(gateway, store, vault);
        await session.LoginAsync("https://zulip.example/", "me@example.test", "password");
        var accountId = Assert.IsType<AccountId>(session.AccountId);
        vault.RemoveFailure = removeFails ? new InvalidOperationException("remove") : null;
        store.LockFailure = lockFails ? new InvalidOperationException("lock") : null;

        var exception = await Assert.ThrowsAsync<AggregateException>(() => session.LogoutAsync());

        Assert.Equal((removeFails ? 1 : 0) + (lockFails ? 1 : 0), exception.InnerExceptions.Count);
        Assert.Equal(ConnectionStatus.Faulted, session.State.Connection.Status);
        Assert.Equal("logout_cleanup_failed", session.State.Connection.Detail);
        Assert.Equal(accountId, session.AccountId);
        Assert.Empty(session.State.Messages);
        Assert.True(vault.RemoveCalls > 0);
        Assert.True(store.LockAttempts > 0);
        Assert.Equal(removeFails, vault.Credential is not null);
        Assert.Equal(lockFails, store.IsUnlocked);
        if (removeFails ^ lockFails)
        {
            await using var restored = new ClientSession(new FakeGateway(), store, vault);
            Assert.False(await restored.RestoreAsync());
            Assert.NotEqual(ConnectionStatus.Connected, restored.State.Connection.Status);
        }
    }

    [Fact]
    public async Task LogoutAsync_WhenCallerCancelsDuringQueueDeletion_StillCompletesSecurityCleanup()
    {
        using var cancellation = new CancellationTokenSource();
        var gateway = new FakeGateway
        {
            DeleteQueueHandler = (_, token) =>
            {
                cancellation.Cancel();
                return Task.FromCanceled(token);
            }
        };
        var store = new FakeAccountStore();
        var vault = new FakeCredentialVault();
        await using var session = new ClientSession(gateway, store, vault);
        await session.LoginAsync("https://zulip.example/", "me@example.test", "password");

        await session.LogoutAsync(cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Null(vault.Credential);
        Assert.False(store.IsUnlocked);
        Assert.Null(session.AccountId);
        Assert.Equal(ConnectionStatus.SignedOut, session.State.Connection.Status);
    }

    [Fact]
    public async Task EventLoop_WhenUnauthorized_RemovesCredentialLocksCacheAndDoesNotRetry()
    {
        var gateway = new FakeGateway
        {
            GetEventsHandler = (_, _) => Task.FromException<EventBatch>(
                new GatewayException(GatewayErrorKind.ReauthRequired, GatewayErrorCode.Unauthorized, 401))
        };
        var store = new FakeAccountStore();
        var vault = new FakeCredentialVault();
        await using var session = new ClientSession(gateway, store, vault);

        await session.LoginAsync("https://zulip.example/", "me@example.test", "password");
        await WaitUntilAsync(() => session.State.Connection.Status == ConnectionStatus.ReauthRequired);

        Assert.Equal(1, gateway.GetEventsCalls);
        Assert.Null(vault.Credential);
        Assert.False(store.IsUnlocked);
        Assert.Empty(session.State.Messages);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task EventLoop_WhenUnauthorizedCleanupFails_PublishesFaultedAndAttemptsBothBarriers(
        bool removeFails,
        bool lockFails)
    {
        var gateway = new FakeGateway
        {
            GetEventsHandler = (_, _) => Task.FromException<EventBatch>(
                new GatewayException(GatewayErrorKind.ReauthRequired, GatewayErrorCode.Unauthorized, 401))
        };
        var store = new FakeAccountStore
        {
            LockFailure = lockFails ? new InvalidOperationException("lock") : null
        };
        var vault = new FakeCredentialVault
        {
            RemoveFailure = removeFails ? new InvalidOperationException("remove") : null
        };
        await using var session = new ClientSession(gateway, store, vault);

        await session.LoginAsync("https://zulip.example/", "me@example.test", "password");
        await WaitUntilAsync(() => session.State.Connection.Status == ConnectionStatus.Faulted);

        Assert.Equal(1, gateway.GetEventsCalls);
        Assert.True(vault.RemoveCalls > 0);
        Assert.True(store.LockAttempts > 0);
        if (removeFails ^ lockFails)
        {
            await using var restored = new ClientSession(new FakeGateway(), store, vault);
            Assert.False(await restored.RestoreAsync());
            Assert.NotEqual(ConnectionStatus.Connected, restored.State.Connection.Status);
        }
    }

    [Fact]
    public async Task EventLoop_WhenOldOrNullEventGroupsReplay_DoesNotPersistThemAndCursorNeverRegresses()
    {
        var conversation = new DirectMessage([20]);
        var message = Message(9, conversation);
        var gateway = new FakeGateway
        {
            RegisterHandler = (_, _) => Task.FromResult(Register(
                events: [new MessageUpsertEvent(message, 1, DomainEventSource.Register)]))
        };
        gateway.GetEventsHandler = (_, cancellationToken) => gateway.GetEventsCalls switch
        {
            1 => Task.FromResult(new EventBatch([new MessageDeletedEvent([9L], 3)], 2)),
            2 => Task.FromResult(new EventBatch(
                [new MessageUpsertEvent(message, 1), new HeartbeatEvent()], 1)),
            _ => Never<EventBatch>(cancellationToken)
        };
        var store = new FakeAccountStore();
        await using var session = new ClientSession(gateway, store, new FakeCredentialVault());

        await session.LoginAsync("https://zulip.example/", "me@example.test", "password");
        await WaitUntilAsync(() => gateway.GetEventsCalls >= 3);

        Assert.Empty(session.State.Messages);
        Assert.Equal(3, session.State.LastEventId);
        Assert.Single(store.AppliedBatches);
        Assert.IsType<MessageDeletedEvent>(Assert.Single(store.AppliedBatches[0]));
        Assert.Empty(store.SnapshotState.Messages);
        await session.StopAsync();
    }

    [Fact]
    public async Task EventLoop_WhenRealtimeDirectMessageArrives_AddsConversationWithoutRetainingNonCurrentMessage()
    {
        var direct = new DirectMessage([77]);
        var gateway = new FakeGateway();
        gateway.GetEventsHandler = (_, cancellationToken) => gateway.GetEventsCalls == 1
            ? Task.FromResult(new EventBatch([new MessageUpsertEvent(Message(7, direct), 2)], 2))
            : Never<EventBatch>(cancellationToken);
        var sawNavigationAtPublish = false;
        await using var session = new ClientSession(gateway, new FakeAccountStore(), new FakeCredentialVault());
        session.StateChanged += (_, _) =>
        {
            if (session.RecentDirectMessages.Contains(direct))
            {
                sawNavigationAtPublish = true;
            }
        };

        await session.LoginAsync("https://zulip.example/", "me@example.test", "password");
        await WaitUntilAsync(() => session.RecentDirectMessages.Contains(direct));

        Assert.True(sawNavigationAtPublish);
        Assert.Contains(direct, session.RecentDirectMessages);
        Assert.DoesNotContain(7, session.State.Messages.Keys);
        await session.StopAsync();
    }

    [Fact]
    public async Task EventLoop_WhenQueueExpires_ReregistersWithoutRetryingPostCommands()
    {
        var gateway = new FakeGateway();
        gateway.RegisterHandler = (_, _) => Task.FromResult(Register(queue: $"queue-{gateway.RegisterCalls}"));
        gateway.GetEventsHandler = (request, cancellationToken) => gateway.GetEventsCalls == 1
            ? Task.FromException<EventBatch>(new GatewayException(GatewayErrorKind.QueueExpired, GatewayErrorCode.BadEventQueueId))
            : Never<EventBatch>(cancellationToken);
        await using var session = new ClientSession(gateway, new FakeAccountStore(), new FakeCredentialVault());

        await session.LoginAsync("https://zulip.example/", "me@example.test", "password");
        await WaitUntilAsync(() => gateway.RegisterCalls >= 2 && gateway.GetEventsCalls >= 2);

        Assert.Equal(2, gateway.RegisterCalls);
        Assert.Equal(ConnectionStatus.Connected, session.State.Connection.Status);
        await session.StopAsync();
    }

    [Fact]
    public async Task EventLoop_WhenServerRestarts_ReprobesWaitsAndRegistersNewQueueBeforePollingAgain()
    {
        var delays = new ControlledDelay();
        var gateway = new FakeGateway();
        gateway.RegisterHandler = (_, _) => Task.FromResult(Register(queue: $"queue-{gateway.RegisterCalls}"));
        gateway.GetEventsHandler = (_, cancellationToken) => gateway.GetEventsCalls == 1
            ? Task.FromResult(new EventBatch([new ServerRestartedEvent(500, 2)], 2))
            : Never<EventBatch>(cancellationToken);
        await using var session = new ClientSession(
            gateway,
            new FakeAccountStore(),
            new FakeCredentialVault(),
            delays.DelayAsync,
            serverRestartDelay: () => TimeSpan.FromMinutes(5));

        await session.LoginAsync("https://zulip.example/", "me@example.test", "password");
        await WaitUntilAsync(() => gateway.ProbeCalls == 2);
        Assert.Equal(ConnectionStatus.Reconnecting, session.State.Connection.Status);
        Assert.Single(gateway.GetEventsRequests);

        await delays.CompleteNextAsync(TimeSpan.FromMinutes(5));
        await WaitUntilAsync(() => gateway.RegisterCalls == 2 && gateway.GetEventsRequests.Count == 2);

        Assert.Equal("queue-1", gateway.GetEventsRequests[0].QueueId);
        Assert.Equal("queue-2", gateway.GetEventsRequests[1].QueueId);
        Assert.Equal(ConnectionStatus.Connected, session.State.Connection.Status);
        await session.StopAsync();
    }

    [Fact]
    public async Task EventLoop_WhenRateLimited_WaitsRetryAfterBeforeRetryingGet()
    {
        var delays = new ControlledDelay();
        var retryAfter = TimeSpan.FromSeconds(17);
        var gateway = new FakeGateway();
        gateway.GetEventsHandler = (_, cancellationToken) => gateway.GetEventsCalls == 1
            ? Task.FromException<EventBatch>(new GatewayException(
                GatewayErrorKind.RateLimited, GatewayErrorCode.RateLimited, 429, retryAfter))
            : Never<EventBatch>(cancellationToken);
        await using var session = new ClientSession(
            gateway, new FakeAccountStore(), new FakeCredentialVault(), delays.DelayAsync);

        await session.LoginAsync("https://zulip.example/", "me@example.test", "password");
        await WaitUntilAsync(() => session.State.Connection.Status == ConnectionStatus.RateLimited);
        Assert.Equal(1, gateway.GetEventsCalls);

        await delays.CompleteNextAsync(retryAfter);
        await WaitUntilAsync(() => gateway.GetEventsCalls == 2);

        await session.StopAsync();
    }

    [Fact]
    public async Task EventLoop_WhenLocalStoreFails_StopsAndPublishesFaultedState()
    {
        var gateway = new FakeGateway
        {
            GetEventsHandler = (_, _) => Task.FromResult(
                new EventBatch([new HeartbeatEvent(2)], 2))
        };
        var store = new FakeAccountStore
        {
            ApplyFailure = new InvalidOperationException("database unavailable")
        };
        await using var session = new ClientSession(gateway, store, new FakeCredentialVault());

        await session.LoginAsync("https://zulip.example/", "me@example.test", "password");
        await WaitUntilAsync(() => session.State.Connection.Status == ConnectionStatus.Faulted);

        Assert.Equal("event_loop_failed", session.State.Connection.Detail);
        Assert.Equal(1, gateway.GetEventsCalls);
    }

    [Fact]
    public async Task SelectConversationAsync_WhenCacheExists_PublishesCacheBeforeNewestHistoryAndOlderUsesOriginalAnchor()
    {
        var conversation = new ChannelTopic(1, "general");
        var newestGate = new TaskCompletionSource<HistoryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = new FakeGateway();
        gateway.RegisterHandler = (_, _) => Task.FromResult(Register(subscriptions: [new Subscription(1, "General")]));
        gateway.HistoryHandler = (request, _) => request.AnchorMessageId is null
            ? newestGate.Task
            : Task.FromResult(new HistoryResult([], true, false));
        var store = new FakeAccountStore
        {
            QueryHandler = (_, _, before, _, _) => Task.FromResult<IReadOnlyList<ChatMessage>>(
                before is null
                    ? Enumerable.Range(51, 50).Select(id => Message(id, conversation)).ToArray()
                    : Enumerable.Range(1, 50).Select(id => Message(id, conversation)).ToArray())
        };
        await using var session = new ClientSession(gateway, store, new FakeCredentialVault());
        await session.LoginAsync("https://zulip.example/", "me@example.test", "password");

        var select = session.SelectConversationAsync(conversation);
        await WaitUntilAsync(() => gateway.HistoryRequests.Count == 1);
        Assert.Equal(50, session.State.Messages.Count);
        Assert.Contains(51, session.State.Messages.Keys);
        Assert.True(gateway.HistoryRequests[0].IncludeAnchor);
        Assert.Equal(50, gateway.HistoryRequests[0].Limit);
        newestGate.SetResult(new HistoryResult([], false, true));
        await select;

        await session.LoadOlderAsync();

        var older = Assert.Single(gateway.HistoryRequests, request => request.AnchorMessageId is not null);
        Assert.Equal(51, older.AnchorMessageId);
        Assert.False(older.IncludeAnchor);
        Assert.Equal(50, older.Limit);
        await session.StopAsync();
    }

    [Fact]
    public async Task LoadOlderAsync_WhenCalledConcurrently_SharesOneRequestAndDeduplicatesOverlap()
    {
        var conversation = new ChannelTopic(1, "general");
        var olderGate = new TaskCompletionSource<HistoryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = new FakeGateway
        {
            RegisterHandler = (_, _) => Task.FromResult(Register(subscriptions: [new Subscription(1, "General")])),
            HistoryHandler = (request, _) => request.AnchorMessageId is null
                ? Task.FromResult(new HistoryResult(
                    Enumerable.Range(101, 50).Select(id => Message(id, conversation)).ToArray(), false, true))
                : olderGate.Task
        };
        await using var session = new ClientSession(gateway, new FakeAccountStore(), new FakeCredentialVault());
        await session.LoginAsync("https://zulip.example/", "me@example.test", "password");
        await session.SelectConversationAsync(conversation);

        var first = session.LoadOlderAsync();
        var second = session.LoadOlderAsync();
        Assert.Same(first, second);
        await WaitUntilAsync(() => gateway.HistoryRequests.Count == 2);
        olderGate.SetResult(new HistoryResult(
            Enumerable.Range(52, 50).Select(id => Message(id, conversation)).ToArray(), false, false));
        await Task.WhenAll(first, second);

        Assert.Equal(2, gateway.HistoryRequests.Count);
        Assert.Equal(99, session.State.Messages.Count);
        Assert.Equal(50, gateway.HistoryRequests[1].Limit);
        await session.StopAsync();
    }

    [Fact]
    public async Task LoadOlderAsync_WhenCacheHasAGap_UsesTheOriginalNetworkAnchor()
    {
        var conversation = new ChannelTopic(1, "general");
        var gateway = new FakeGateway
        {
            RegisterHandler = (_, _) => Task.FromResult(Register(subscriptions: [new Subscription(1, "General")])),
            HistoryHandler = (request, _) => request.AnchorMessageId is null
                ? Task.FromResult(new HistoryResult(
                    Enumerable.Range(100, 50).Select(id => Message(id, conversation)).ToArray(), false, true))
                : Task.FromResult(new HistoryResult(
                    Enumerable.Range(51, 49).Select(id => Message(id, conversation)).ToArray(), false, false))
        };
        var store = new FakeAccountStore
        {
            QueryHandler = (_, _, before, _, _) => Task.FromResult<IReadOnlyList<ChatMessage>>(
                before == 100
                    ? Enumerable.Range(1, 50).Select(id => Message(id, conversation)).ToArray()
                    : [])
        };
        await using var session = new ClientSession(gateway, store, new FakeCredentialVault());
        await session.LoginAsync("https://zulip.example/", "me@example.test", "password");
        await session.SelectConversationAsync(conversation);

        await session.LoadOlderAsync();

        var older = Assert.Single(gateway.HistoryRequests, request => request.AnchorMessageId is not null);
        Assert.Equal(100, older.AnchorMessageId);
        Assert.Contains(51, session.State.Messages.Keys);
        Assert.Contains(99, session.State.Messages.Keys);
        await session.StopAsync();
    }

    [Fact]
    public async Task SelectConversationAsync_WhenOldResponseCompletesAfterSwitch_StoresButDoesNotProjectIt()
    {
        var firstConversation = new ChannelTopic(1, "first");
        var secondConversation = new ChannelTopic(2, "second");
        var firstGate = new TaskCompletionSource<HistoryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new FakeAccountStore();
        var gateway = new FakeGateway
        {
            RegisterHandler = (_, _) => Task.FromResult(Register(subscriptions:
                [new Subscription(1, "First"), new Subscription(2, "Second")])),
            HistoryHandler = (request, _) => request.Conversation == firstConversation
                ? firstGate.Task
                : Task.FromResult(new HistoryResult([Message(200, secondConversation)], true, true))
        };
        await using var session = new ClientSession(gateway, store, new FakeCredentialVault());
        await session.LoginAsync("https://zulip.example/", "me@example.test", "password");

        var firstSelection = session.SelectConversationAsync(firstConversation);
        await WaitUntilAsync(() => gateway.HistoryRequests.Count == 1);
        var firstGeneration = session.HistoryState.Generation;
        await session.SelectConversationAsync(secondConversation);
        firstGate.SetResult(new HistoryResult([Message(100, firstConversation)], true, true));
        await firstSelection;

        Assert.True(session.HistoryState.Generation > firstGeneration);
        Assert.Equal(secondConversation, session.HistoryState.Conversation);
        Assert.Contains(200, session.State.Messages.Keys);
        Assert.DoesNotContain(100, session.State.Messages.Keys);
        Assert.Contains(store.StoredPages, page => page.Any(message => message.Id == 100));
        await session.StopAsync();
    }

    [Fact]
    public async Task SelectConversationAsync_WhenOfflineCacheIsEmpty_DoesNotClaimOldest()
    {
        var conversation = new DirectMessage([77]);
        var gateway = new FakeGateway
        {
            RegisterHandler = (_, _) => Task.FromResult(Register()),
            HistoryHandler = (_, _) => Task.FromException<HistoryResult>(
                new GatewayException(GatewayErrorKind.Offline, GatewayErrorCode.NetworkError))
        };
        await using var session = new ClientSession(gateway, new FakeAccountStore(), new FakeCredentialVault());
        await session.LoginAsync("https://zulip.example/", "me@example.test", "password");

        await session.SelectConversationAsync(conversation);

        Assert.Empty(session.State.Messages);
        Assert.False(session.HistoryState.FoundOldest);
        Assert.False(session.HistoryState.HasOlderInCache);
        Assert.False(session.HistoryState.IsLoading);
        Assert.Equal("offline", session.HistoryState.Error);
        await session.StopAsync();
    }

    [Fact]
    public async Task SelectConversationAsync_WhenAutomaticMarkReadFails_KeepsLoadedHistoryAndUnreadState()
    {
        var conversation = new DirectMessage([20]);
        var unread = new ChatMessage(
            100,
            conversation,
            20,
            "unread",
            DateTimeOffset.UnixEpoch.AddSeconds(100));
        var gateway = new FakeGateway
        {
            RegisterHandler = (_, _) => Task.FromResult(Register()),
            HistoryHandler = (_, _) => Task.FromResult(new HistoryResult([unread], true, true)),
            MarkReadHandler = (_, _) => Task.FromException(
                new GatewayException(GatewayErrorKind.Offline, GatewayErrorCode.NetworkError))
        };
        await using var session = new ClientSession(gateway, new FakeAccountStore(), new FakeCredentialVault());
        await session.LoginAsync("https://zulip.example/", "me@example.test", "password");

        await session.SelectConversationAsync(conversation);

        Assert.Null(session.HistoryState.Error);
        Assert.False(session.HistoryState.IsLoading);
        Assert.Equal(ConnectionStatus.Connected, session.State.Connection.Status);
        Assert.False(Assert.Single(session.State.Messages.Values).IsRead);
        Assert.Single(gateway.MarkReadRequests);
        await session.StopAsync();
    }

    [Fact]
    public async Task SelectConversationAsync_WhenAutomaticMarkReadCacheWriteFails_KeepsLoadedHistoryAndReportsCacheFault()
    {
        var conversation = new DirectMessage([20]);
        var unread = new ChatMessage(
            100,
            conversation,
            20,
            "unread",
            DateTimeOffset.UnixEpoch.AddSeconds(100));
        var store = new FakeAccountStore();
        var gateway = new FakeGateway
        {
            RegisterHandler = (_, _) => Task.FromResult(Register()),
            HistoryHandler = (_, _) => Task.FromResult(new HistoryResult([unread], true, true))
        };
        await using var session = new ClientSession(gateway, store, new FakeCredentialVault());
        await session.LoginAsync("https://zulip.example/", "me@example.test", "password");
        store.ApplyFailure = new InvalidOperationException("database unavailable");

        await session.SelectConversationAsync(conversation);

        Assert.Null(session.HistoryState.Error);
        Assert.False(session.HistoryState.IsLoading);
        Assert.Equal(ConnectionStatus.Faulted, session.State.Connection.Status);
        Assert.Equal("mark_read_cache_failed", session.State.Connection.Detail);
        Assert.False(Assert.Single(session.State.Messages.Values).IsRead);
        Assert.Single(gateway.MarkReadRequests);
        await session.StopAsync();
    }

    [Fact]
    public async Task LoadOlderAsync_WhenSixPagesAreLoaded_KeepsFiftyPageSizeAndTwoHundredFiftyMessageWindow()
    {
        var conversation = new ChannelTopic(1, "general");
        var gateway = new FakeGateway
        {
            RegisterHandler = (_, _) => Task.FromResult(Register(subscriptions: [new Subscription(1, "General")])),
            HistoryHandler = (request, _) =>
            {
                var top = request.AnchorMessageId is null ? 300 : request.AnchorMessageId.Value - 1;
                var bottom = Math.Max(1, top - 49);
                return Task.FromResult(new HistoryResult(
                    Enumerable.Range((int)bottom, (int)(top - bottom + 1))
                        .Select(id => Message(id, conversation)).ToArray(),
                    bottom == 1,
                    request.AnchorMessageId is null));
            }
        };
        await using var session = new ClientSession(gateway, new FakeAccountStore(), new FakeCredentialVault());
        await session.LoginAsync("https://zulip.example/", "me@example.test", "password");
        await session.SelectConversationAsync(conversation);

        for (var page = 0; page < 5; page++) await session.LoadOlderAsync();

        Assert.Equal(250, session.State.Messages.Count);
        Assert.Equal(1, session.State.Messages.Keys.Min());
        Assert.Equal(250, session.State.Messages.Keys.Max());
        Assert.True(session.HistoryState.FoundOldest);
        Assert.All(gateway.HistoryRequests, request => Assert.Equal(50, request.Limit));
        await session.StopAsync();
    }

    [Fact]
    public async Task SendAsync_WhenNoEchoArrives_TransitionsOutboxWithoutResend()
    {
        var delays = new ControlledDelay();
        var conversation = new ChannelTopic(1, "general");
        var gateway = new FakeGateway
        {
            RegisterHandler = (_, _) => Task.FromResult(Register(subscriptions: [new Subscription(1, "General")])),
            HistoryHandler = (_, _) => Task.FromResult(new HistoryResult([], false, false))
        };
        await using var session = new ClientSession(gateway, new FakeAccountStore(), new FakeCredentialVault(), delays.DelayAsync);
        await session.LoginAsync("https://zulip.example/", "me@example.test", "password");
        await session.SelectConversationAsync(conversation);

        await session.SendAsync("hello");
        var localId = Assert.Single(session.State.Outbox).Key;
        Assert.Equal(OutboxState.Hidden, session.State.Outbox[localId].State);
        await delays.CompleteNextAsync(OutboxTimingPolicy.WaitDuration);
        await WaitUntilAsync(() => session.State.Outbox[localId].State == OutboxState.Waiting);
        await delays.CompleteNextAsync(OutboxTimingPolicy.ExpiryDuration - OutboxTimingPolicy.WaitDuration);
        await WaitUntilAsync(() => session.State.Outbox[localId].State == OutboxState.WaitExpired);

        Assert.Equal(1, gateway.SendCalls);
        await session.StopAsync();
    }

    [Fact]
    public async Task LogoutAsync_WhenSendIsInFlight_WaitsBeforeRemovingCredentialsOrLockingCache()
    {
        var conversation = new ChannelTopic(1, "general");
        var sendSource = new TaskCompletionSource<SendResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = new FakeGateway
        {
            RegisterHandler = (_, _) => Task.FromResult(Register(subscriptions: [new Subscription(1, "General")])),
            SendHandler = (_, _) => sendSource.Task
        };
        var store = new FakeAccountStore();
        var vault = new FakeCredentialVault();
        await using var session = new ClientSession(gateway, store, vault);
        await session.LoginAsync("https://zulip.example/", "me@example.test", "password");
        await session.SelectConversationAsync(conversation);

        var sending = session.SendAsync("hello");
        await WaitUntilAsync(() => gateway.SendRequests.Count == 1);
        var logout = session.LogoutAsync();
        await Task.Delay(25);

        Assert.False(logout.IsCompleted);
        Assert.NotNull(vault.Credential);
        Assert.True(store.IsUnlocked);
        Assert.NotNull(session.AccountId);
        sendSource.SetResult(new SendResult(gateway.SendRequests[0].LocalId, 500));
        await sending;
        await logout;

        Assert.Null(vault.Credential);
        Assert.False(store.IsUnlocked);
        Assert.Null(session.AccountId);
        Assert.Empty(session.State.Outbox);
        Assert.Equal(ConnectionStatus.SignedOut, session.State.Connection.Status);
    }

    [Fact]
    public async Task LogoutAsync_WhenSendDeadlineExpires_WaitsForDeadlineThenCompletesTeardown()
    {
        var deadline = new ControlledDelay();
        var conversation = new ChannelTopic(1, "general");
        var gateway = new FakeGateway
        {
            RegisterHandler = (_, _) => Task.FromResult(Register(subscriptions: [new Subscription(1, "General")])),
            SendHandler = (_, cancellationToken) => Never<SendResult>(cancellationToken)
        };
        var store = new FakeAccountStore();
        var vault = new FakeCredentialVault();
        await using var session = new ClientSession(
            gateway,
            store,
            vault,
            sendDeadlineDelay: deadline.DelayAsync);
        await session.LoginAsync("https://zulip.example/", "me@example.test", "password");
        await session.SelectConversationAsync(conversation);

        var sending = session.SendAsync("hello");
        await WaitUntilAsync(() => gateway.SendCalls == 1);
        var logout = session.LogoutAsync();
        await Task.Delay(25);
        Assert.False(logout.IsCompleted);
        Assert.NotNull(vault.Credential);
        Assert.True(store.IsUnlocked);

        await deadline.CompleteNextAsync(OutboxTimingPolicy.ExpiryDuration);
        await Assert.ThrowsAsync<GatewayException>(() => sending);
        await logout;

        Assert.Equal(1, gateway.SendCalls);
        Assert.Null(vault.Credential);
        Assert.False(store.IsUnlocked);
        Assert.Equal(ConnectionStatus.SignedOut, session.State.Connection.Status);
    }

    [Fact]
    public async Task StopAsync_WhenSendIsInFlight_WaitsUntilSendSettles()
    {
        var conversation = new ChannelTopic(1, "general");
        var sendSource = new TaskCompletionSource<SendResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = new FakeGateway
        {
            RegisterHandler = (_, _) => Task.FromResult(Register(subscriptions: [new Subscription(1, "General")])),
            SendHandler = (_, _) => sendSource.Task
        };
        await using var session = new ClientSession(gateway, new FakeAccountStore(), new FakeCredentialVault());
        await session.LoginAsync("https://zulip.example/", "me@example.test", "password");
        await session.SelectConversationAsync(conversation);

        var sending = session.SendAsync("hello");
        await WaitUntilAsync(() => gateway.SendRequests.Count == 1);
        var stopping = session.StopAsync();
        await Task.Delay(25);
        Assert.False(stopping.IsCompleted);

        sendSource.SetResult(new SendResult(gateway.SendRequests[0].LocalId, 500));
        await sending;
        await stopping;

        Assert.Equal(ConnectionStatus.Offline, session.State.Connection.Status);
        Assert.Equal(1, gateway.SendCalls);
    }

    [Fact]
    public async Task DisposeAsync_WhenSendIsInFlight_WaitsUntilSendSettles()
    {
        var conversation = new ChannelTopic(1, "general");
        var sendSource = new TaskCompletionSource<SendResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = new FakeGateway
        {
            RegisterHandler = (_, _) => Task.FromResult(Register(subscriptions: [new Subscription(1, "General")])),
            SendHandler = (_, _) => sendSource.Task
        };
        var session = new ClientSession(gateway, new FakeAccountStore(), new FakeCredentialVault());
        await session.LoginAsync("https://zulip.example/", "me@example.test", "password");
        await session.SelectConversationAsync(conversation);

        var sending = session.SendAsync("hello");
        await WaitUntilAsync(() => gateway.SendRequests.Count == 1);
        var disposing = session.DisposeAsync().AsTask();
        await Task.Delay(25);
        Assert.False(disposing.IsCompleted);

        sendSource.SetResult(new SendResult(gateway.SendRequests[0].LocalId, 500));
        await sending;
        await disposing;

        Assert.Equal(1, gateway.SendCalls);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.SendAsync("again"));
    }

    [Fact]
    public async Task SendAsync_WhenStopOwnsCommandGate_DoesNotStartPostAfterTeardown()
    {
        var conversation = new ChannelTopic(1, "general");
        var stoppingEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var eventSource = new TaskCompletionSource<EventBatch>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = new FakeGateway
        {
            RegisterHandler = (_, _) => Task.FromResult(Register(subscriptions: [new Subscription(1, "General")])),
            GetEventsHandler = (_, token) =>
            {
                token.Register(() => stoppingEntered.TrySetResult(true));
                return eventSource.Task;
            }
        };
        await using var session = new ClientSession(gateway, new FakeAccountStore(), new FakeCredentialVault());
        await session.LoginAsync("https://zulip.example/", "me@example.test", "password");
        await session.SelectConversationAsync(conversation);
        await WaitUntilAsync(() => gateway.GetEventsCalls == 1);

        var stopping = session.StopAsync();
        await stoppingEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var sending = session.SendAsync("hello");
        await Task.Delay(25);
        Assert.False(stopping.IsCompleted);
        Assert.False(sending.IsCompleted);
        Assert.Equal(0, gateway.SendCalls);

        eventSource.TrySetCanceled();
        await stopping;

        await Assert.ThrowsAsync<InvalidOperationException>(() => sending);

        Assert.Equal(0, gateway.SendCalls);
        Assert.Empty(session.State.Outbox);
    }

    [Fact]
    public async Task SendAsync_WhenHistoryReconciliationExceedsDeadline_ReturnsAndKeepsOutboxForRealtime()
    {
        var deadline = new ControlledDelay();
        var conversation = new ChannelTopic(1, "general");
        var gateway = new FakeGateway
        {
            RegisterHandler = (_, _) => Task.FromResult(Register(subscriptions: [new Subscription(1, "General")])),
            HistoryHandler = (request, cancellationToken) => request.AnchorMessageId is null
                ? Task.FromResult(new HistoryResult([], false, true))
                : Never<HistoryResult>(cancellationToken)
        };
        var session = new ClientSession(
            gateway,
            new FakeAccountStore(),
            new FakeCredentialVault(),
            sendDeadlineDelay: deadline.DelayAsync);
        using var cancellation = new CancellationTokenSource();
        Task? sending = null;
        try
        {
            await session.LoginAsync("https://zulip.example/", "me@example.test", "password");
            await session.SelectConversationAsync(conversation);

            sending = session.SendAsync("hello", cancellation.Token);
            await WaitUntilAsync(() => gateway.HistoryRequests.Count == 2);
            await deadline.CompleteNextAsync(OutboxTimingPolicy.ExpiryDuration);
            await sending.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(1, gateway.SendCalls);
            Assert.Single(session.State.Outbox);
            Assert.Equal(ConnectionStatus.Connected, session.State.Connection.Status);
        }
        finally
        {
            if (sending is { IsCompleted: false }) cancellation.Cancel();
            if (sending is not null)
            {
                try { await sending; } catch (OperationCanceledException) { }
            }
            await session.DisposeAsync();
        }
    }

    [Fact]
    public async Task SendAsync_WhenPostExceedsDeadline_EndsCommandAsWaitExpiredWithoutResend()
    {
        var deadline = new ControlledDelay();
        var conversation = new ChannelTopic(1, "general");
        var gateway = new FakeGateway
        {
            RegisterHandler = (_, _) => Task.FromResult(Register(subscriptions: [new Subscription(1, "General")])),
            SendHandler = (_, cancellationToken) => Never<SendResult>(cancellationToken)
        };
        await using var session = new ClientSession(
            gateway,
            new FakeAccountStore(),
            new FakeCredentialVault(),
            sendDeadlineDelay: deadline.DelayAsync);
        await session.LoginAsync("https://zulip.example/", "me@example.test", "password");
        await session.SelectConversationAsync(conversation);

        var sending = session.SendAsync("hello");
        await WaitUntilAsync(() => gateway.SendCalls == 1);
        await deadline.CompleteNextAsync(OutboxTimingPolicy.ExpiryDuration);

        var exception = await Assert.ThrowsAsync<GatewayException>(() => sending);
        Assert.Equal(GatewayErrorCode.RequestTimedOut, exception.Code);
        Assert.Null(exception.InnerException);
        Assert.Equal(typeof(TimeoutException).FullName, exception.CauseTypeName);
        Assert.Equal(OutboxState.WaitExpired, Assert.Single(session.State.Outbox).Value.State);
        Assert.Equal(ConnectionStatus.Offline, session.State.Connection.Status);
        await Task.Delay(25);
        Assert.Equal(1, gateway.SendCalls);
        await session.StopAsync();
    }

    [Fact]
    public async Task SendAsync_WhenUserCancels_DoesNotReportDeadlineExpiry()
    {
        var deadline = new ControlledDelay();
        var conversation = new ChannelTopic(1, "general");
        var gateway = new FakeGateway
        {
            RegisterHandler = (_, _) => Task.FromResult(Register(subscriptions: [new Subscription(1, "General")])),
            SendHandler = (_, cancellationToken) => Never<SendResult>(cancellationToken)
        };
        await using var session = new ClientSession(
            gateway,
            new FakeAccountStore(),
            new FakeCredentialVault(),
            sendDeadlineDelay: deadline.DelayAsync);
        await session.LoginAsync("https://zulip.example/", "me@example.test", "password");
        await session.SelectConversationAsync(conversation);
        using var cancellation = new CancellationTokenSource();

        var sending = session.SendAsync("hello", cancellation.Token);
        await WaitUntilAsync(() => gateway.SendCalls == 1);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
        Assert.Equal(OutboxState.Failed, Assert.Single(session.State.Outbox).Value.State);
        Assert.Equal(1, gateway.SendCalls);
        await session.StopAsync();
    }

    [Fact]
    public async Task SendAsync_WhenRealtimeEchoBeatsHttpResponse_ReconcilesByLocalIdAndNeverResends()
    {
        var conversation = new ChannelTopic(1, "general");
        var sendSource = new TaskCompletionSource<SendResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var eventSource = new TaskCompletionSource<EventBatch>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = new FakeGateway
        {
            RegisterHandler = (_, _) => Task.FromResult(Register(subscriptions: [new Subscription(1, "General")])),
            SendHandler = (_, _) => sendSource.Task,
            HistoryHandler = (request, _) => Task.FromResult(request.AnchorMessageId is { } anchor
                ? new HistoryResult([Message(anchor, conversation)], true, true)
                : new HistoryResult([], false, true))
        };
        gateway.GetEventsHandler = (_, cancellationToken) => gateway.GetEventsCalls == 1
            ? eventSource.Task
            : Never<EventBatch>(cancellationToken);
        await using var session = new ClientSession(gateway, new FakeAccountStore(), new FakeCredentialVault());
        await session.LoginAsync("https://zulip.example/", "me@example.test", "password");
        await session.SelectConversationAsync(conversation);

        var sending = session.SendAsync("hello");
        await WaitUntilAsync(() => gateway.SendRequests.Count == 1);
        var request = gateway.SendRequests[0];
        eventSource.SetResult(new EventBatch(
            [new MessageUpsertEvent(Message(500, conversation), 2, DomainEventSource.Realtime, request.LocalId)], 2));
        await WaitUntilAsync(() => session.State.Outbox.Count == 0);
        sendSource.SetResult(new SendResult(request.LocalId, 500));
        await sending;

        Assert.Equal(1, gateway.SendCalls);
        Assert.Empty(session.State.Outbox);
        Assert.Contains(500, session.State.Messages.Keys);
        await session.StopAsync();
    }

    [Fact]
    public async Task SendAsync_WhenPostFailsExplicitly_MarksFailedAndDoesNotRetry()
    {
        var conversation = new ChannelTopic(1, "general");
        var gateway = new FakeGateway
        {
            RegisterHandler = (_, _) => Task.FromResult(Register(subscriptions: [new Subscription(1, "General")])),
            SendHandler = (_, _) => Task.FromException<SendResult>(
                new GatewayException(GatewayErrorKind.RateLimited, GatewayErrorCode.RateLimited, 429, TimeSpan.FromSeconds(4)))
        };
        await using var session = new ClientSession(gateway, new FakeAccountStore(), new FakeCredentialVault());
        await session.LoginAsync("https://zulip.example/", "me@example.test", "password");
        await session.SelectConversationAsync(conversation);

        await Assert.ThrowsAsync<GatewayException>(() => session.SendAsync("hello"));

        var entry = Assert.Single(session.State.Outbox).Value;
        Assert.Equal(OutboxState.Failed, entry.State);
        Assert.Equal(OutboxFailureKind.RateLimited, entry.Failure);
        Assert.Equal(ConnectionStatus.RateLimited, session.State.Connection.Status);
        Assert.Equal(1, gateway.SendCalls);
        await session.StopAsync();
    }

    [Fact]
    public async Task SendAsync_WhenPostHasExplicitNetworkFailure_MarksFailedAndSessionOffline()
    {
        var conversation = new ChannelTopic(1, "general");
        var gateway = new FakeGateway
        {
            RegisterHandler = (_, _) => Task.FromResult(Register(subscriptions: [new Subscription(1, "General")])),
            SendHandler = (_, _) => Task.FromException<SendResult>(
                new GatewayException(GatewayErrorKind.Offline, GatewayErrorCode.NetworkError))
        };
        await using var session = new ClientSession(gateway, new FakeAccountStore(), new FakeCredentialVault());
        await session.LoginAsync("https://zulip.example/", "me@example.test", "password");
        await session.SelectConversationAsync(conversation);

        await Assert.ThrowsAsync<GatewayException>(() => session.SendAsync("hello"));

        var entry = Assert.Single(session.State.Outbox).Value;
        Assert.Equal(OutboxState.Failed, entry.State);
        Assert.Equal(OutboxFailureKind.NetworkResultUnknown, entry.Failure);
        Assert.Equal(ConnectionStatus.Offline, session.State.Connection.Status);
        Assert.Equal(1, gateway.SendCalls);
        await session.StopAsync();
    }

    [Fact]
    public async Task MarkDisplayedReadAsync_WhenServiceHasNotSucceeded_DoesNotMutateThenMarksOnlyNewestFifty()
    {
        var conversation = new ChannelTopic(1, "general");
        var markSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var messages = Enumerable.Range(1, 60)
            .Select(id => (DomainEvent)new MessageUpsertEvent(
                Message(id, conversation) with { SenderId = 8 }, Source: DomainEventSource.Register))
            .ToArray();
        var gateway = new FakeGateway
        {
            RegisterHandler = (_, _) => Task.FromResult(Register(
                subscriptions: [new Subscription(1, "General")],
                events: messages,
                unread: new UnreadState(new Dictionary<string, int> { [conversation.CanonicalKey] = 60 }, 60))),
            MarkReadHandler = async (_, cancellationToken) =>
            {
                await markSource.Task.WaitAsync(cancellationToken);
            }
        };
        await using var session = new ClientSession(gateway, new FakeAccountStore(), new FakeCredentialVault());
        await session.LoginAsync("https://zulip.example/", "me@example.test", "password");
        await session.SelectConversationAsync(conversation);

        var marking = session.MarkDisplayedReadAsync();
        await WaitUntilAsync(() => gateway.MarkReadRequests.Count == 1);
        Assert.All(session.State.Messages.Values, message => Assert.False(message.IsRead));
        markSource.SetResult(true);
        await marking;

        Assert.Equal(50, session.State.Messages.Values.Count(message => message.IsRead));
        Assert.Equal(50, gateway.MarkReadRequests[0].Limit);
        Assert.Equal(60, gateway.MarkReadRequests[0].AnchorMessageId);
        await session.StopAsync();
    }

    [Fact]
    public async Task MarkDisplayedReadAsync_WhenOnlyOlderPageIsUnread_DoesNotMarkOutsideNewestWindow()
    {
        var conversation = new ChannelTopic(1, "general");
        var messages = Enumerable.Range(1, 100)
            .Select(id => (DomainEvent)new MessageUpsertEvent(
                Message(id, conversation) with { IsRead = id > 50 },
                Source: DomainEventSource.Register))
            .ToArray();
        var gateway = new FakeGateway
        {
            RegisterHandler = (_, _) => Task.FromResult(Register(
                subscriptions: [new Subscription(1, "General")],
                events: messages))
        };
        await using var session = new ClientSession(gateway, new FakeAccountStore(), new FakeCredentialVault());
        await session.LoginAsync("https://zulip.example/", "me@example.test", "password");
        await session.SelectConversationAsync(conversation);

        await session.MarkDisplayedReadAsync();

        Assert.Empty(gateway.MarkReadRequests);
        Assert.DoesNotContain(session.State.Messages.Values, message => !message.IsRead);
        await session.StopAsync();
    }

    [Fact]
    public async Task MarkDisplayedReadAsync_WhenNewestWindowHasMixedFlags_UpdatesOnlyUnreadInThatWindow()
    {
        var conversation = new ChannelTopic(1, "general");
        var messages = Enumerable.Range(1, 100)
            .Select(id => (DomainEvent)new MessageUpsertEvent(
                Message(id, conversation) with { SenderId = 8, IsRead = id > 50 && id % 2 == 0 },
                Source: DomainEventSource.Register))
            .ToArray();
        var gateway = new FakeGateway
        {
            RegisterHandler = (_, _) => Task.FromResult(Register(
                subscriptions: [new Subscription(1, "General")],
                events: messages))
        };
        await using var session = new ClientSession(gateway, new FakeAccountStore(), new FakeCredentialVault());
        await session.LoginAsync("https://zulip.example/", "me@example.test", "password");
        await session.SelectConversationAsync(conversation);

        await session.MarkDisplayedReadAsync();

        var request = Assert.Single(gateway.MarkReadRequests);
        Assert.Equal(99, request.AnchorMessageId);
        Assert.Equal(25, request.Limit);
        Assert.All(session.State.Messages.Values.Where(message => message.Id > 50), message => Assert.True(message.IsRead));
        Assert.All(session.State.Messages.Values.Where(message => message.Id <= 50), message => Assert.False(message.IsRead));
        await session.StopAsync();
    }

    [Fact]
    public async Task StopAsync_WhenCalledRepeatedly_CancelsLongPollAndSecondLoginIsRejected()
    {
        var gateway = new FakeGateway();
        await using var session = new ClientSession(gateway, new FakeAccountStore(), new FakeCredentialVault());
        await session.LoginAsync("https://zulip.example/", "me@example.test", "password");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.LoginAsync("https://zulip.example/", "me@example.test", "password"));
        await session.StopAsync();
        await session.StopAsync();

        Assert.Equal(1, gateway.AuthenticateCalls);
        Assert.True(gateway.CancelledLongPolls > 0);
        Assert.Equal(ConnectionStatus.Offline, session.State.Connection.Status);
    }

    [Fact]
    public async Task LoadTopicsAsync_WhenConnected_StoresAndReturnsTopics()
    {
        var gateway = new FakeGateway
        {
            TopicsHandler = (_, _) => Task.FromResult<TopicsResult>(new TopicsResult([new TopicSummary(8, "release", 80)]))
        };
        await using var session = new ClientSession(gateway, new FakeAccountStore(), new FakeCredentialVault());
        await session.LoginAsync("https://zulip.example/", "me@example.test", "password");

        var topics = await session.LoadTopicsAsync(8);

        Assert.Equal("release", Assert.Single(topics).Topic);
        Assert.Contains(new ChannelTopic(8, "release").CanonicalKey, session.State.Topics.Keys);
        await session.StopAsync();
    }

    [Fact]
    public async Task LoadTopicsAsync_WhenOffline_ReturnsCachedTopicsWithoutNetwork()
    {
        var topic = new TopicSummary(8, "cached", 80);
        var gateway = new FakeGateway
        {
            RegisterHandler = (_, _) => Task.FromResult(Register(
                events: [new TopicUpsertEvent(topic, Source: DomainEventSource.Register)]))
        };
        await using var session = new ClientSession(gateway, new FakeAccountStore(), new FakeCredentialVault());
        await session.LoginAsync("https://zulip.example/", "me@example.test", "password");
        await session.StopAsync();

        var topics = await session.LoadTopicsAsync(8);

        Assert.Equal("cached", Assert.Single(topics).Topic);
        Assert.Equal(0, gateway.TopicsCalls);
    }

    [Fact]
    public async Task ClearLocalCacheAsync_WhenActive_ClearsMemoryAndReinitializesAccountStore()
    {
        var gateway = new FakeGateway();
        var store = new FakeAccountStore();
        await using var session = new ClientSession(gateway, store, new FakeCredentialVault());
        await session.LoginAsync("https://zulip.example/", "me@example.test", "password");

        await session.ClearLocalCacheAsync();

        Assert.Equal(1, store.ClearCalls);
        Assert.True(store.InitializeCalls >= 2);
        Assert.Empty(session.State.Messages);
        Assert.Equal(2, gateway.RegisterCalls);
        Assert.Equal("queue-1", Assert.Single(gateway.DeleteQueueRequests).QueueId);
        await session.StopAsync();
    }

    [Fact]
    public async Task SetMessageStarredAsync_WhenGatewaySucceeds_ConvergesLocalProjection()
    {
        var message = Message(50, new DirectMessage([]));
        var gateway = new FakeGateway
        {
            RegisterHandler = (_, _) => Task.FromResult(Register(events:
                [new MessageUpsertEvent(message, Source: DomainEventSource.Register)]))
        };
        await using var session = new ClientSession(gateway, new FakeAccountStore(), new FakeCredentialVault());
        await session.LoginAsync("https://zulip.example/", "me@example.test", "password");
        await session.SelectConversationAsync(message.Conversation);

        await session.SetMessageStarredAsync(50, true);

        Assert.True(session.State.Messages[50].IsStarred);
        Assert.DoesNotContain(50, session.State.MessageMutations.Keys);
        await session.StopAsync();
    }

    [Fact]
    public async Task SearchMessagesAsync_WhenNewSearchStarts_CancelsTheOlderRequestAndDoesNotPersistResults()
    {
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = new FakeGateway
        {
            SearchHandler = async (_, token) =>
            {
                started.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new MessageQueryPage([], false, false, true);
            }
        };
        await using var session = new ClientSession(gateway, new FakeAccountStore(), new FakeCredentialVault());
        await session.LoginAsync("https://zulip.example/", "me@example.test", "password");

        var first = session.SearchMessagesAsync("first", null, 50);
        await started.Task;
        using var secondCancellation = new CancellationTokenSource();
        var second = session.SearchMessagesAsync("second", null, 50, secondCancellation.Token);
        secondCancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        Assert.Empty(session.State.Messages);
        await session.StopAsync();
    }

    [Fact]
    public async Task MessageQueries_WhenSavedStarts_DoesNotCancelAnIndependentSearch()
    {
        var releaseSearch = new TaskCompletionSource<MessageQueryPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = new FakeGateway
        {
            SearchHandler = (_, _) => releaseSearch.Task,
            SavedHandler = (_, _) => Task.FromResult(new MessageQueryPage([], false, true, true))
        };
        await using var session = new ClientSession(gateway, new FakeAccountStore(), new FakeCredentialVault());
        await session.LoginAsync("https://zulip.example/", "me@example.test", "password");

        var search = session.SearchMessagesAsync("query", null, 50);
        var saved = await session.LoadSavedMessagesAsync(null, 50);
        releaseSearch.SetResult(new MessageQueryPage([], false, true, true));

        Assert.Empty(saved.Messages);
        Assert.Empty((await search).Messages);
        await session.StopAsync();
    }

    [Fact]
    public async Task OpenMessageAsync_WhenAnchorIsFound_LoadsAStableAroundContextWithoutFetchingNewest()
    {
        var conversation = new DirectMessage([20]);
        var message = Message(75, conversation);
        var gateway = new FakeGateway
        {
            AroundHandler = (request, _) => Task.FromResult(new HistoryResult([message], false, false, true))
        };
        await using var session = new ClientSession(gateway, new FakeAccountStore(), new FakeCredentialVault());
        await session.LoginAsync("https://zulip.example/", "me@example.test", "password");

        await session.OpenMessageAsync(conversation, message.Id);

        var around = Assert.Single(gateway.AroundRequests);
        Assert.Equal(message.Id, around.MessageId);
        Assert.Equal(25, around.BeforeCount);
        Assert.Equal(24, around.AfterCount);
        Assert.Empty(gateway.HistoryRequests);
        Assert.Equal(message.Id, Assert.Single(session.State.Messages).Key);
        await session.StopAsync();
    }

    [Fact]
    public async Task EditMessageAsync_WhenGatewayResultIsUnknown_BlocksFurtherMutationAndDoesNotRetry()
    {
        var message = Message(51, new DirectMessage([]));
        var calls = 0;
        var gateway = new FakeGateway
        {
            RegisterHandler = (_, _) => Task.FromResult(Register(events:
                [new MessageUpsertEvent(message, Source: DomainEventSource.Register)])),
            EditMessageHandler = (_, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromException(new GatewayException(GatewayErrorKind.Offline, GatewayErrorCode.NetworkError));
            }
        };
        await using var session = new ClientSession(gateway, new FakeAccountStore(), new FakeCredentialVault());
        await session.LoginAsync("https://zulip.example/", "me@example.test", "password");
        await session.SelectConversationAsync(message.Conversation);

        await Assert.ThrowsAsync<GatewayException>(() => session.EditMessageAsync(51, "edited"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.SetMessageStarredAsync(51, true));

        Assert.Equal(1, Volatile.Read(ref calls));
        Assert.Equal("message-51", session.State.Messages[51].Content);
        Assert.Equal(MessageMutationStatus.Uncertain, session.State.MessageMutations[51].Status);
        await session.StopAsync();
    }

    [Fact]
    public async Task EditMessageAsync_WhenMessageIsNotOwned_DoesNotCallGateway()
    {
        var message = new ChatMessage(52, new DirectMessage([20]), 20, "other", DateTimeOffset.UnixEpoch);
        var calls = 0;
        var gateway = new FakeGateway
        {
            RegisterHandler = (_, _) => Task.FromResult(Register(events:
                [new MessageUpsertEvent(message, Source: DomainEventSource.Register)])),
            EditMessageHandler = (_, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.CompletedTask;
            }
        };
        await using var session = new ClientSession(gateway, new FakeAccountStore(), new FakeCredentialVault());
        await session.LoginAsync("https://zulip.example/", "me@example.test", "password");

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.EditMessageAsync(52, "edited"));

        Assert.Equal(0, calls);
        await session.StopAsync();
    }

    [Fact]
    public async Task UploadAttachmentAsync_WhenRegisterDefinesLimit_ValidatesBeforeOneGatewayPost()
    {
        var gateway = new FakeGateway
        {
            RegisterHandler = (_, _) => Task.FromResult(Register(maxFileUploadSizeMiB: 1))
        };
        await using var session = new ClientSession(gateway, new FakeAccountStore(), new FakeCredentialVault());
        await session.LoginAsync("https://zulip.example/", "me@example.test", "password");
        await using var acceptedStream = new MemoryStream([1, 2, 3]);

        var uploaded = await session.UploadAttachmentAsync(
            new AttachmentUpload("note.txt", "text/plain", 3, acceptedStream));

        Assert.Equal(1024 * 1024, session.MaxFileUploadBytes);
        Assert.Equal(1, gateway.UploadCalls);
        Assert.Equal("note.txt", uploaded.FileName);
        await using var tooLarge = new MemoryStream(new byte[1]);
        await Assert.ThrowsAsync<ArgumentException>(() => session.UploadAttachmentAsync(
            new AttachmentUpload("large.bin", null, 1024 * 1024 + 1L, tooLarge)));
        Assert.Equal(1, gateway.UploadCalls);
        await session.StopAsync();
    }

    [Fact]
    public async Task SubscribeToChannelAsync_WhenRefetchedCatalogMatches_AddsOnlyConfirmedChannel()
    {
        var gateway = new FakeGateway
        {
            AvailableChannelsHandler = (_, _) => Task.FromResult<IReadOnlyList<ChannelSummary>>([new ChannelSummary(7, "engineering", "raw", false, 2)])
        };
        await using var session = new ClientSession(gateway, new FakeAccountStore(), new FakeCredentialVault());
        await session.LoginAsync("https://zulip.example/", "me@example.test", "password");

        await session.GetAvailableChannelsAsync();
        await session.SubscribeToChannelAsync(7);

        Assert.Equal(2, gateway.AvailableChannelsCalls);
        Assert.Single(gateway.SubscribeRequests);
        Assert.Equal("engineering", gateway.SubscribeRequests[0].Channel.Name);
        Assert.Contains(7, session.State.Subscriptions.Keys);
        await session.StopAsync();
    }

    [Fact]
    public async Task SubscribeToChannelAsync_WhenRefetchedCatalogChanged_DoesNotPost()
    {
        var calls = 0;
        var gateway = new FakeGateway
        {
            AvailableChannelsHandler = (_, _) => Task.FromResult<IReadOnlyList<ChannelSummary>>(
                ++calls == 1 ? [new ChannelSummary(7, "engineering", null, false, null)] : [new ChannelSummary(7, "renamed", null, false, null)])
        };
        await using var session = new ClientSession(gateway, new FakeAccountStore(), new FakeCredentialVault());
        await session.LoginAsync("https://zulip.example/", "me@example.test", "password");

        await session.GetAvailableChannelsAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.SubscribeToChannelAsync(7));

        Assert.Empty(gateway.SubscribeRequests);
        await session.StopAsync();
    }

    [Fact]
    public async Task SubscribeToChannelAsync_WhenPostIsUnauthorized_ClearsCredentialsAndDoesNotAddSubscription()
    {
        var gateway = new FakeGateway
        {
            AvailableChannelsHandler = (_, _) => Task.FromResult<IReadOnlyList<ChannelSummary>>([new ChannelSummary(7, "engineering", null, false, null)]),
            SubscribeChannelHandler = (_, _) => Task.FromException<SubscribeChannelResult>(new GatewayException(GatewayErrorKind.ReauthRequired, GatewayErrorCode.Unauthorized))
        };
        var vault = new FakeCredentialVault();
        await using var session = new ClientSession(gateway, new FakeAccountStore(), vault);
        await session.LoginAsync("https://zulip.example/", "me@example.test", "password");

        await session.GetAvailableChannelsAsync();
        await Assert.ThrowsAsync<GatewayException>(() => session.SubscribeToChannelAsync(7));

        Assert.Equal(ConnectionStatus.ReauthRequired, session.State.Connection.Status);
        Assert.DoesNotContain(7, session.State.Subscriptions.Keys);
        Assert.True(vault.RemoveCalls > 0);
    }

    [Fact]
    public async Task SetSubscriptionPreferenceAsync_WhenConfirmed_UpdatesReducerState()
    {
        var gateway = new FakeGateway { RegisterHandler = (_, _) => Task.FromResult(Register(subscriptions: [new Subscription(7, "engineering")])) };
        await using var session = new ClientSession(gateway, new FakeAccountStore(), new FakeCredentialVault());
        await session.LoginAsync("https://zulip.example/", "me@example.test", "password");

        await session.SetSubscriptionPreferenceAsync(7, SubscriptionPreference.Pinned, true);

        Assert.Single(gateway.PreferenceRequests);
        Assert.True(session.State.Subscriptions[7].IsPinned);
        await session.StopAsync();
    }

    [Fact]
    public async Task UnsubscribeChannelAsync_WhenServerConfirms_RemovesChannelAndSelection()
    {
        var channel = new Subscription(7, "Engineering");
        var store = new FakeAccountStore();
        var requestedNames = new List<string>();
        var gateway = new FakeGateway
        {
            RegisterHandler = (_, _) => Task.FromResult(Register(subscriptions: [channel])),
            UnsubscribeChannelHandler = (request, _) =>
            {
                requestedNames.Add(request.ChannelName);
                return Task.FromResult(new UnsubscribeChannelResult([request.ChannelName], []));
            }
        };
        await using var session = new ClientSession(gateway, store, new FakeCredentialVault());
        await session.LoginAsync("https://zulip.example/", "me@example.test", "password");
        await session.SelectConversationAsync(new ChannelTopic(channel.ChannelId, "topic"));

        await session.UnsubscribeChannelAsync(channel.ChannelId);

        Assert.Equal([channel.Name], requestedNames);
        Assert.DoesNotContain(channel.ChannelId, session.State.Subscriptions.Keys);
        Assert.Null(session.SelectedConversation);
        Assert.Contains(channel.ChannelId, store.PurgedChannels);
        await session.StopAsync();
    }

    [Fact]
    public async Task UnsubscribeChannelAsync_WhenAlreadyRemoved_ConvergesLocalState()
    {
        var channel = new Subscription(8, "Already gone");
        var gateway = new FakeGateway
        {
            RegisterHandler = (_, _) => Task.FromResult(Register(subscriptions: [channel])),
            UnsubscribeChannelHandler = (request, _) =>
                Task.FromResult(new UnsubscribeChannelResult([], [request.ChannelName]))
        };
        await using var session = new ClientSession(gateway, new FakeAccountStore(), new FakeCredentialVault());
        await session.LoginAsync("https://zulip.example/", "me@example.test", "password");

        await session.UnsubscribeChannelAsync(channel.ChannelId);

        Assert.DoesNotContain(channel.ChannelId, session.State.Subscriptions.Keys);
        await session.StopAsync();
    }

    [Fact]
    public async Task UnsubscribeChannelAsync_WhenCachePurgeFails_KeepsConfirmedRemovalInMemory()
    {
        var channel = new Subscription(11, "Confirmed remotely");
        var store = new FakeAccountStore
        {
            PurgeFailure = new InvalidOperationException("database unavailable")
        };
        var gateway = new FakeGateway
        {
            RegisterHandler = (_, _) => Task.FromResult(Register(subscriptions: [channel])),
            UnsubscribeChannelHandler = (request, _) =>
                Task.FromResult(new UnsubscribeChannelResult([request.ChannelName], []))
        };
        await using var session = new ClientSession(gateway, store, new FakeCredentialVault());
        await session.LoginAsync("https://zulip.example/", "me@example.test", "password");
        await session.SelectConversationAsync(new ChannelTopic(channel.ChannelId, "topic"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.UnsubscribeChannelAsync(channel.ChannelId));

        Assert.DoesNotContain(channel.ChannelId, session.State.Subscriptions.Keys);
        Assert.Null(session.SelectedConversation);
        Assert.Contains(channel.ChannelId, store.PurgedChannels);
        Assert.Equal(ConnectionStatus.Faulted, session.State.Connection.Status);
        Assert.Equal("channel_unsubscribe_cache_cleanup_failed", session.State.Connection.Detail);
        await session.StopAsync();
    }

    [Fact]
    public async Task UnsubscribeChannelAsync_WhenResultIsUnknown_DoesNotRemoveOrRetry()
    {
        var channel = new Subscription(9, "Keep me");
        var calls = 0;
        var gateway = new FakeGateway
        {
            RegisterHandler = (_, _) => Task.FromResult(Register(subscriptions: [channel])),
            UnsubscribeChannelHandler = (_, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromException<UnsubscribeChannelResult>(
                    new GatewayException(GatewayErrorKind.Offline, GatewayErrorCode.NetworkError));
            }
        };
        await using var session = new ClientSession(gateway, new FakeAccountStore(), new FakeCredentialVault());
        await session.LoginAsync("https://zulip.example/", "me@example.test", "password");

        await Assert.ThrowsAsync<GatewayException>(() => session.UnsubscribeChannelAsync(channel.ChannelId));

        Assert.Equal(1, Volatile.Read(ref calls));
        Assert.Contains(channel.ChannelId, session.State.Subscriptions.Keys);
        Assert.Equal(ConnectionStatus.Offline, session.State.Connection.Status);
        await session.StopAsync();
    }

    [Fact]
    public async Task EventLoop_WhenSubscriptionIsRemoved_ClearsSelectedChannel()
    {
        var channel = new Subscription(10, "External removal");
        var removal = new TaskCompletionSource<EventBatch>(TaskCreationOptions.RunContinuationsAsynchronously);
        var eventCalls = 0;
        var gateway = new FakeGateway
        {
            RegisterHandler = (_, _) => Task.FromResult(Register(subscriptions: [channel])),
            GetEventsHandler = (_, cancellationToken) => Interlocked.Increment(ref eventCalls) == 1
                ? removal.Task
                : Never<EventBatch>(cancellationToken)
        };
        await using var session = new ClientSession(gateway, new FakeAccountStore(), new FakeCredentialVault());
        await session.LoginAsync("https://zulip.example/", "me@example.test", "password");
        await session.SelectConversationAsync(new ChannelTopic(channel.ChannelId, "topic"));

        removal.SetResult(new EventBatch([new SubscriptionRemovedEvent(channel.ChannelId, 2)], 2));
        await WaitUntilAsync(() => session.SelectedConversation is null);

        Assert.DoesNotContain(channel.ChannelId, session.State.Subscriptions.Keys);
        await session.StopAsync();
    }

    [Fact]
    public async Task EventLoop_WhenMessageOutsideWindowMoves_RefreshesAffectedTopicsFromCache()
    {
        var source = new ChannelTopic(1, "old");
        var destination = new ChannelTopic(2, "new");
        var message = new ChatMessage(100, source, 20, "moved", DateTimeOffset.UnixEpoch.AddSeconds(100), isRead: true);
        var move = new TaskCompletionSource<EventBatch>(TaskCreationOptions.RunContinuationsAsynchronously);
        var eventCalls = 0;
        var gateway = new FakeGateway
        {
            RegisterHandler = (_, _) => Task.FromResult(Register(
                subscriptions: [new Subscription(1, "Source"), new Subscription(2, "Destination")],
                events: [new MessageUpsertEvent(message, Source: DomainEventSource.Register)])),
            GetEventsHandler = (_, cancellationToken) => Interlocked.Increment(ref eventCalls) == 1
                ? move.Task
                : Never<EventBatch>(cancellationToken),
            HistoryHandler = (_, _) => Task.FromResult(new HistoryResult([], true, true))
        };
        var store = new FakeAccountStore
        {
            QueryTopicsHandler = (_, topics, _) => Task.FromResult<IReadOnlyList<TopicSummary>>(
                topics.Any(topic => topic == destination)
                    ? [new TopicSummary(destination.ChannelId, destination.Topic, message.Id)]
                    : [])
        };
        await using var session = new ClientSession(gateway, store, new FakeCredentialVault());
        await session.LoginAsync("https://zulip.example/", "me@example.test", "password");
        Assert.Contains(source.CanonicalKey, session.State.ConversationSummaries.Keys);
        await session.SelectConversationAsync(new DirectMessage([20]));
        Assert.DoesNotContain(message.Id, session.State.Messages.Keys);

        move.SetResult(new EventBatch([new MessageMovedEvent([message.Id], destination, 2)], 2));
        await WaitUntilAsync(() => store.QueryTopicCalls > 0);

        Assert.DoesNotContain(source.CanonicalKey, session.State.Topics.Keys);
        Assert.Equal(message.Id, session.State.Topics[destination.CanonicalKey].MaxMessageId);
        await session.StopAsync();
    }

    private static CredentialEnvelope Credential() =>
        new(RealmEndpoint.Parse("https://zulip.example/"), "me@example.test", 10, "api-key");

    private static StoredAccount Stored(CredentialEnvelope credential) =>
        new(AccountId.Create(credential.Realm, credential.UserId), credential.Realm, credential.Email, credential.UserId);

    private static ChatMessage Message(long id, ConversationKey conversation) =>
        new(id, conversation, 10, $"message-{id}", DateTimeOffset.UnixEpoch.AddSeconds(id));

    private static RegisterResult Register(
        string queue = "queue-1",
        IReadOnlyList<Subscription>? subscriptions = null,
        IReadOnlyList<DomainEvent>? events = null,
        IReadOnlyList<ConversationKey>? recent = null,
        UnreadState? unread = null,
        int? maxFileUploadSizeMiB = null) =>
        new(queue, 1, TimeSpan.FromSeconds(25), 1_000, 100, subscriptions ?? [],
            [new UserProfile(10, "Me", "me@example.test")], recent ?? [], unread ?? new UnreadState(), events ?? [], maxFileUploadSizeMiB);

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }

    private static Task<T> Never<T>(CancellationToken cancellationToken)
    {
        var source = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        cancellationToken.Register(() => source.TrySetCanceled(cancellationToken));
        return source.Task;
    }

    private sealed class ControlledDelay
    {
        private readonly ConcurrentQueue<(TimeSpan Delay, TaskCompletionSource<bool> Source)> _requests = new();

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            var source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(() => source.TrySetCanceled(cancellationToken));
            _requests.Enqueue((delay, source));
            return source.Task;
        }

        public async Task CompleteNextAsync(TimeSpan expected)
        {
            await WaitUntilAsync(() => !_requests.IsEmpty);
            Assert.True(_requests.TryDequeue(out var request));
            Assert.Equal(expected, request.Delay);
            request.Source.TrySetResult(true);
        }
    }

    private sealed class FakeCredentialVault(List<string>? log = null) : ICredentialVault
    {
        public CredentialEnvelope? Credential { get; set; }
        public Exception? GetFailure { get; set; }
        public Exception? SetFailure { get; set; }
        public Exception? RemoveFailure { get; set; }
        public int RemoveCalls { get; private set; }

        public Task<CredentialEnvelope?> GetAsync(CancellationToken cancellationToken = default)
        {
            log?.Add("vault:get");
            if (GetFailure is not null) return Task.FromException<CredentialEnvelope?>(GetFailure);
            return Task.FromResult(Credential);
        }

        public Task SetAsync(CredentialEnvelope credentials, CancellationToken cancellationToken = default)
        {
            log?.Add("vault:set");
            if (SetFailure is not null) return Task.FromException(SetFailure);
            Credential = credentials;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(CancellationToken cancellationToken = default)
        {
            log?.Add("vault:remove");
            RemoveCalls++;
            if (RemoveFailure is not null) return Task.FromException(RemoveFailure);
            Credential = null;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAccountStore(List<string>? log = null) : IAccountStore
    {
        public StoredAccount? Account { get; set; }
        public ClientState SnapshotState { get; set; } = ClientState.Empty;
        public IReadOnlyList<ConversationKey> RecentDirectMessages { get; set; } = [];
        public bool IsUnlocked { get; set; } = true;
        public int ClearCalls { get; private set; }
        public int InitializeCalls { get; private set; }
        public int QueryTopicCalls { get; private set; }
        public int LockAttempts { get; private set; }
        public Exception? InitializeFailure { get; set; }
        public Exception? MigrateFailure { get; set; }
        public Exception? ApplyFailure { get; set; }
        public Exception? PurgeFailure { get; set; }
        public Exception? LockFailure { get; set; }
        public List<IReadOnlyCollection<DomainEvent>> AppliedBatches { get; } = [];
        public List<IReadOnlyCollection<ChatMessage>> StoredPages { get; } = [];
        public List<long> PurgedChannels { get; } = [];
        public Func<AccountId, ConversationKey, long?, int, CancellationToken, Task<IReadOnlyList<ChatMessage>>>? QueryHandler { get; set; }
        public Func<AccountId, IReadOnlyCollection<ChannelTopic>, CancellationToken, Task<IReadOnlyList<TopicSummary>>>? QueryTopicsHandler { get; set; }

        public Task<IReadOnlyList<StoredAccount>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StoredAccount>>(Account is null ? [] : [Account]);

        public Task InitializeAsync(StoredAccount account, CancellationToken cancellationToken = default)
        {
            log?.Add("store:initialize");
            InitializeCalls++;
            if (InitializeFailure is not null) return Task.FromException(InitializeFailure);
            Account = account;
            return Task.CompletedTask;
        }

        public Task MigrateAsync(AccountId accountId, CancellationToken cancellationToken = default)
        {
            log?.Add("store:migrate");
            if (MigrateFailure is not null) return Task.FromException(MigrateFailure);
            return Task.CompletedTask;
        }

        public Task<AccountSnapshot?> LoadAsync(AccountId accountId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Account is null ? null : new AccountSnapshot(
                Account,
                IsUnlocked,
                IsUnlocked ? SnapshotState : ClientState.Empty,
                IsUnlocked ? RecentDirectMessages : []));

        public Task<IReadOnlyList<ChatMessage>> QueryMessagesAsync(
            AccountId accountId, ConversationKey conversation, long? beforeMessageId, int limit,
            CancellationToken cancellationToken = default) => QueryHandler?.Invoke(accountId, conversation, beforeMessageId, limit, cancellationToken)
                ?? Task.FromResult<IReadOnlyList<ChatMessage>>(SnapshotState.Messages.Values
                    .Where(message => message.Conversation == conversation &&
                        (beforeMessageId is null || message.Id < beforeMessageId))
                    .OrderByDescending(message => message.Id)
                    .Take(limit)
                    .OrderBy(message => message.Id)
                    .ToArray());

        public async Task<MessagePage> QueryMessagePageAsync(
            AccountId accountId, ConversationKey conversation, long? beforeMessageId, int limit,
            CancellationToken cancellationToken = default)
        {
            var messages = await QueryMessagesAsync(accountId, conversation, beforeMessageId, limit + 1, cancellationToken);
            return new MessagePage(
                messages.OrderByDescending(message => message.Id).Take(limit).OrderBy(message => message.Id).ToArray(),
                messages.Count > limit);
        }

        public Task StoreMessagePageAsync(
            AccountId accountId, IReadOnlyCollection<ChatMessage> messages,
            CancellationToken cancellationToken = default)
        {
            StoredPages.Add(messages.ToArray());
            SnapshotState = DomainReducer.Apply(
                SnapshotState,
                messages.Select(message => new MessageUpsertEvent(message, Source: DomainEventSource.History)));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TopicSummary>> QueryTopicSummariesAsync(
            AccountId accountId,
            IReadOnlyCollection<ChannelTopic> topics,
            CancellationToken cancellationToken = default)
        {
            QueryTopicCalls++;
            return QueryTopicsHandler?.Invoke(accountId, topics, cancellationToken) ??
                Task.FromResult<IReadOnlyList<TopicSummary>>(
                    SnapshotState.Topics.Values
                        .Where(topic => topics.Contains(new ChannelTopic(topic.ChannelId, topic.Topic)))
                        .ToArray());
        }

        public Task ReplaceRegisterSnapshotAsync(AccountId accountId, RegisterResult snapshot, CancellationToken cancellationToken = default)
        {
            log?.Add("store:replace");
            SnapshotState = new ClientState(
                subscriptions: snapshot.Subscriptions.ToDictionary(item => item.ChannelId),
                users: snapshot.Users.ToDictionary(item => item.UserId),
                unread: snapshot.Unread);
            SnapshotState = DomainReducer.Apply(SnapshotState, snapshot.Events);
            return Task.CompletedTask;
        }

        public Task ApplyBatchAsync(AccountId accountId, IReadOnlyCollection<DomainEvent> events, CancellationToken cancellationToken = default)
        {
            if (ApplyFailure is not null) return Task.FromException(ApplyFailure);
            AppliedBatches.Add(events.ToArray());
            SnapshotState = DomainReducer.Apply(SnapshotState, events);
            return Task.CompletedTask;
        }

        public Task PurgeSubscriptionAsync(AccountId accountId, long channelId, CancellationToken cancellationToken = default)
        {
            PurgedChannels.Add(channelId);
            if (PurgeFailure is not null) return Task.FromException(PurgeFailure);
            SnapshotState = DomainReducer.Apply(
                SnapshotState,
                new SubscriptionRemovedEvent(channelId, Source: DomainEventSource.Local));
            return Task.CompletedTask;
        }

        public Task<bool> IsCacheUnlockedAsync(AccountId accountId, CancellationToken cancellationToken = default) =>
            Task.FromResult(IsUnlocked);

        public Task SetCacheUnlockedAsync(AccountId accountId, bool isUnlocked, CancellationToken cancellationToken = default)
        {
            log?.Add($"store:unlock:{isUnlocked}");
            if (!isUnlocked)
            {
                LockAttempts++;
                if (LockFailure is not null) return Task.FromException(LockFailure);
            }
            IsUnlocked = isUnlocked;
            return Task.CompletedTask;
        }

        public Task ClearAsync(AccountId accountId, CancellationToken cancellationToken = default)
        {
            ClearCalls++;
            SnapshotState = ClientState.Empty;
            Account = null;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeGateway(List<string>? log = null) : IZulipGateway
    {
        public int ProbeCalls { get; private set; }
        public int AuthenticateCalls { get; private set; }
        public int RegisterCalls { get; private set; }
        public int GetEventsCalls { get; private set; }
        public int SendCalls { get; private set; }
        public int TopicsCalls { get; private set; }
        public int DeleteQueueCalls { get; private set; }
        public int UploadCalls { get; private set; }
        public int CancelledLongPolls { get; private set; }
        public TaskCompletionSource<bool> RegisterEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> AuthenticateEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Exception? ProbeFailure { get; set; }
        public Exception? AuthenticateFailure { get; set; }
        public bool BlockAuthenticationUntilCancelled { get; set; }
        public List<HistoryRequest> HistoryRequests { get; } = [];
        public List<MessageAroundRequest> AroundRequests { get; } = [];
        public List<GetEventsRequest> GetEventsRequests { get; } = [];
        public List<SendRequest> SendRequests { get; } = [];
        public List<MarkReadRequest> MarkReadRequests { get; } = [];
        public List<DeleteQueueRequest> DeleteQueueRequests { get; } = [];
        public List<SubscribeChannelRequest> SubscribeRequests { get; } = [];
        public List<SetSubscriptionPreferenceRequest> PreferenceRequests { get; } = [];
        public int AvailableChannelsCalls { get; private set; }
        public Func<RegisterRequest, CancellationToken, Task<RegisterResult>> RegisterHandler { get; set; } = (_, _) => Task.FromResult(Register());
        public Func<GetEventsRequest, CancellationToken, Task<EventBatch>>? GetEventsHandler { get; set; }
        public Func<HistoryRequest, CancellationToken, Task<HistoryResult>> HistoryHandler { get; set; } = (_, _) => Task.FromResult(new HistoryResult([], false, false));
        public Func<MessageSearchRequest, CancellationToken, Task<MessageQueryPage>>? SearchHandler { get; set; }
        public Func<SavedMessagesRequest, CancellationToken, Task<MessageQueryPage>>? SavedHandler { get; set; }
        public Func<MessageAroundRequest, CancellationToken, Task<HistoryResult>>? AroundHandler { get; set; }
        public Func<SendRequest, CancellationToken, Task<SendResult>>? SendHandler { get; set; }
        public Func<MarkReadRequest, CancellationToken, Task>? MarkReadHandler { get; set; }
        public Func<SetReactionRequest, CancellationToken, Task>? SetReactionHandler { get; set; }
        public Func<EditMessageRequest, CancellationToken, Task>? EditMessageHandler { get; set; }
        public Func<DeleteMessageRequest, CancellationToken, Task>? DeleteMessageHandler { get; set; }
        public Func<SetMessageStarredRequest, CancellationToken, Task>? SetMessageStarredHandler { get; set; }
        public Func<UploadAttachmentRequest, CancellationToken, Task<UploadedAttachment>>? UploadAttachmentHandler { get; set; }
        public Func<GetRealmMediaRequest, CancellationToken, Task<RealmMediaResult>>? GetRealmMediaHandler { get; set; }
        public Func<UnsubscribeChannelRequest, CancellationToken, Task<UnsubscribeChannelResult>>? UnsubscribeChannelHandler { get; set; }
        public Func<AvailableChannelsRequest, CancellationToken, Task<IReadOnlyList<ChannelSummary>>>? AvailableChannelsHandler { get; set; }
        public Func<SubscribeChannelRequest, CancellationToken, Task<SubscribeChannelResult>>? SubscribeChannelHandler { get; set; }
        public Func<SetSubscriptionPreferenceRequest, CancellationToken, Task>? SetSubscriptionPreferenceHandler { get; set; }
        public Func<DeleteQueueRequest, CancellationToken, Task>? DeleteQueueHandler { get; set; }
        public Func<TopicsRequest, CancellationToken, Task<TopicsResult>> TopicsHandler { get; set; } = (_, _) => Task.FromResult(new TopicsResult([]));

        public Task<RealmProbeResult> ProbeRealmAsync(RealmEndpoint realm, CancellationToken cancellationToken = default)
        {
            log?.Add("gateway:probe");
            ProbeCalls++;
            if (ProbeFailure is not null) return Task.FromException<RealmProbeResult>(ProbeFailure);
            return Task.FromResult(new RealmProbeResult(realm, "10", 500, false, true));
        }

        public async Task<AuthenticationResult> AuthenticateAsync(AuthenticationRequest request, CancellationToken cancellationToken = default)
        {
            log?.Add("gateway:authenticate");
            AuthenticateCalls++;
            AuthenticateEntered.TrySetResult(true);
            if (AuthenticateFailure is not null) throw AuthenticateFailure;
            if (BlockAuthenticationUntilCancelled)
            {
                return await Never<AuthenticationResult>(cancellationToken);
            }
            var credential = new CredentialEnvelope(request.Realm, request.Email, 10, "api-key");
            return new AuthenticationResult(credential, new UserProfile(10, "Me", request.Email));
        }

        public Task<RegisterResult> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default)
        {
            log?.Add("gateway:register");
            RegisterCalls++;
            RegisterEntered.TrySetResult(true);
            return RegisterHandler(request, cancellationToken);
        }

        public async Task<EventBatch> GetEventsAsync(GetEventsRequest request, CancellationToken cancellationToken = default)
        {
            GetEventsCalls++;
            GetEventsRequests.Add(request);
            try
            {
                return GetEventsHandler is null
                    ? await Never<EventBatch>(cancellationToken)
                    : await GetEventsHandler(request, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CancelledLongPolls++;
                throw;
            }
        }

        public Task<HistoryResult> GetHistoryAsync(HistoryRequest request, CancellationToken cancellationToken = default)
        {
            HistoryRequests.Add(request);
            return HistoryHandler(request, cancellationToken);
        }

        public Task<HistoryResult> GetMessagesAroundAsync(MessageAroundRequest request, CancellationToken cancellationToken = default)
        {
            AroundRequests.Add(request);
            return AroundHandler?.Invoke(request, cancellationToken) ??
                HistoryHandler(
                    new HistoryRequest(request.Credentials, request.Conversation, request.MessageId, includeAnchor: true, limit: request.BeforeCount + request.AfterCount + 1),
                    cancellationToken);
        }

        public Task<MessageQueryPage> SearchMessagesAsync(MessageSearchRequest request, CancellationToken cancellationToken = default) =>
            SearchHandler?.Invoke(request, cancellationToken) ?? Task.FromResult(new MessageQueryPage([], false, false, true));

        public Task<MessageQueryPage> LoadSavedMessagesAsync(SavedMessagesRequest request, CancellationToken cancellationToken = default) =>
            SavedHandler?.Invoke(request, cancellationToken) ?? Task.FromResult(new MessageQueryPage([], false, false, true));

        public Task<TopicsResult> GetTopicsAsync(TopicsRequest request, CancellationToken cancellationToken = default)
        {
            TopicsCalls++;
            return TopicsHandler(request, cancellationToken);
        }

        public Task<SendResult> SendAsync(SendRequest request, CancellationToken cancellationToken = default)
        {
            SendCalls++;
            SendRequests.Add(request);
            return SendHandler?.Invoke(request, cancellationToken) ?? Task.FromResult(new SendResult(request.LocalId, 500));
        }

        public Task MarkReadAsync(MarkReadRequest request, CancellationToken cancellationToken = default)
        {
            MarkReadRequests.Add(request);
            return MarkReadHandler?.Invoke(request, cancellationToken) ?? Task.CompletedTask;
        }

        public Task SetReactionAsync(SetReactionRequest request, CancellationToken cancellationToken = default) =>
            SetReactionHandler?.Invoke(request, cancellationToken) ?? Task.CompletedTask;

        public Task EditMessageAsync(EditMessageRequest request, CancellationToken cancellationToken = default) =>
            EditMessageHandler?.Invoke(request, cancellationToken) ?? Task.CompletedTask;

        public Task DeleteMessageAsync(DeleteMessageRequest request, CancellationToken cancellationToken = default) =>
            DeleteMessageHandler?.Invoke(request, cancellationToken) ?? Task.CompletedTask;

        public Task SetMessageStarredAsync(SetMessageStarredRequest request, CancellationToken cancellationToken = default) =>
            SetMessageStarredHandler?.Invoke(request, cancellationToken) ?? Task.CompletedTask;

        public Task<UploadedAttachment> UploadAttachmentAsync(UploadAttachmentRequest request, CancellationToken cancellationToken = default)
        {
            UploadCalls++;
            return UploadAttachmentHandler?.Invoke(request, cancellationToken) ??
                Task.FromResult(new UploadedAttachment(request.Upload.FileName, $"https://zulip.example/user_uploads/{request.Upload.FileName}"));
        }

        public Task<RealmMediaResult> GetRealmMediaAsync(GetRealmMediaRequest request, CancellationToken cancellationToken = default) =>
            GetRealmMediaHandler?.Invoke(request, cancellationToken) ??
            Task.FromResult(new RealmMediaResult([1], "image/png"));

        public Task<UnsubscribeChannelResult> UnsubscribeChannelAsync(
            UnsubscribeChannelRequest request,
            CancellationToken cancellationToken = default) =>
            UnsubscribeChannelHandler?.Invoke(request, cancellationToken) ??
            Task.FromResult(new UnsubscribeChannelResult([request.ChannelName], []));

        public Task<IReadOnlyList<ChannelSummary>> GetAvailableChannelsAsync(AvailableChannelsRequest request, CancellationToken cancellationToken = default)
        {
            AvailableChannelsCalls++;
            return AvailableChannelsHandler?.Invoke(request, cancellationToken) ?? Task.FromResult<IReadOnlyList<ChannelSummary>>([]);
        }

        public Task<SubscribeChannelResult> SubscribeToChannelAsync(SubscribeChannelRequest request, CancellationToken cancellationToken = default)
        {
            SubscribeRequests.Add(request);
            return SubscribeChannelHandler?.Invoke(request, cancellationToken) ??
                Task.FromResult(new SubscribeChannelResult([request.Channel.Name], [], []));
        }

        public Task SetSubscriptionPreferenceAsync(SetSubscriptionPreferenceRequest request, CancellationToken cancellationToken = default)
        {
            PreferenceRequests.Add(request);
            return SetSubscriptionPreferenceHandler?.Invoke(request, cancellationToken) ?? Task.CompletedTask;
        }

        public Task DeleteQueueAsync(DeleteQueueRequest request, CancellationToken cancellationToken = default)
        {
            DeleteQueueCalls++;
            DeleteQueueRequests.Add(request);
            return DeleteQueueHandler?.Invoke(request, cancellationToken) ?? Task.CompletedTask;
        }
    }
}
