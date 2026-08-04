using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RelayCove.Client.Accounts;
using RelayCove.Client.Activation;
using RelayCove.Client.Auth;
using RelayCove.Client.Notifications;
using RelayCove.Client.Storage;
using RelayCove.Client.Sync;
using RelayCove.Shared.Auth;
using RelayCove.Shared.Conversations;
using RelayCove.Shared.Messages;
using RelayCove.Shared.Realtime;

namespace RelayCove.Client.Tests.Accounts;

public sealed class ClientAccountShellCoordinatorTests
{
    private static readonly Uri ServerBaseUri = new("https://relay.example/");
    private static readonly Guid UserId = Guid.Parse("4f48783e-79e5-4131-a3cf-e9d84343681a");
    private static readonly DateTimeOffset Now = new(2026, 8, 4, 1, 2, 3, TimeSpan.Zero);

    [Fact]
    public async Task RestoreAsync_WhenCredentialIsMissing_PublishesSignedOutFallback()
    {
        var authentication = new FakeAuthentication
        {
            RestoreOutcome = PersistentClientAuthenticationOutcome.Failure(
                PersistentClientAuthenticationStatus.NoStoredCredential),
        };
        using var router = CreateRouter();
        await using var coordinator = CreateCoordinator(authentication, new FakeRuntimeFactory(), router);

        await coordinator.RestoreAsync();

        Assert.Equal(1, authentication.RestoreCount);
        Assert.Equal(ClientAccountShellPhase.SignedOut, coordinator.Snapshot.Phase);
        Assert.Equal(
            PersistentClientAuthenticationStatus.NoStoredCredential,
            coordinator.Snapshot.AuthenticationStatus);
    }

    [Fact]
    public async Task LoginAsync_WhenInputIsInvalid_DoesNotCallAuthentication()
    {
        var authentication = new FakeAuthentication();
        using var router = CreateRouter();
        await using var coordinator = CreateCoordinator(authentication, new FakeRuntimeFactory(), router);

        await coordinator.LoginAsync("not-a-server", " ", "");
        await coordinator.LoginAsync(
            ServerBaseUri.AbsoluteUri,
            new string('u', 65),
            "secret");
        await coordinator.LoginAsync(
            ServerBaseUri.AbsoluteUri,
            "shell-user",
            new string('p', 1_025));

        Assert.Equal(0, authentication.LoginCount);
        Assert.Equal(ClientAccountShellPhase.SignedOut, coordinator.Snapshot.Phase);
        Assert.Equal(
            PersistentClientAuthenticationStatus.ValidationFailed,
            coordinator.Snapshot.AuthenticationStatus);
    }

    [Fact]
    public async Task LoginAsync_WhenRuntimeStarts_ActivatesAuthorizedAccount()
    {
        var session = CreateSession();
        var authentication = new FakeAuthentication
        {
            LoginOutcome = PersistentClientAuthenticationOutcome.Authenticated(
                session,
                isCredentialPersisted: true),
        };
        var runtime = new FakeRuntime(session);
        var factory = new FakeRuntimeFactory { Runtime = runtime };
        var navigated = 0;
        using var router = CreateRouter(_ => navigated++);
        await using var coordinator = CreateCoordinator(authentication, factory, router);

        await coordinator.LoginAsync(ServerBaseUri.AbsoluteUri, "shell-user", "secret");
        var routeStatus = router.TryRoute(
            ClientNotificationActivationTarget.UnreadOverview(runtime.Identity.Id));

        Assert.Equal(1, authentication.LoginCount);
        Assert.Equal("shell-user", authentication.LastLoginRequest!.UserName);
        Assert.Equal("secret", authentication.LastLoginRequest.Password);
        Assert.Equal(1, runtime.StartCount);
        Assert.Equal(ClientAccountShellPhase.Active, coordinator.Snapshot.Phase);
        Assert.Equal(ConnectionState.Connected, coordinator.Snapshot.ConnectionState);
        Assert.Equal(ClientSyncRunStatus.Completed, coordinator.Snapshot.LastSyncStatus);
        Assert.Equal(ClientNotificationActivationRouteStatus.Accepted, routeStatus);
        Assert.Equal(1, navigated);
    }

    [Fact]
    public async Task LoginAsync_WhenConversationListIsReady_PublishesUnreadAndContinuousConnection()
    {
        var session = CreateSession();
        var conversationId = Guid.NewGuid();
        var runtime = new FakeRuntime(session)
        {
            ConversationListOutcome = CreateConversationListOutcome(
                conversationId,
                totalUnreadCount: 7),
        };
        using var router = CreateRouter();
        await using var coordinator = CreateCoordinator(
            Authenticated(session),
            new FakeRuntimeFactory { Runtime = runtime },
            router);
        var listPublished = NewSignal();
        coordinator.ConversationListChanged += outcome =>
        {
            if (outcome.Status == LocalCacheOperationStatus.Ready)
            {
                listPublished.TrySetResult();
            }
        };

        await coordinator.LoginAsync(ServerBaseUri.AbsoluteUri, "shell-user", "secret");
        await listPublished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        runtime.RaiseConnectionStateChanged(ConnectionState.Reconnecting);

        Assert.Equal(7, coordinator.ConversationList.TotalUnreadCount);
        Assert.Equal(7, coordinator.Snapshot.TotalUnreadCount);
        Assert.Equal(ConnectionState.Reconnecting, coordinator.Snapshot.ConnectionState);
        Assert.Equal(ClientAccountShellPhase.Active, coordinator.Snapshot.Phase);
        Assert.Equal(conversationId, Assert.Single(coordinator.ConversationList.Conversations).Id);
    }

    [Fact]
    public async Task LogoutAsync_WhenRuntimeRaisesLateState_DetachesWithoutDeadlockOrResurrection()
    {
        var session = CreateSession();
        var runtime = new FakeRuntime(session);
        runtime.LogoutAction = _ =>
        {
            runtime.RaiseConnectionStateChanged(ConnectionState.Disconnected);
            runtime.RaiseConversationStateChanged(20);
            return Task.FromResult(ClientLogoutStatus.LoggedOut);
        };
        using var router = CreateRouter();
        await using var coordinator = CreateCoordinator(
            Authenticated(session),
            new FakeRuntimeFactory { Runtime = runtime },
            router);
        await coordinator.LoginAsync(ServerBaseUri.AbsoluteUri, "shell-user", "secret");

        await coordinator.LogoutAsync().WaitAsync(TimeSpan.FromSeconds(5));
        runtime.RaiseConnectionStateChanged(ConnectionState.Connected);
        runtime.RaiseConversationStateChanged(21);

        Assert.Equal(ClientAccountShellPhase.SignedOut, coordinator.Snapshot.Phase);
        Assert.Equal(ConnectionState.Disconnected, coordinator.Snapshot.ConnectionState);
        Assert.Equal(0, coordinator.Snapshot.TotalUnreadCount);
        Assert.Equal(
            LocalCacheOperationStatus.AuthoritativeSnapshotRequired,
            coordinator.ConversationList.Status);
        Assert.Empty(coordinator.ConversationList.Conversations);
    }

    [Fact]
    public async Task RetryAsync_WhenRuntimeRaisesStateInline_CompletesWithoutGateDeadlock()
    {
        var session = CreateSession();
        var runtime = new FakeRuntime(session);
        runtime.RetryAction = _ =>
        {
            runtime.RaiseConnectionStateChanged(ConnectionState.Reconnecting);
            runtime.RaiseConversationStateChanged(30);
            return Task.FromResult(new ClientSyncRunOutcome(
                ClientSyncRunStatus.Completed,
                SyncReason.Reconnect,
                RoundsExecuted: 1));
        };
        using var router = CreateRouter();
        await using var coordinator = CreateCoordinator(
            Authenticated(session),
            new FakeRuntimeFactory { Runtime = runtime },
            router);
        await coordinator.LoginAsync(ServerBaseUri.AbsoluteUri, "shell-user", "secret");

        await coordinator.RetryAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ClientAccountShellPhase.Active, coordinator.Snapshot.Phase);
        Assert.Equal(ClientSyncRunStatus.Completed, coordinator.Snapshot.LastSyncStatus);
    }

    [Fact]
    public async Task LoginAsync_WhenFactoryFails_DisposesUnownedAuthenticationSession()
    {
        var session = CreateSession();
        var authentication = new FakeAuthentication
        {
            LoginOutcome = PersistentClientAuthenticationOutcome.Authenticated(
                session,
                isCredentialPersisted: false),
        };
        var factory = new FakeRuntimeFactory
        {
            CreateException = new IOException("classified factory detail"),
        };
        var logger = new RecordingLogger<ClientAccountShellCoordinator>();
        using var router = CreateRouter();
        await using var coordinator = CreateCoordinator(authentication, factory, router, logger);

        await coordinator.LoginAsync(ServerBaseUri.AbsoluteUri, "shell-user", "secret");

        Assert.True(session.IsDisposeCompleted);
        Assert.Equal(ClientAccountShellPhase.SignedOut, coordinator.Snapshot.Phase);
        Assert.Contains(logger.Entries, entry => entry.Contains("IOException", StringComparison.Ordinal));
        Assert.DoesNotContain(
            logger.Entries,
            entry => entry.Contains("classified factory detail", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LoginAsync_WhenShutdownWinsStartRace_DisposesUncommittedRuntime()
    {
        var session = CreateSession();
        var authentication = new FakeAuthentication
        {
            LoginOutcome = PersistentClientAuthenticationOutcome.Authenticated(
                session,
                isCredentialPersisted: true),
        };
        var startEntered = NewSignal();
        var releaseStart = NewSignal();
        var runtime = new FakeRuntime(session)
        {
            StartAction = async _ =>
            {
                startEntered.TrySetResult();
                await releaseStart.Task;
                return Started();
            },
        };
        using var router = CreateRouter();
        var coordinator = CreateCoordinator(
            authentication,
            new FakeRuntimeFactory { Runtime = runtime },
            router);

        var login = coordinator.LoginAsync(ServerBaseUri.AbsoluteUri, "shell-user", "secret");
        await startEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var dispose = coordinator.DisposeAsync().AsTask();
        releaseStart.TrySetResult();
        await Task.WhenAll(login, dispose).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, runtime.DisposeCount);
        Assert.Equal(ClientAccountShellPhase.Stopping, coordinator.Snapshot.Phase);
        Assert.Equal(
            ClientNotificationActivationRouteStatus.NoActiveAccount,
            router.TryRoute(ClientNotificationActivationTarget.UnreadOverview(runtime.Identity.Id)));
    }

    [Fact]
    public async Task LoginAsync_WhenSubmittedTwice_AllowsOnlyOneAuthenticationAndRuntime()
    {
        var session = CreateSession();
        var authenticationEntered = NewSignal();
        var releaseAuthentication = NewSignal();
        var authentication = new FakeAuthentication
        {
            LoginAction = async _ =>
            {
                authenticationEntered.TrySetResult();
                await releaseAuthentication.Task;
                return PersistentClientAuthenticationOutcome.Authenticated(
                    session,
                    isCredentialPersisted: true);
            },
        };
        var runtime = new FakeRuntime(session);
        using var router = CreateRouter();
        await using var coordinator = CreateCoordinator(
            authentication,
            new FakeRuntimeFactory { Runtime = runtime },
            router);

        var first = coordinator.LoginAsync(ServerBaseUri.AbsoluteUri, "shell-user", "secret");
        await authenticationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = coordinator.LoginAsync(ServerBaseUri.AbsoluteUri, "shell-user", "secret");
        releaseAuthentication.TrySetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, authentication.LoginCount);
        Assert.Equal(1, runtime.StartCount);
        Assert.Equal(ClientAccountShellPhase.Active, coordinator.Snapshot.Phase);
    }

    [Fact]
    public async Task LoginAsync_WhenCallerCancelsStart_DisposesRuntimeAndReturnsSignedOut()
    {
        var session = CreateSession();
        var startEntered = NewSignal();
        var runtime = new FakeRuntime(session)
        {
            StartAction = async token =>
            {
                startEntered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return Started();
            },
        };
        using var router = CreateRouter();
        await using var coordinator = CreateCoordinator(
            Authenticated(session),
            new FakeRuntimeFactory { Runtime = runtime },
            router);
        using var cancellation = new CancellationTokenSource();

        var login = coordinator.LoginAsync(
            ServerBaseUri.AbsoluteUri,
            "shell-user",
            "secret",
            cancellation.Token);
        await startEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await login.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, runtime.DisposeCount);
        Assert.Equal(ClientAccountShellPhase.SignedOut, coordinator.Snapshot.Phase);
    }

    [Fact]
    public async Task LoginAsync_WhenStartupRequiresAuthentication_LogsOutAndReturnsSignedOut()
    {
        var session = CreateSession();
        var runtime = new FakeRuntime(session)
        {
            StartOutcome = new ClientAccountRuntimeStartOutcome(
                ConnectionState.Disconnected,
                new ClientSyncRunOutcome(
                    ClientSyncRunStatus.AuthenticationRequired,
                    SyncReason.Startup,
                    RoundsExecuted: 0)),
            LogoutAction = _ => Task.FromResult(
                ClientLogoutStatus.CredentialClearFailed),
        };
        using var router = CreateRouter();
        await using var coordinator = CreateCoordinator(
            Authenticated(session),
            new FakeRuntimeFactory { Runtime = runtime },
            router);

        await coordinator.LoginAsync(ServerBaseUri.AbsoluteUri, "shell-user", "secret");

        Assert.Equal(1, runtime.LogoutCount);
        Assert.Equal(1, runtime.DisposeCount);
        Assert.Equal(ClientAccountShellPhase.SignedOut, coordinator.Snapshot.Phase);
        Assert.Equal(
            PersistentClientAuthenticationStatus.AuthenticationFailed,
            coordinator.Snapshot.AuthenticationStatus);
        Assert.Equal(
            ClientLogoutStatus.CredentialClearFailed,
            coordinator.Snapshot.LastLogoutStatus);
        Assert.Equal(
            ClientNotificationActivationRouteStatus.NoActiveAccount,
            router.TryRoute(ClientNotificationActivationTarget.UnreadOverview(runtime.Identity.Id)));
    }

    [Fact]
    public async Task LoginAsync_WhenInvalidRequestWaitsBehindSuccessfulLogin_DoesNotOverwriteActive()
    {
        var session = CreateSession();
        var authenticationEntered = NewSignal();
        var releaseAuthentication = NewSignal();
        var authentication = new FakeAuthentication
        {
            LoginAction = async _ =>
            {
                authenticationEntered.TrySetResult();
                await releaseAuthentication.Task;
                return PersistentClientAuthenticationOutcome.Authenticated(
                    session,
                    isCredentialPersisted: true);
            },
        };
        var runtime = new FakeRuntime(session);
        using var router = CreateRouter();
        await using var coordinator = CreateCoordinator(
            authentication,
            new FakeRuntimeFactory { Runtime = runtime },
            router);

        var valid = coordinator.LoginAsync(
            ServerBaseUri.AbsoluteUri,
            "shell-user",
            "secret");
        await authenticationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var invalid = coordinator.LoginAsync("invalid", "", "");
        releaseAuthentication.TrySetResult();
        await Task.WhenAll(valid, invalid).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, authentication.LoginCount);
        Assert.Equal(ClientAccountShellPhase.Active, coordinator.Snapshot.Phase);
        Assert.Equal(
            PersistentClientAuthenticationStatus.Authenticated,
            coordinator.Snapshot.AuthenticationStatus);
    }

    [Fact]
    public async Task RetryAsync_WhenAccountIsActive_UpdatesConnectionAndSyncStatus()
    {
        var session = CreateSession();
        var authentication = Authenticated(session);
        var runtime = new FakeRuntime(session)
        {
            RetryOutcome = new ClientSyncRunOutcome(
                ClientSyncRunStatus.RemoteFailure,
                SyncReason.Reconnect,
                RoundsExecuted: 1),
            ConnectionStateValue = ConnectionState.Reconnecting,
        };
        using var router = CreateRouter();
        await using var coordinator = CreateCoordinator(
            authentication,
            new FakeRuntimeFactory { Runtime = runtime },
            router);
        await coordinator.LoginAsync(ServerBaseUri.AbsoluteUri, "shell-user", "secret");

        await coordinator.RetryAsync();

        Assert.Equal(1, runtime.RetryCount);
        Assert.Equal(ClientAccountShellPhase.Active, coordinator.Snapshot.Phase);
        Assert.Equal(ConnectionState.Reconnecting, coordinator.Snapshot.ConnectionState);
        Assert.Equal(ClientSyncRunStatus.RemoteFailure, coordinator.Snapshot.LastSyncStatus);
    }

    [Fact]
    public async Task RetryAsync_WhenStartupCacheWasNotReady_ActivatesOnlyAfterCompletedSync()
    {
        var session = CreateSession();
        var runtime = new FakeRuntime(session)
        {
            StartOutcome = new ClientAccountRuntimeStartOutcome(
                ConnectionState.Disconnected,
                new ClientSyncRunOutcome(
                    ClientSyncRunStatus.RemoteFailure,
                    SyncReason.Startup,
                    RoundsExecuted: 0)),
        };
        var navigated = 0;
        using var router = CreateRouter(_ => navigated++);
        await using var coordinator = CreateCoordinator(
            Authenticated(session),
            new FakeRuntimeFactory { Runtime = runtime },
            router);
        var target = ClientNotificationActivationTarget.UnreadOverview(runtime.Identity.Id);

        await coordinator.LoginAsync(ServerBaseUri.AbsoluteUri, "shell-user", "secret");
        Assert.Equal(
            ClientNotificationActivationRouteStatus.NoActiveAccount,
            router.TryRoute(target));

        await coordinator.RetryAsync();

        Assert.Equal(1, runtime.RetryCount);
        Assert.Equal(ClientNotificationActivationRouteStatus.Duplicate, router.TryRoute(target));
        Assert.Equal(1, navigated);
    }

    [Fact]
    public async Task RetryAsync_WhenAuthenticationIsRequired_RevokesAndLogsOutAccount()
    {
        var session = CreateSession();
        var runtime = new FakeRuntime(session)
        {
            RetryOutcome = new ClientSyncRunOutcome(
                ClientSyncRunStatus.AuthenticationRequired,
                SyncReason.Reconnect,
                RoundsExecuted: 0),
        };
        using var router = CreateRouter();
        await using var coordinator = CreateCoordinator(
            Authenticated(session),
            new FakeRuntimeFactory { Runtime = runtime },
            router);
        await coordinator.LoginAsync(ServerBaseUri.AbsoluteUri, "shell-user", "secret");

        await coordinator.RetryAsync();

        Assert.Equal(1, runtime.LogoutCount);
        Assert.Equal(1, runtime.DisposeCount);
        Assert.Equal(ClientAccountShellPhase.SignedOut, coordinator.Snapshot.Phase);
        Assert.Equal(
            PersistentClientAuthenticationStatus.AuthenticationFailed,
            coordinator.Snapshot.AuthenticationStatus);
        Assert.Equal(
            ClientNotificationActivationRouteStatus.NoActiveAccount,
            router.TryRoute(ClientNotificationActivationTarget.UnreadOverview(runtime.Identity.Id)));
    }

    [Fact]
    public async Task LogoutAsync_RevokesActivationBeforeRuntimeLogoutAndReturnsSignedOut()
    {
        var session = CreateSession();
        var authentication = Authenticated(session);
        using var router = CreateRouter();
        var runtime = new FakeRuntime(session);
        var target = ClientNotificationActivationTarget.UnreadOverview(runtime.Identity.Id);
        runtime.LogoutAction = _ =>
        {
            Assert.Equal(
                ClientNotificationActivationRouteStatus.NoActiveAccount,
                router.TryRoute(target));
            return Task.FromResult(ClientLogoutStatus.LoggedOut);
        };
        await using var coordinator = CreateCoordinator(
            authentication,
            new FakeRuntimeFactory { Runtime = runtime },
            router);
        await coordinator.LoginAsync(ServerBaseUri.AbsoluteUri, "shell-user", "secret");

        await coordinator.LogoutAsync();

        Assert.Equal(1, runtime.LogoutCount);
        Assert.Equal(1, runtime.DisposeCount);
        Assert.Equal(ClientAccountShellPhase.SignedOut, coordinator.Snapshot.Phase);
        Assert.Equal(ClientLogoutStatus.LoggedOut, coordinator.Snapshot.LastLogoutStatus);
    }

    [Fact]
    public async Task UpdateActivity_WhenAccountIsActive_ForwardsLatestWindowState()
    {
        var session = CreateSession();
        var runtime = new FakeRuntime(session);
        using var router = CreateRouter();
        await using var coordinator = CreateCoordinator(
            Authenticated(session),
            new FakeRuntimeFactory { Runtime = runtime },
            router);
        await coordinator.LoginAsync(ServerBaseUri.AbsoluteUri, "shell-user", "secret");
        var activity = new ClientActivitySnapshot(true, false, true, OpenConversationId: null);

        coordinator.UpdateActivity(activity);

        Assert.Same(activity, runtime.LastActivity);
    }

    [Fact]
    public async Task UpdateActivity_BeforeAccountStarts_IsRestoredIntoNewRuntime()
    {
        var session = CreateSession();
        var runtime = new FakeRuntime(session);
        using var router = CreateRouter();
        await using var coordinator = CreateCoordinator(
            Authenticated(session),
            new FakeRuntimeFactory { Runtime = runtime },
            router);
        var activity = new ClientActivitySnapshot(true, false, true, OpenConversationId: null);

        coordinator.UpdateActivity(activity);
        await coordinator.LoginAsync(ServerBaseUri.AbsoluteUri, "shell-user", "secret");

        Assert.Same(activity, runtime.LastActivity);
    }

    [Fact]
    public async Task SelectConversation_UntilCurrentSnapshotIsApplied_DoesNotPublishActivityOrRead()
    {
        var session = CreateSession();
        var conversationId = Guid.NewGuid();
        var message = CreateMessage(10, conversationId);
        var historyEntered = NewSignal();
        var releaseHistory = NewSignal();
        var rendered = NewSignal();
        var markedMessageIds = new ConcurrentQueue<long>();
        var runtime = new FakeRuntime(session)
        {
            ConversationListOutcome = CreateConversationListOutcome(conversationId, 1),
            MessagePageReadAction = (id, _, _, _) => Task.FromResult(
                new LocalMessagePageReadOutcome(
                    LocalCacheOperationStatus.Ready,
                    id,
                    [message],
                    NextBeforeMessageId: null,
                    HasMoreBefore: false)),
            MessageHistoryLoadAction = async (_, _, _, _) =>
            {
                historyEntered.TrySetResult();
                await releaseHistory.Task;
                return new ClientMessageHistoryPageOutcome(
                    ClientMessageLoadStatus.Completed,
                    [message],
                    NextBeforeMessageId: null,
                    HasMore: false);
            },
            MarkRenderedAction = (id, messageId, _) =>
            {
                Assert.Equal(conversationId, id);
                markedMessageIds.Enqueue(messageId);
                rendered.TrySetResult();
                return Task.FromResult(LocalCacheOperationStatus.Ready);
            },
        };
        using var router = CreateRouter();
        await using var coordinator = CreateCoordinator(
            Authenticated(session),
            new FakeRuntimeFactory { Runtime = runtime },
            router);
        await coordinator.LoginAsync(ServerBaseUri.AbsoluteUri, "shell-user", "secret");
        await WaitUntilAsync(() => coordinator.ConversationList.Status ==
            LocalCacheOperationStatus.Ready);
        coordinator.UpdateActivity(new ClientActivitySnapshot(
            true,
            false,
            true,
            OpenConversationId: null));
        var localPublished = NewSignal();
        coordinator.MessageListChanged += snapshot =>
        {
            if (snapshot.Status == ClientMessageListStatus.Ready &&
                snapshot.Messages.Count == 1)
            {
                localPublished.TrySetResult();
            }
        };

        coordinator.SelectConversation(conversationId);
        await localPublished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await historyEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(runtime.LastActivity!.OpenConversationId);
        Assert.Empty(markedMessageIds);
        var applied = coordinator.MessageList;
        coordinator.AcknowledgeMessageSnapshotApplied(
            conversationId,
            applied.Revision,
            observedThroughMessageId: 10,
            isAtLatestRegion: true);
        await rendered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(conversationId, runtime.LastActivity.OpenConversationId);
        Assert.Equal([10L], markedMessageIds);
        coordinator.AcknowledgeMessageSnapshotApplied(
            conversationId,
            applied.Revision,
            observedThroughMessageId: null,
            isAtLatestRegion: false);
        Assert.Null(runtime.LastActivity.OpenConversationId);
        releaseHistory.TrySetResult();
    }

    [Fact]
    public async Task AcknowledgeMessageSnapshotApplied_WhenWindowIsHidden_DefersReadUntilForeground()
    {
        var session = CreateSession();
        var conversationId = Guid.NewGuid();
        var message = CreateMessage(10, conversationId);
        var rendered = NewSignal();
        var runtime = new FakeRuntime(session)
        {
            ConversationListOutcome = CreateConversationListOutcome(conversationId, 1),
            MessagePageReadAction = (id, _, _, _) => Task.FromResult(
                new LocalMessagePageReadOutcome(
                    LocalCacheOperationStatus.Ready,
                    id,
                    [message],
                    NextBeforeMessageId: null,
                    HasMoreBefore: false)),
            MarkRenderedAction = (_, _, _) =>
            {
                rendered.TrySetResult();
                return Task.FromResult(LocalCacheOperationStatus.Ready);
            },
        };
        using var router = CreateRouter();
        await using var coordinator = CreateCoordinator(
            Authenticated(session),
            new FakeRuntimeFactory { Runtime = runtime },
            router);
        await coordinator.LoginAsync(ServerBaseUri.AbsoluteUri, "shell-user", "secret");
        await WaitUntilAsync(() => coordinator.ConversationList.Status ==
            LocalCacheOperationStatus.Ready);
        coordinator.UpdateActivity(ClientActivitySnapshot.Inactive);
        coordinator.SelectConversation(conversationId);
        await WaitUntilAsync(() => coordinator.MessageList.Status ==
            ClientMessageListStatus.Ready && coordinator.MessageList.Messages.Count == 1);
        var applied = coordinator.MessageList;

        coordinator.AcknowledgeMessageSnapshotApplied(
            conversationId,
            applied.Revision,
            observedThroughMessageId: 10,
            isAtLatestRegion: true);
        await Task.Delay(50);

        Assert.False(rendered.Task.IsCompleted);
        Assert.Equal(conversationId, runtime.LastActivity!.OpenConversationId);
        coordinator.UpdateActivity(new ClientActivitySnapshot(
            true,
            false,
            true,
            OpenConversationId: null));
        await rendered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task SelectConversation_WhenHistoryRequiresAuthentication_EndsAccountSession()
    {
        var session = CreateSession();
        var conversationId = Guid.NewGuid();
        var runtime = new FakeRuntime(session)
        {
            ConversationListOutcome = CreateConversationListOutcome(conversationId, 1),
            MessageHistoryLoadAction = (_, _, _, _) => Task.FromResult(
                ClientMessageHistoryPageOutcome.Failure(
                    ClientMessageLoadStatus.AuthenticationRequired)),
        };
        using var router = CreateRouter();
        await using var coordinator = CreateCoordinator(
            Authenticated(session),
            new FakeRuntimeFactory { Runtime = runtime },
            router);
        await coordinator.LoginAsync(ServerBaseUri.AbsoluteUri, "shell-user", "secret");
        await WaitUntilAsync(() => coordinator.ConversationList.Status ==
            LocalCacheOperationStatus.Ready);

        coordinator.SelectConversation(conversationId);
        await WaitUntilAsync(() => coordinator.Snapshot.Phase ==
            ClientAccountShellPhase.SignedOut);

        Assert.Equal(1, runtime.LogoutCount);
        Assert.Equal(1, runtime.DisposeCount);
        Assert.Equal(
            PersistentClientAuthenticationStatus.AuthenticationFailed,
            coordinator.Snapshot.AuthenticationStatus);
        Assert.Equal(ClientMessageListStatus.None, coordinator.MessageList.Status);
    }

    [Fact]
    public async Task AcknowledgeMessageSnapshotApplied_WhenCacheIsTransient_DoesNotTightLoop()
    {
        var session = CreateSession();
        var conversationId = Guid.NewGuid();
        var message = CreateMessage(10, conversationId);
        var attempts = 0;
        var runtime = new FakeRuntime(session)
        {
            ConversationListOutcome = CreateConversationListOutcome(conversationId, 1),
            MessagePageReadAction = (id, _, _, _) => Task.FromResult(
                new LocalMessagePageReadOutcome(
                    LocalCacheOperationStatus.Ready,
                    id,
                    [message],
                    NextBeforeMessageId: null,
                    HasMoreBefore: false)),
            MarkRenderedAction = (_, _, _) =>
            {
                Interlocked.Increment(ref attempts);
                return Task.FromResult(LocalCacheOperationStatus.TransientFailure);
            },
        };
        using var router = CreateRouter();
        await using var coordinator = CreateCoordinator(
            Authenticated(session),
            new FakeRuntimeFactory { Runtime = runtime },
            router);
        await coordinator.LoginAsync(ServerBaseUri.AbsoluteUri, "shell-user", "secret");
        await WaitUntilAsync(() => coordinator.ConversationList.Status ==
            LocalCacheOperationStatus.Ready);
        coordinator.UpdateActivity(new ClientActivitySnapshot(
            true,
            false,
            true,
            OpenConversationId: null));
        coordinator.SelectConversation(conversationId);
        await WaitUntilAsync(() => coordinator.MessageList.Status ==
            ClientMessageListStatus.Ready && !coordinator.MessageList.IsLoading);
        var applied = coordinator.MessageList;

        coordinator.AcknowledgeMessageSnapshotApplied(
            conversationId,
            applied.Revision,
            observedThroughMessageId: 10,
            isAtLatestRegion: true);
        await WaitUntilAsync(() => Volatile.Read(ref attempts) == 1);
        await Task.Delay(100);

        Assert.Equal(1, Volatile.Read(ref attempts));
    }

    [Fact]
    public async Task MessageRefresh_WhenLocalScopeFails_HidesMessagesAndClearsActivity()
    {
        var session = CreateSession();
        var conversationId = Guid.NewGuid();
        var message = CreateMessage(10, conversationId);
        var readCount = 0;
        var runtime = new FakeRuntime(session)
        {
            ConversationListOutcome = CreateConversationListOutcome(conversationId, 1),
            MessagePageReadAction = (id, _, _, _) => Task.FromResult(
                Interlocked.Increment(ref readCount) == 1
                    ? new LocalMessagePageReadOutcome(
                        LocalCacheOperationStatus.Ready,
                        id,
                        [message],
                        NextBeforeMessageId: null,
                        HasMoreBefore: false)
                    : new LocalMessagePageReadOutcome(
                        LocalCacheOperationStatus.FatalScope,
                        id,
                        [message],
                        NextBeforeMessageId: null,
                        HasMoreBefore: false)),
        };
        using var router = CreateRouter();
        await using var coordinator = CreateCoordinator(
            Authenticated(session),
            new FakeRuntimeFactory { Runtime = runtime },
            router);
        await coordinator.LoginAsync(ServerBaseUri.AbsoluteUri, "shell-user", "secret");
        await WaitUntilAsync(() => coordinator.ConversationList.Status ==
            LocalCacheOperationStatus.Ready);
        coordinator.UpdateActivity(new ClientActivitySnapshot(
            true,
            false,
            true,
            OpenConversationId: null));
        coordinator.SelectConversation(conversationId);
        await WaitUntilAsync(() => coordinator.MessageList.Status ==
            ClientMessageListStatus.Ready && !coordinator.MessageList.IsLoading);
        var applied = coordinator.MessageList;
        coordinator.AcknowledgeMessageSnapshotApplied(
            conversationId,
            applied.Revision,
            observedThroughMessageId: 10,
            isAtLatestRegion: true);
        await WaitUntilAsync(() => runtime.LastActivity?.OpenConversationId == conversationId);

        runtime.RaiseConversationStateChanged(1);
        await WaitUntilAsync(() => coordinator.MessageList.Status ==
            ClientMessageListStatus.FatalScope);

        Assert.Empty(coordinator.MessageList.Messages);
        Assert.False(coordinator.MessageList.HasMoreBefore);
        Assert.False(coordinator.MessageList.HasMoreAfter);
        Assert.Null(coordinator.MessageList.TargetMessageId);
        Assert.Null(runtime.LastActivity!.OpenConversationId);
    }

    [Fact]
    public async Task MessageViewportChanged_WhenNewerSnapshotIsNotApplied_DoesNotAdvanceRead()
    {
        var session = CreateSession();
        var conversationId = Guid.NewGuid();
        var firstMessage = CreateMessage(10, conversationId);
        var secondMessage = CreateMessage(11, conversationId);
        var includeSecondMessage = 0;
        var markedMessageIds = new ConcurrentQueue<long>();
        var runtime = new FakeRuntime(session)
        {
            ConversationListOutcome = CreateConversationListOutcome(conversationId, 1),
            MessagePageReadAction = (id, _, _, _) => Task.FromResult(
                new LocalMessagePageReadOutcome(
                    LocalCacheOperationStatus.Ready,
                    id,
                    Volatile.Read(ref includeSecondMessage) == 0
                        ? [firstMessage]
                        : [firstMessage, secondMessage],
                    NextBeforeMessageId: null,
                    HasMoreBefore: false)),
            MarkRenderedAction = (_, messageId, _) =>
            {
                markedMessageIds.Enqueue(messageId);
                return Task.FromResult(LocalCacheOperationStatus.Ready);
            },
        };
        using var router = CreateRouter();
        await using var coordinator = CreateCoordinator(
            Authenticated(session),
            new FakeRuntimeFactory { Runtime = runtime },
            router);
        await coordinator.LoginAsync(ServerBaseUri.AbsoluteUri, "shell-user", "secret");
        await WaitUntilAsync(() => coordinator.ConversationList.Status ==
            LocalCacheOperationStatus.Ready);
        coordinator.UpdateActivity(new ClientActivitySnapshot(
            true,
            false,
            true,
            OpenConversationId: null));
        coordinator.SelectConversation(conversationId);
        await WaitUntilAsync(() => coordinator.MessageList.Status ==
            ClientMessageListStatus.Ready && !coordinator.MessageList.IsLoading);
        var firstApplied = coordinator.MessageList;
        coordinator.AcknowledgeMessageSnapshotApplied(
            conversationId,
            firstApplied.Revision,
            observedThroughMessageId: 10,
            isAtLatestRegion: true);
        await WaitUntilAsync(() => markedMessageIds.Count == 1);

        Volatile.Write(ref includeSecondMessage, 1);
        runtime.RaiseConversationStateChanged(2);
        await WaitUntilAsync(() => coordinator.MessageList.LatestMessageId == 11);
        var notYetApplied = coordinator.MessageList;
        coordinator.AcknowledgeMessageViewportChanged(
            conversationId,
            notYetApplied.Revision,
            observedThroughMessageId: 11,
            isAtLatestRegion: true);
        await Task.Delay(50);

        Assert.Equal([10L], markedMessageIds);
        coordinator.AcknowledgeMessageSnapshotApplied(
            conversationId,
            notYetApplied.Revision,
            observedThroughMessageId: 11,
            isAtLatestRegion: true);
        await WaitUntilAsync(() => markedMessageIds.Count == 2);
        Assert.Equal([10L, 11L], markedMessageIds);
    }

    [Fact]
    public async Task SelectConversation_WhenOldReadCompletesLate_DoesNotReplaceNewSelection()
    {
        var session = CreateSession();
        var firstConversationId = Guid.NewGuid();
        var secondConversationId = Guid.NewGuid();
        var firstEntered = NewSignal();
        var releaseFirst = NewSignal();
        var runtime = new FakeRuntime(session)
        {
            ConversationListOutcome = CreateConversationListOutcome(
                firstConversationId,
                secondConversationId),
            MessagePageReadAction = async (id, _, _, _) =>
            {
                if (id == firstConversationId)
                {
                    firstEntered.TrySetResult();
                    await releaseFirst.Task;
                }

                return new LocalMessagePageReadOutcome(
                    LocalCacheOperationStatus.Ready,
                    id,
                    [CreateMessage(id == firstConversationId ? 1 : 2, id)],
                    NextBeforeMessageId: null,
                    HasMoreBefore: false);
            },
        };
        using var router = CreateRouter();
        await using var coordinator = CreateCoordinator(
            Authenticated(session),
            new FakeRuntimeFactory { Runtime = runtime },
            router);
        await coordinator.LoginAsync(ServerBaseUri.AbsoluteUri, "shell-user", "secret");
        await WaitUntilAsync(() => coordinator.ConversationList.Status ==
            LocalCacheOperationStatus.Ready);

        coordinator.SelectConversation(firstConversationId);
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        coordinator.SelectConversation(secondConversationId);
        await WaitUntilAsync(() =>
            coordinator.MessageList.ConversationId == secondConversationId &&
            coordinator.MessageList.Status == ClientMessageListStatus.Ready &&
            coordinator.MessageList.Messages.Count == 1);
        releaseFirst.TrySetResult();
        await Task.Delay(50);

        Assert.Equal(secondConversationId, coordinator.MessageList.ConversationId);
        Assert.Equal(2, Assert.Single(coordinator.MessageList.Messages).ServerMessageId);
    }

    [Fact]
    public async Task LoadOlderMessagesAsync_WhenInvokedTwice_SharesOneRemotePageAndPrepends()
    {
        var session = CreateSession();
        var conversationId = Guid.NewGuid();
        var latest = Enumerable.Range(51, 50)
            .Select(id => CreateMessage(id, conversationId))
            .ToArray();
        var older = Enumerable.Range(1, 50)
            .Select(id => CreateMessage(id, conversationId))
            .ToArray();
        var olderEntered = NewSignal();
        var releaseOlder = NewSignal();
        var olderRequestCount = 0;
        var runtime = new FakeRuntime(session)
        {
            ConversationListOutcome = CreateConversationListOutcome(conversationId, 0),
            MessagePageReadAction = (id, before, _, _) => Task.FromResult(
                new LocalMessagePageReadOutcome(
                    LocalCacheOperationStatus.Ready,
                    id,
                    before.HasValue ? older : latest,
                    before.HasValue ? null : 51,
                    HasMoreBefore: !before.HasValue)),
            MessageHistoryLoadAction = async (_, before, _, _) =>
            {
                if (!before.HasValue)
                {
                    return new ClientMessageHistoryPageOutcome(
                        ClientMessageLoadStatus.Completed,
                        latest,
                        NextBeforeMessageId: 51,
                        HasMore: true);
                }

                Interlocked.Increment(ref olderRequestCount);
                olderEntered.TrySetResult();
                await releaseOlder.Task;
                return new ClientMessageHistoryPageOutcome(
                    ClientMessageLoadStatus.Completed,
                    older,
                    NextBeforeMessageId: null,
                    HasMore: false);
            },
        };
        using var router = CreateRouter();
        await using var coordinator = CreateCoordinator(
            Authenticated(session),
            new FakeRuntimeFactory { Runtime = runtime },
            router);
        await coordinator.LoginAsync(ServerBaseUri.AbsoluteUri, "shell-user", "secret");
        await WaitUntilAsync(() => coordinator.ConversationList.Status ==
            LocalCacheOperationStatus.Ready);
        coordinator.SelectConversation(conversationId);
        await WaitUntilAsync(() => coordinator.MessageList.CanLoadOlder);

        var first = coordinator.LoadOlderMessagesAsync();
        var second = coordinator.LoadOlderMessagesAsync();
        await olderEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, Volatile.Read(ref olderRequestCount));
        releaseOlder.TrySetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(100, coordinator.MessageList.Messages.Count);
        Assert.Equal(1, coordinator.MessageList.Messages[0].ServerMessageId);
        Assert.Equal(100, coordinator.MessageList.Messages[^1].ServerMessageId);
        Assert.False(coordinator.MessageList.HasMoreBefore);
    }

    [Fact]
    public async Task SelectConversation_WhenUnreadIsRenderedAndStateRefreshes_KeepsFrozenDivider()
    {
        var session = CreateSession();
        var conversationId = Guid.NewGuid();
        var message = CreateMessage(10, conversationId);
        var rendered = NewSignal();
        var pageReads = 0;
        var runtime = new FakeRuntime(session)
        {
            ConversationListOutcome = CreateConversationListOutcome(conversationId, 1),
            MessagePageReadAction = (id, _, _, _) =>
            {
                var firstRead = Interlocked.Increment(ref pageReads) == 1;
                return Task.FromResult(new LocalMessagePageReadOutcome(
                    LocalCacheOperationStatus.Ready,
                    id,
                    [message],
                    NextBeforeMessageId: null,
                    HasMoreBefore: false,
                    PendingMessages: Array.Empty<LocalPendingMessage>(),
                    LastReadMessageId: firstRead ? 9 : 10,
                    UnreadCount: firstRead ? 1 : 0));
            },
            MessageHistoryLoadAction = (_, _, _, _) => Task.FromResult(
                new ClientMessageHistoryPageOutcome(
                    ClientMessageLoadStatus.Completed,
                    [message],
                    NextBeforeMessageId: null,
                    HasMore: false)),
            MarkRenderedAction = (_, _, _) =>
            {
                rendered.TrySetResult();
                return Task.FromResult(LocalCacheOperationStatus.Ready);
            },
        };
        using var router = CreateRouter();
        await using var coordinator = CreateCoordinator(
            Authenticated(session),
            new FakeRuntimeFactory { Runtime = runtime },
            router);
        await coordinator.LoginAsync(ServerBaseUri.AbsoluteUri, "shell-user", "secret");
        await WaitUntilAsync(() => coordinator.ConversationList.Status ==
            LocalCacheOperationStatus.Ready);
        coordinator.UpdateActivity(new ClientActivitySnapshot(
            true,
            false,
            true,
            OpenConversationId: null));

        coordinator.SelectConversation(conversationId);
        await WaitUntilAsync(() => coordinator.MessageList.Status ==
            ClientMessageListStatus.Ready && !coordinator.MessageList.IsLoading);
        var initial = coordinator.MessageList;
        Assert.True(Assert.Single(initial.Messages).ShowNewMessageSeparator);

        coordinator.AcknowledgeMessageSnapshotApplied(
            conversationId,
            initial.Revision,
            observedThroughMessageId: 10,
            isAtLatestRegion: true);
        await rendered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        runtime.RaiseConversationStateChanged(2);
        await WaitUntilAsync(() => coordinator.MessageList.Revision > initial.Revision);

        Assert.True(Assert.Single(coordinator.MessageList.Messages)
            .ShowNewMessageSeparator);
        Assert.True(pageReads >= 2);
    }

    [Fact]
    public async Task LoadOlderMessagesAsync_UntilFrozenReadBoundaryIsCovered_DefersThenResolvesDivider()
    {
        var session = CreateSession();
        var conversationId = Guid.NewGuid();
        var latest = Enumerable.Range(51, 50)
            .Select(id => CreateMessage(id, conversationId))
            .ToArray();
        var older = Enumerable.Range(1, 50)
            .Select(id => CreateMessage(id, conversationId))
            .ToArray();
        var runtime = new FakeRuntime(session)
        {
            ConversationListOutcome = CreateConversationListOutcome(conversationId, 50),
            MessagePageReadAction = (id, before, _, _) => Task.FromResult(
                new LocalMessagePageReadOutcome(
                    LocalCacheOperationStatus.Ready,
                    id,
                    before.HasValue ? older : latest,
                    NextBeforeMessageId: before.HasValue ? null : 51,
                    HasMoreBefore: !before.HasValue,
                    PendingMessages: Array.Empty<LocalPendingMessage>(),
                    LastReadMessageId: 50,
                    UnreadCount: 50)),
            MessageHistoryLoadAction = (_, before, _, _) => Task.FromResult(
                new ClientMessageHistoryPageOutcome(
                    ClientMessageLoadStatus.Completed,
                    before.HasValue ? older : latest,
                    NextBeforeMessageId: before.HasValue ? null : 51,
                    HasMore: !before.HasValue)),
        };
        using var router = CreateRouter();
        await using var coordinator = CreateCoordinator(
            Authenticated(session),
            new FakeRuntimeFactory { Runtime = runtime },
            router);
        await coordinator.LoginAsync(ServerBaseUri.AbsoluteUri, "shell-user", "secret");
        await WaitUntilAsync(() => coordinator.ConversationList.Status ==
            LocalCacheOperationStatus.Ready);

        coordinator.SelectConversation(conversationId);
        await WaitUntilAsync(() => coordinator.MessageList.CanLoadOlder);
        Assert.DoesNotContain(
            coordinator.MessageList.Messages,
            item => item.ShowNewMessageSeparator);

        await coordinator.LoadOlderMessagesAsync();

        var separator = Assert.Single(
            coordinator.MessageList.Messages,
            item => item.ShowNewMessageSeparator);
        Assert.Equal(51, separator.ServerMessageId);
        Assert.False(coordinator.MessageList.HasMoreBefore);
    }

    [Fact]
    public async Task SelectConversation_WhenAroundPageCrossesFrozenBoundary_ResolvesExactDivider()
    {
        var session = CreateSession();
        var conversationId = Guid.NewGuid();
        var targetMessageId = 96L;
        var latest = CreateMessage(100, conversationId);
        var around = new[]
        {
            CreateMessage(94, conversationId),
            CreateMessage(96, conversationId),
            CreateMessage(97, conversationId),
        };
        var runtime = new FakeRuntime(session)
        {
            ConversationListOutcome = CreateConversationListOutcome(conversationId, 1),
            MessagePageReadAction = (id, _, _, _) => Task.FromResult(
                new LocalMessagePageReadOutcome(
                    LocalCacheOperationStatus.Ready,
                    id,
                    [latest],
                    NextBeforeMessageId: null,
                    HasMoreBefore: false,
                    PendingMessages: Array.Empty<LocalPendingMessage>(),
                    LastReadMessageId: 95,
                    UnreadCount: 1)),
            MessageAroundLoadAction = (_, target, _, _, _) => Task.FromResult(
                new ClientMessageAroundOutcome(
                    ClientMessageLoadStatus.Completed,
                    around,
                    target,
                    HasMoreBefore: true,
                    HasMoreAfter: true)),
        };
        using var router = CreateRouter();
        await using var coordinator = CreateCoordinator(
            Authenticated(session),
            new FakeRuntimeFactory { Runtime = runtime },
            router);
        await coordinator.LoginAsync(ServerBaseUri.AbsoluteUri, "shell-user", "secret");
        await WaitUntilAsync(() => coordinator.ConversationList.Status ==
            LocalCacheOperationStatus.Ready);

        coordinator.SelectConversation(conversationId, targetMessageId);
        await WaitUntilAsync(() => coordinator.MessageList.Status ==
            ClientMessageListStatus.Ready && !coordinator.MessageList.IsLoading);

        var separator = Assert.Single(
            coordinator.MessageList.Messages,
            item => item.ShowNewMessageSeparator);
        Assert.Equal(targetMessageId, separator.ServerMessageId);
        Assert.True(coordinator.MessageList.HasMoreBefore);
        Assert.True(coordinator.MessageList.HasMoreAfter);
    }

    [Fact]
    public async Task SelectConversation_WhenAroundPageDoesNotReachFrozenBoundary_HidesApproximation()
    {
        var session = CreateSession();
        var conversationId = Guid.NewGuid();
        var targetMessageId = 99L;
        var runtime = new FakeRuntime(session)
        {
            ConversationListOutcome = CreateConversationListOutcome(conversationId, 1),
            MessagePageReadAction = (id, _, _, _) => Task.FromResult(
                new LocalMessagePageReadOutcome(
                    LocalCacheOperationStatus.Ready,
                    id,
                    [CreateMessage(100, id)],
                    NextBeforeMessageId: null,
                    HasMoreBefore: false,
                    PendingMessages: Array.Empty<LocalPendingMessage>(),
                    LastReadMessageId: 95,
                    UnreadCount: 1)),
            MessageAroundLoadAction = (_, target, _, _, _) => Task.FromResult(
                new ClientMessageAroundOutcome(
                    ClientMessageLoadStatus.Completed,
                    [CreateMessage(98, conversationId), CreateMessage(99, conversationId)],
                    target,
                    HasMoreBefore: true,
                    HasMoreAfter: true)),
        };
        using var router = CreateRouter();
        await using var coordinator = CreateCoordinator(
            Authenticated(session),
            new FakeRuntimeFactory { Runtime = runtime },
            router);
        await coordinator.LoginAsync(ServerBaseUri.AbsoluteUri, "shell-user", "secret");
        await WaitUntilAsync(() => coordinator.ConversationList.Status ==
            LocalCacheOperationStatus.Ready);

        coordinator.SelectConversation(conversationId, targetMessageId);
        await WaitUntilAsync(() => coordinator.MessageList.Status ==
            ClientMessageListStatus.Ready && !coordinator.MessageList.IsLoading);

        Assert.DoesNotContain(
            coordinator.MessageList.Messages,
            item => item.ShowNewMessageSeparator);
        Assert.True(coordinator.MessageList.HasMoreBefore);
        Assert.True(coordinator.MessageList.HasMoreAfter);
    }

    [Fact]
    public async Task SelectConversation_WhenReplyTargetIsMissing_LoadsAroundAndPublishesTarget()
    {
        var session = CreateSession();
        var conversationId = Guid.NewGuid();
        long? requestedTargetMessageId = null;
        var runtime = new FakeRuntime(session)
        {
            ConversationListOutcome = CreateConversationListOutcome(conversationId, 0),
            MessagePageReadAction = (id, _, _, _) => Task.FromResult(
                new LocalMessagePageReadOutcome(
                    LocalCacheOperationStatus.Ready,
                    id,
                    [CreateMessage(10, id)],
                    NextBeforeMessageId: null,
                    HasMoreBefore: false)),
            MessageAroundLoadAction = (id, targetMessageId, _, _, _) =>
            {
                requestedTargetMessageId = targetMessageId;
                return Task.FromResult(new ClientMessageAroundOutcome(
                    ClientMessageLoadStatus.Completed,
                    [CreateMessage(98, id), CreateMessage(targetMessageId, id)],
                    targetMessageId,
                    HasMoreBefore: true,
                    HasMoreAfter: true));
            },
        };
        using var router = CreateRouter();
        await using var coordinator = CreateCoordinator(
            Authenticated(session),
            new FakeRuntimeFactory { Runtime = runtime },
            router);
        await coordinator.LoginAsync(ServerBaseUri.AbsoluteUri, "shell-user", "secret");
        await WaitUntilAsync(() => coordinator.ConversationList.Status ==
            LocalCacheOperationStatus.Ready);

        coordinator.SelectConversation(conversationId, targetMessageId: 99);
        await WaitUntilAsync(() => coordinator.MessageList.Status ==
            ClientMessageListStatus.Ready &&
            !coordinator.MessageList.IsLoading &&
            coordinator.MessageList.TargetMessageId == 99);

        Assert.Equal(99, requestedTargetMessageId);
        Assert.Contains(
            coordinator.MessageList.Messages,
            item => item.ServerMessageId == 99);
        Assert.True(coordinator.MessageList.HasMoreBefore);
        Assert.True(coordinator.MessageList.HasMoreAfter);
    }

    [Fact]
    public async Task SearchMentionCandidatesAsync_WhenSelectionIsReady_ForwardsExactContext()
    {
        var session = CreateSession();
        var conversationId = Guid.NewGuid();
        var candidate = new MentionCandidateDto(Guid.NewGuid(), "alpha", "Alpha");
        Guid? searchedConversationId = null;
        string? searchedQuery = null;
        int? searchedLimit = null;
        var runtime = new FakeRuntime(session)
        {
            ConversationListOutcome = CreateConversationListOutcome(conversationId, 0),
            MessagePageReadAction = (id, _, _, _) => Task.FromResult(
                new LocalMessagePageReadOutcome(
                    LocalCacheOperationStatus.Ready,
                    id,
                    Array.Empty<MessageDto>(),
                    NextBeforeMessageId: null,
                    HasMoreBefore: false)),
            MentionCandidateSearchAction = (id, query, limit, _) =>
            {
                searchedConversationId = id;
                searchedQuery = query;
                searchedLimit = limit;
                return Task.FromResult(new ClientMentionCandidateOutcome(
                    ClientMentionCandidateStatus.Completed,
                    [candidate],
                    HasMore: false));
            },
        };
        using var router = CreateRouter();
        await using var coordinator = CreateCoordinator(
            Authenticated(session),
            new FakeRuntimeFactory { Runtime = runtime },
            router);
        await coordinator.LoginAsync(ServerBaseUri.AbsoluteUri, "shell-user", "secret");
        await WaitUntilAsync(() => coordinator.ConversationList.Status ==
            LocalCacheOperationStatus.Ready);
        coordinator.SelectConversation(conversationId);
        await WaitUntilAsync(() => coordinator.MessageList.Status ==
            ClientMessageListStatus.Ready);

        var outcome = await coordinator.SearchMentionCandidatesAsync("al", limit: 7);

        Assert.Equal(ClientMentionCandidateStatus.Completed, outcome.Status);
        Assert.Equal([candidate], outcome.Candidates);
        Assert.Equal(conversationId, searchedConversationId);
        Assert.Equal("al", searchedQuery);
        Assert.Equal(7, searchedLimit);
    }

    [Fact]
    public async Task SearchMentionCandidatesAsync_WhenNoReadySelection_DoesNotInvokeRuntime()
    {
        var session = CreateSession();
        var searchCount = 0;
        var runtime = new FakeRuntime(session)
        {
            ConversationListOutcome = CreateConversationListOutcome(Guid.NewGuid(), 0),
            MentionCandidateSearchAction = (_, _, _, _) =>
            {
                Interlocked.Increment(ref searchCount);
                return Task.FromResult(ClientMentionCandidateOutcome.Failure(
                    ClientMentionCandidateStatus.RemoteFailure));
            },
        };
        using var router = CreateRouter();
        await using var coordinator = CreateCoordinator(
            Authenticated(session),
            new FakeRuntimeFactory { Runtime = runtime },
            router);
        await coordinator.LoginAsync(ServerBaseUri.AbsoluteUri, "shell-user", "secret");
        await WaitUntilAsync(() => coordinator.ConversationList.Status ==
            LocalCacheOperationStatus.Ready);

        var outcome = await coordinator.SearchMentionCandidatesAsync("al");

        Assert.Equal(ClientMentionCandidateStatus.Unavailable, outcome.Status);
        Assert.Equal(0, Volatile.Read(ref searchCount));
    }

    [Fact]
    public async Task SearchMentionCandidatesAsync_WhenSelectionChanges_DiscardsLateCandidates()
    {
        var session = CreateSession();
        var firstConversationId = Guid.NewGuid();
        var secondConversationId = Guid.NewGuid();
        var searchEntered = NewSignal();
        var releaseSearch = NewSignal();
        CancellationToken searchToken = default;
        var runtime = new FakeRuntime(session)
        {
            ConversationListOutcome = CreateConversationListOutcome(
                firstConversationId,
                secondConversationId),
            MessagePageReadAction = (id, _, _, _) => Task.FromResult(
                new LocalMessagePageReadOutcome(
                    LocalCacheOperationStatus.Ready,
                    id,
                    Array.Empty<MessageDto>(),
                    NextBeforeMessageId: null,
                    HasMoreBefore: false)),
            MentionCandidateSearchAction = async (_, _, _, token) =>
            {
                searchToken = token;
                searchEntered.TrySetResult();
                await releaseSearch.Task;
                return new ClientMentionCandidateOutcome(
                    ClientMentionCandidateStatus.Completed,
                    [new MentionCandidateDto(Guid.NewGuid(), "alpha", "Alpha")],
                    HasMore: false);
            },
        };
        using var router = CreateRouter();
        await using var coordinator = CreateCoordinator(
            Authenticated(session),
            new FakeRuntimeFactory { Runtime = runtime },
            router);
        await coordinator.LoginAsync(ServerBaseUri.AbsoluteUri, "shell-user", "secret");
        await WaitUntilAsync(() => coordinator.ConversationList.Status ==
            LocalCacheOperationStatus.Ready);
        coordinator.SelectConversation(firstConversationId);
        await WaitUntilAsync(() => coordinator.MessageList.Status ==
            ClientMessageListStatus.Ready &&
            coordinator.MessageList.ConversationId == firstConversationId);

        var search = coordinator.SearchMentionCandidatesAsync("al");
        await searchEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        coordinator.SelectConversation(secondConversationId);
        await WaitUntilAsync(() => coordinator.MessageList.Status ==
            ClientMessageListStatus.Ready &&
            coordinator.MessageList.ConversationId == secondConversationId);
        Assert.True(searchToken.IsCancellationRequested);
        releaseSearch.TrySetResult();
        var outcome = await search;

        Assert.Equal(ClientMentionCandidateStatus.Stale, outcome.Status);
        Assert.Empty(outcome.Candidates);
    }

    [Fact]
    public async Task SearchMentionCandidatesAsync_WhenAuthenticationExpires_EndsSession()
    {
        var session = CreateSession();
        var conversationId = Guid.NewGuid();
        var runtime = new FakeRuntime(session)
        {
            ConversationListOutcome = CreateConversationListOutcome(conversationId, 0),
            MessagePageReadAction = (id, _, _, _) => Task.FromResult(
                new LocalMessagePageReadOutcome(
                    LocalCacheOperationStatus.Ready,
                    id,
                    Array.Empty<MessageDto>(),
                    NextBeforeMessageId: null,
                    HasMoreBefore: false)),
            MentionCandidateSearchAction = (_, _, _, _) => Task.FromResult(
                ClientMentionCandidateOutcome.Failure(
                    ClientMentionCandidateStatus.AuthenticationRequired)),
        };
        using var router = CreateRouter();
        await using var coordinator = CreateCoordinator(
            Authenticated(session),
            new FakeRuntimeFactory { Runtime = runtime },
            router);
        await coordinator.LoginAsync(ServerBaseUri.AbsoluteUri, "shell-user", "secret");
        await WaitUntilAsync(() => coordinator.ConversationList.Status ==
            LocalCacheOperationStatus.Ready);
        coordinator.SelectConversation(conversationId);
        await WaitUntilAsync(() => coordinator.MessageList.Status ==
            ClientMessageListStatus.Ready);

        var outcome = await coordinator.SearchMentionCandidatesAsync("al");

        Assert.Equal(ClientMentionCandidateStatus.AuthenticationRequired, outcome.Status);
        Assert.Equal(ClientAccountShellPhase.SignedOut, coordinator.Snapshot.Phase);
        Assert.False(coordinator.Snapshot.HasActiveAccount);
        Assert.Equal(1, runtime.DisposeCount);
    }

    [Fact]
    public async Task DownloadAttachmentAsync_WhenSelectionIsReady_ForwardsContextAndProgress()
    {
        var session = CreateSession();
        var conversationId = Guid.NewGuid();
        var attachmentId = Guid.NewGuid();
        var attachment = new AttachmentDto(
            attachmentId,
            "download.bin",
            "application/octet-stream",
            3,
            $"/api/attachments/{attachmentId:D}/download",
            ThumbnailUrl: null);
        var message = CreateMessage(10, conversationId) with
        {
            Type = MessageType.File,
            Content = null,
            Attachments = [attachment],
        };
        var progress = new Progress<ClientAttachmentDownloadProgress>(_ => { });
        Guid? capturedConversationId = null;
        Guid? capturedAttachmentId = null;
        IProgress<ClientAttachmentDownloadProgress>? capturedProgress = null;
        var runtime = new FakeRuntime(session)
        {
            ConversationListOutcome = CreateConversationListOutcome(conversationId, 0),
            MessagePageReadAction = (id, _, _, _) => Task.FromResult(
                new LocalMessagePageReadOutcome(
                    LocalCacheOperationStatus.Ready,
                    id,
                    [message],
                    NextBeforeMessageId: null,
                    HasMoreBefore: false)),
            AttachmentDownloadAction = (conversation, attachment, _, reportedProgress) =>
            {
                capturedConversationId = conversation;
                capturedAttachmentId = attachment;
                capturedProgress = reportedProgress;
                return Task.FromResult(new ClientAttachmentDownloadOutcome(
                    ClientAttachmentDownloadStatus.Completed,
                    "managed.cache"));
            },
        };
        using var router = CreateRouter();
        await using var coordinator = CreateCoordinator(
            Authenticated(session),
            new FakeRuntimeFactory { Runtime = runtime },
            router);
        await coordinator.LoginAsync(ServerBaseUri.AbsoluteUri, "shell-user", "secret");
        await WaitUntilAsync(() => coordinator.ConversationList.Status ==
            LocalCacheOperationStatus.Ready);
        coordinator.SelectConversation(conversationId);
        await WaitUntilAsync(() => coordinator.MessageList.Status ==
            ClientMessageListStatus.Ready);

        var outcome = await coordinator.DownloadAttachmentAsync(
            attachmentId,
            progress: progress);

        Assert.Equal(ClientAttachmentDownloadStatus.Completed, outcome.Status);
        Assert.Equal(conversationId, capturedConversationId);
        Assert.Equal(attachmentId, capturedAttachmentId);
        Assert.Same(progress, capturedProgress);
    }

    [Fact]
    public async Task DownloadAttachmentAsync_WhenAttachmentIsNotInSelection_DoesNotCallRuntime()
    {
        var session = CreateSession();
        var conversationId = Guid.NewGuid();
        var calls = 0;
        var runtime = new FakeRuntime(session)
        {
            ConversationListOutcome = CreateConversationListOutcome(conversationId, 0),
            MessagePageReadAction = (id, _, _, _) => Task.FromResult(
                new LocalMessagePageReadOutcome(
                    LocalCacheOperationStatus.Ready,
                    id,
                    Array.Empty<MessageDto>(),
                    NextBeforeMessageId: null,
                    HasMoreBefore: false)),
            AttachmentDownloadAction = (_, _, _, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(new ClientAttachmentDownloadOutcome(
                    ClientAttachmentDownloadStatus.Completed,
                    "managed.cache"));
            },
        };
        using var router = CreateRouter();
        await using var coordinator = CreateCoordinator(
            Authenticated(session),
            new FakeRuntimeFactory { Runtime = runtime },
            router);
        await coordinator.LoginAsync(ServerBaseUri.AbsoluteUri, "shell-user", "secret");
        await WaitUntilAsync(() => coordinator.ConversationList.Status ==
            LocalCacheOperationStatus.Ready);
        coordinator.SelectConversation(conversationId);
        await WaitUntilAsync(() => coordinator.MessageList.Status ==
            ClientMessageListStatus.Ready);

        var outcome = await coordinator.DownloadAttachmentAsync(Guid.NewGuid());

        Assert.Equal(ClientAttachmentDownloadStatus.AttachmentUnavailable, outcome.Status);
        Assert.Equal(0, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task DownloadAttachmentAsync_WhenSelectionBecomesNonReady_DiscardsLateOutcome()
    {
        var session = CreateSession();
        var conversationId = Guid.NewGuid();
        var attachmentId = Guid.NewGuid();
        var attachment = new AttachmentDto(
            attachmentId,
            "late-download.bin",
            "application/octet-stream",
            3,
            $"/api/attachments/{attachmentId:D}/download",
            ThumbnailUrl: null);
        var message = CreateMessage(10, conversationId) with
        {
            Type = MessageType.File,
            Content = null,
            Attachments = [attachment],
        };
        var returnFailure = 0;
        var downloadStarted = NewSignal();
        var downloadRelease = NewSignal();
        var runtime = new FakeRuntime(session)
        {
            ConversationListOutcome = CreateConversationListOutcome(conversationId, 0),
            MessagePageReadAction = (id, _, _, _) => Task.FromResult(
                Volatile.Read(ref returnFailure) == 0
                    ? new LocalMessagePageReadOutcome(
                        LocalCacheOperationStatus.Ready,
                        id,
                        [message],
                        NextBeforeMessageId: null,
                        HasMoreBefore: false)
                    : LocalMessagePageReadOutcome.Failure(
                        LocalCacheOperationStatus.FatalScope,
                        id)),
            AttachmentDownloadAction = async (_, _, _, _) =>
            {
                downloadStarted.TrySetResult();
                await downloadRelease.Task;
                return new ClientAttachmentDownloadOutcome(
                    ClientAttachmentDownloadStatus.Completed,
                    "managed.cache");
            },
        };
        using var router = CreateRouter();
        await using var coordinator = CreateCoordinator(
            Authenticated(session),
            new FakeRuntimeFactory { Runtime = runtime },
            router);
        await coordinator.LoginAsync(ServerBaseUri.AbsoluteUri, "shell-user", "secret");
        await WaitUntilAsync(() => coordinator.ConversationList.Status ==
            LocalCacheOperationStatus.Ready);
        coordinator.SelectConversation(conversationId);
        await WaitUntilAsync(() => coordinator.MessageList.Status ==
            ClientMessageListStatus.Ready);

        var download = coordinator.DownloadAttachmentAsync(attachmentId);
        await downloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Interlocked.Exchange(ref returnFailure, 1);
        runtime.RaiseConversationStateChanged(2);
        await WaitUntilAsync(() => coordinator.MessageList.Status ==
            ClientMessageListStatus.FatalScope);

        downloadRelease.TrySetResult();
        var outcome = await download;

        Assert.Equal(ClientAttachmentDownloadStatus.Canceled, outcome.Status);
    }

    [Fact]
    public async Task RevealAttachmentInFolderAsync_WhenAttachmentIsInExactSelection_ForwardsIdentity()
    {
        var session = CreateSession();
        var conversationId = Guid.NewGuid();
        var attachmentId = Guid.NewGuid();
        var attachment = new AttachmentDto(
            attachmentId,
            "revealed.bin",
            "application/octet-stream",
            3,
            $"/api/attachments/{attachmentId:D}/download",
            ThumbnailUrl: null);
        var message = CreateMessage(10, conversationId) with
        {
            Type = MessageType.File,
            Content = null,
            Attachments = [attachment],
        };
        Guid? capturedConversationId = null;
        Guid? capturedAttachmentId = null;
        var runtime = new FakeRuntime(session)
        {
            ConversationListOutcome = CreateConversationListOutcome(conversationId, 0),
            MessagePageReadAction = (id, _, _, _) => Task.FromResult(
                new LocalMessagePageReadOutcome(
                    LocalCacheOperationStatus.Ready,
                    id,
                    [message],
                    NextBeforeMessageId: null,
                    HasMoreBefore: false)
                {
                    DownloadedAttachmentIds = new HashSet<Guid> { attachmentId },
                }),
            AttachmentRevealAction = (conversation, requestedAttachment, commit, _) =>
            {
                capturedConversationId = conversation;
                capturedAttachmentId = requestedAttachment;
                return Task.FromResult(ClientAttachmentRevealOutcome.FromStatus(
                    commit()));
            },
        };
        using var router = CreateRouter();
        await using var coordinator = CreateCoordinator(
            Authenticated(session),
            new FakeRuntimeFactory { Runtime = runtime },
            router);
        await coordinator.LoginAsync(ServerBaseUri.AbsoluteUri, "shell-user", "secret");
        await WaitUntilAsync(() => coordinator.ConversationList.Status ==
            LocalCacheOperationStatus.Ready);
        coordinator.SelectConversation(conversationId);
        await WaitUntilAsync(() => coordinator.MessageList.Status ==
            ClientMessageListStatus.Ready);

        var outcome = await coordinator.RevealAttachmentInFolderAsync(attachmentId);

        Assert.Equal(ClientAttachmentRevealStatus.Revealed, outcome.Status);
        Assert.Equal(conversationId, capturedConversationId);
        Assert.Equal(attachmentId, capturedAttachmentId);
    }

    [Fact]
    public async Task RevealAttachmentInFolderAsync_WhenAttachmentIsNotInSelection_DoesNotCallRuntime()
    {
        var session = CreateSession();
        var conversationId = Guid.NewGuid();
        var calls = 0;
        var runtime = new FakeRuntime(session)
        {
            ConversationListOutcome = CreateConversationListOutcome(conversationId, 0),
            MessagePageReadAction = (id, _, _, _) => Task.FromResult(
                new LocalMessagePageReadOutcome(
                    LocalCacheOperationStatus.Ready,
                    id,
                    Array.Empty<MessageDto>(),
                    NextBeforeMessageId: null,
                    HasMoreBefore: false)),
            AttachmentRevealAction = (_, _, _, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(ClientAttachmentRevealOutcome.FromStatus(
                    ClientAttachmentRevealStatus.Revealed));
            },
        };
        using var router = CreateRouter();
        await using var coordinator = CreateCoordinator(
            Authenticated(session),
            new FakeRuntimeFactory { Runtime = runtime },
            router);
        await coordinator.LoginAsync(ServerBaseUri.AbsoluteUri, "shell-user", "secret");
        await WaitUntilAsync(() => coordinator.ConversationList.Status ==
            LocalCacheOperationStatus.Ready);
        coordinator.SelectConversation(conversationId);
        await WaitUntilAsync(() => coordinator.MessageList.Status ==
            ClientMessageListStatus.Ready);

        var outcome = await coordinator.RevealAttachmentInFolderAsync(Guid.NewGuid());

        Assert.Equal(ClientAttachmentRevealStatus.AttachmentUnavailable, outcome.Status);
        Assert.Equal(0, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task RevealAttachmentInFolderAsync_WhenSelectionChangesBeforeCommit_DoesNotReveal()
    {
        var session = CreateSession();
        var conversationId = Guid.NewGuid();
        var attachmentId = Guid.NewGuid();
        var attachment = new AttachmentDto(
            attachmentId,
            "selection-race.bin",
            "application/octet-stream",
            3,
            $"/api/attachments/{attachmentId:D}/download",
            ThumbnailUrl: null);
        var message = CreateMessage(10, conversationId) with
        {
            Type = MessageType.File,
            Content = null,
            Attachments = [attachment],
        };
        var commitReady = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var commitRelease = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var revealCalls = 0;
        var runtime = new FakeRuntime(session)
        {
            ConversationListOutcome = CreateConversationListOutcome(conversationId, 0),
            MessagePageReadAction = (id, _, _, _) => Task.FromResult(
                new LocalMessagePageReadOutcome(
                    LocalCacheOperationStatus.Ready,
                    id,
                    [message],
                    NextBeforeMessageId: null,
                    HasMoreBefore: false)),
            AttachmentRevealAction = async (_, _, commit, _) =>
            {
                commitReady.TrySetResult();
                await commitRelease.Task;
                var status = commit();
                if (status == ClientAttachmentRevealStatus.Revealed)
                {
                    Interlocked.Increment(ref revealCalls);
                }

                return ClientAttachmentRevealOutcome.FromStatus(status);
            },
        };
        using var router = CreateRouter();
        await using var coordinator = CreateCoordinator(
            Authenticated(session),
            new FakeRuntimeFactory { Runtime = runtime },
            router);
        await coordinator.LoginAsync(ServerBaseUri.AbsoluteUri, "shell-user", "secret");
        await WaitUntilAsync(() => coordinator.ConversationList.Status ==
            LocalCacheOperationStatus.Ready);
        coordinator.SelectConversation(conversationId);
        await WaitUntilAsync(() => coordinator.MessageList.Status ==
            ClientMessageListStatus.Ready);

        var reveal = coordinator.RevealAttachmentInFolderAsync(attachmentId);
        await commitReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
        coordinator.SelectConversation(conversationId: null);
        commitRelease.TrySetResult();
        var outcome = await reveal;

        Assert.Equal(ClientAttachmentRevealStatus.Stale, outcome.Status);
        Assert.Equal(0, Volatile.Read(ref revealCalls));
    }

    [Fact]
    public async Task RevealAttachmentInFolderAsync_WhenSelectionBecomesNonReadyBeforeCommit_DoesNotReveal()
    {
        var session = CreateSession();
        var conversationId = Guid.NewGuid();
        var attachmentId = Guid.NewGuid();
        var attachment = new AttachmentDto(
            attachmentId,
            "non-ready.bin",
            "application/octet-stream",
            3,
            $"/api/attachments/{attachmentId:D}/download",
            ThumbnailUrl: null);
        var message = CreateMessage(10, conversationId) with
        {
            Type = MessageType.File,
            Content = null,
            Attachments = [attachment],
        };
        var returnFailure = 0;
        var commitReady = NewSignal();
        var commitRelease = NewSignal();
        var revealCalls = 0;
        var runtime = new FakeRuntime(session)
        {
            ConversationListOutcome = CreateConversationListOutcome(conversationId, 0),
            MessagePageReadAction = (id, _, _, _) => Task.FromResult(
                Volatile.Read(ref returnFailure) == 0
                    ? new LocalMessagePageReadOutcome(
                        LocalCacheOperationStatus.Ready,
                        id,
                        [message],
                        NextBeforeMessageId: null,
                        HasMoreBefore: false)
                    : LocalMessagePageReadOutcome.Failure(
                        LocalCacheOperationStatus.FatalScope,
                        id)),
            AttachmentRevealAction = async (_, _, commit, _) =>
            {
                commitReady.TrySetResult();
                await commitRelease.Task;
                var status = commit();
                if (status == ClientAttachmentRevealStatus.Revealed)
                {
                    Interlocked.Increment(ref revealCalls);
                }

                return ClientAttachmentRevealOutcome.FromStatus(status);
            },
        };
        using var router = CreateRouter();
        await using var coordinator = CreateCoordinator(
            Authenticated(session),
            new FakeRuntimeFactory { Runtime = runtime },
            router);
        await coordinator.LoginAsync(ServerBaseUri.AbsoluteUri, "shell-user", "secret");
        await WaitUntilAsync(() => coordinator.ConversationList.Status ==
            LocalCacheOperationStatus.Ready);
        coordinator.SelectConversation(conversationId);
        await WaitUntilAsync(() => coordinator.MessageList.Status ==
            ClientMessageListStatus.Ready);

        var reveal = coordinator.RevealAttachmentInFolderAsync(attachmentId);
        await commitReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Interlocked.Exchange(ref returnFailure, 1);
        runtime.RaiseConversationStateChanged(2);
        await WaitUntilAsync(() => coordinator.MessageList.Status ==
            ClientMessageListStatus.FatalScope);

        commitRelease.TrySetResult();
        var outcome = await reveal;

        Assert.Equal(ClientAttachmentRevealStatus.Stale, outcome.Status);
        Assert.Equal(0, Volatile.Read(ref revealCalls));
    }

    [Fact]
    public async Task RevealAttachmentInFolderAsync_WhenShellIsRunning_DoesNotBlockSelectionChange()
    {
        var session = CreateSession();
        var conversationId = Guid.NewGuid();
        var attachmentId = Guid.NewGuid();
        var attachment = new AttachmentDto(
            attachmentId,
            "running-shell.bin",
            "application/octet-stream",
            3,
            $"/api/attachments/{attachmentId:D}/download",
            ThumbnailUrl: null);
        var message = CreateMessage(10, conversationId) with
        {
            Type = MessageType.File,
            Content = null,
            Attachments = [attachment],
        };
        var shellStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var shellRelease = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new FakeRuntime(session)
        {
            ConversationListOutcome = CreateConversationListOutcome(conversationId, 0),
            MessagePageReadAction = (id, _, _, _) => Task.FromResult(
                new LocalMessagePageReadOutcome(
                    LocalCacheOperationStatus.Ready,
                    id,
                    [message],
                    NextBeforeMessageId: null,
                    HasMoreBefore: false)),
            AttachmentRevealAction = async (_, _, commit, _) =>
            {
                var status = commit();
                if (status != ClientAttachmentRevealStatus.Revealed)
                {
                    return ClientAttachmentRevealOutcome.FromStatus(status);
                }

                shellStarted.TrySetResult();
                await shellRelease.Task;
                return ClientAttachmentRevealOutcome.FromStatus(
                    ClientAttachmentRevealStatus.Revealed);
            },
        };
        using var router = CreateRouter();
        await using var coordinator = CreateCoordinator(
            Authenticated(session),
            new FakeRuntimeFactory { Runtime = runtime },
            router);
        await coordinator.LoginAsync(ServerBaseUri.AbsoluteUri, "shell-user", "secret");
        await WaitUntilAsync(() => coordinator.ConversationList.Status ==
            LocalCacheOperationStatus.Ready);
        coordinator.SelectConversation(conversationId);
        await WaitUntilAsync(() => coordinator.MessageList.Status ==
            ClientMessageListStatus.Ready);

        var reveal = coordinator.RevealAttachmentInFolderAsync(attachmentId);
        await shellStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        coordinator.SelectConversation(conversationId: null);
        Assert.Equal(ClientMessageListStatus.None, coordinator.MessageList.Status);

        shellRelease.TrySetResult();
        var outcome = await reveal;

        Assert.Equal(ClientAttachmentRevealStatus.Stale, outcome.Status);
    }

    [Fact]
    public async Task SendTextMessageAsync_WhenReplyAndMentionsAreValid_ForwardsCanonicalPayload()
    {
        var session = CreateSession();
        var conversationId = Guid.NewGuid();
        var target = CreateMessage(10, conversationId);
        Guid? sentConversationId = null;
        string? sentContent = null;
        long? sentReplyToMessageId = null;
        IReadOnlyList<Guid>? sentMentionUserIds = null;
        var firstMention = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var secondMention = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var runtime = new FakeRuntime(session)
        {
            ConversationListOutcome = CreateConversationListOutcome(conversationId, 0),
            MessagePageReadAction = (id, _, _, _) => Task.FromResult(
                new LocalMessagePageReadOutcome(
                    LocalCacheOperationStatus.Ready,
                    id,
                    [target],
                    NextBeforeMessageId: null,
                    HasMoreBefore: false)),
            MessageSendAction = (conversation, content, replyToMessageId, mentions, _) =>
            {
                sentConversationId = conversation;
                sentContent = content;
                sentReplyToMessageId = replyToMessageId;
                sentMentionUserIds = mentions;
                return Task.FromResult(new ClientMessageSendOutcome(
                    ClientMessageSendStatus.Completed,
                    PendingCommitted: true));
            },
        };
        using var router = CreateRouter();
        await using var coordinator = CreateCoordinator(
            Authenticated(session),
            new FakeRuntimeFactory { Runtime = runtime },
            router);
        await coordinator.LoginAsync(ServerBaseUri.AbsoluteUri, "shell-user", "secret");
        await WaitUntilAsync(() => coordinator.ConversationList.Status ==
            LocalCacheOperationStatus.Ready);
        coordinator.SelectConversation(conversationId);
        await WaitUntilAsync(() => coordinator.MessageList.Status ==
            ClientMessageListStatus.Ready &&
            coordinator.MessageList.Messages.Any(item => item.ServerMessageId == 10));

        var outcome = await coordinator.SendTextMessageAsync(
            "reply body",
            10,
            [secondMention, firstMention]);

        Assert.Equal(ClientMessageSendStatus.Completed, outcome.Status);
        Assert.Equal(conversationId, sentConversationId);
        Assert.Equal("reply body", sentContent);
        Assert.Equal(10, sentReplyToMessageId);
        Assert.Equal([firstMention, secondMention], sentMentionUserIds);
    }

    [Fact]
    public async Task SendTextMessageAsync_WhenReplyTargetIsMissingOrInvalid_DoesNotInvokeRuntime()
    {
        var session = CreateSession();
        var conversationId = Guid.NewGuid();
        var sendCount = 0;
        var runtime = new FakeRuntime(session)
        {
            ConversationListOutcome = CreateConversationListOutcome(conversationId, 0),
            MessagePageReadAction = (id, _, _, _) => Task.FromResult(
                new LocalMessagePageReadOutcome(
                    LocalCacheOperationStatus.Ready,
                    id,
                    [CreateMessage(10, id)],
                    NextBeforeMessageId: null,
                    HasMoreBefore: false)),
            MessageSendAction = (_, _, _, _, _) =>
            {
                Interlocked.Increment(ref sendCount);
                return Task.FromResult(new ClientMessageSendOutcome(
                    ClientMessageSendStatus.Completed,
                    PendingCommitted: true));
            },
        };
        using var router = CreateRouter();
        await using var coordinator = CreateCoordinator(
            Authenticated(session),
            new FakeRuntimeFactory { Runtime = runtime },
            router);
        await coordinator.LoginAsync(ServerBaseUri.AbsoluteUri, "shell-user", "secret");
        await WaitUntilAsync(() => coordinator.ConversationList.Status ==
            LocalCacheOperationStatus.Ready);
        coordinator.SelectConversation(conversationId);
        await WaitUntilAsync(() => coordinator.MessageList.Status ==
            ClientMessageListStatus.Ready &&
            coordinator.MessageList.Messages.Any(item => item.ServerMessageId == 10));

        var missing = await coordinator.SendTextMessageAsync("missing", 999);
        var zero = await coordinator.SendTextMessageAsync("zero", 0);
        var negative = await coordinator.SendTextMessageAsync("negative", -1);

        Assert.Equal(ClientMessageSendStatus.Unavailable, missing.Status);
        Assert.Equal(ClientMessageSendStatus.Unavailable, zero.Status);
        Assert.Equal(ClientMessageSendStatus.Unavailable, negative.Status);
        Assert.Equal(0, Volatile.Read(ref sendCount));
    }

    [Fact]
    public async Task SendTextMessageAsync_WhenMentionSetIsInvalid_DoesNotInvokeRuntime()
    {
        var session = CreateSession();
        var conversationId = Guid.NewGuid();
        var sendCount = 0;
        var duplicate = Guid.NewGuid();
        var runtime = new FakeRuntime(session)
        {
            ConversationListOutcome = CreateConversationListOutcome(conversationId, 0),
            MessagePageReadAction = (id, _, _, _) => Task.FromResult(
                new LocalMessagePageReadOutcome(
                    LocalCacheOperationStatus.Ready,
                    id,
                    Array.Empty<MessageDto>(),
                    NextBeforeMessageId: null,
                    HasMoreBefore: false)),
            MessageSendAction = (_, _, _, _, _) =>
            {
                Interlocked.Increment(ref sendCount);
                return Task.FromResult(ClientMessageSendOutcome.Failure(
                    ClientMessageSendStatus.RemoteFailure));
            },
        };
        using var router = CreateRouter();
        await using var coordinator = CreateCoordinator(
            Authenticated(session),
            new FakeRuntimeFactory { Runtime = runtime },
            router);
        await coordinator.LoginAsync(ServerBaseUri.AbsoluteUri, "shell-user", "secret");
        await WaitUntilAsync(() => coordinator.ConversationList.Status ==
            LocalCacheOperationStatus.Ready);
        coordinator.SelectConversation(conversationId);
        await WaitUntilAsync(() => coordinator.MessageList.Status ==
            ClientMessageListStatus.Ready);

        var outcome = await coordinator.SendTextMessageAsync(
            "invalid mentions",
            mentionUserIds: [duplicate, duplicate]);

        Assert.Equal(ClientMessageSendStatus.ValidationFailed, outcome.Status);
        Assert.False(outcome.PendingCommitted);
        Assert.Equal(0, Volatile.Read(ref sendCount));
    }

    [Fact]
    public async Task SendTextMessageAsync_WhenSelectionChanges_KeepsCapturedConversationAndReplyFlight()
    {
        var session = CreateSession();
        var firstConversationId = Guid.NewGuid();
        var secondConversationId = Guid.NewGuid();
        var sendEntered = NewSignal();
        var releaseSend = NewSignal();
        Guid? sentConversationId = null;
        long? sentReplyToMessageId = null;
        CancellationToken sentToken = default;
        var runtime = new FakeRuntime(session)
        {
            ConversationListOutcome = CreateConversationListOutcome(
                firstConversationId,
                secondConversationId),
            MessagePageReadAction = (id, _, _, _) => Task.FromResult(
                new LocalMessagePageReadOutcome(
                    LocalCacheOperationStatus.Ready,
                    id,
                    [CreateMessage(id == firstConversationId ? 10 : 20, id)],
                    NextBeforeMessageId: null,
                    HasMoreBefore: false)),
            MessageSendAction = async (conversationId, _, replyToMessageId, _, token) =>
            {
                sentConversationId = conversationId;
                sentReplyToMessageId = replyToMessageId;
                sentToken = token;
                sendEntered.TrySetResult();
                await releaseSend.Task;
                return new ClientMessageSendOutcome(
                    ClientMessageSendStatus.Completed,
                    PendingCommitted: true);
            },
        };
        using var router = CreateRouter();
        await using var coordinator = CreateCoordinator(
            Authenticated(session),
            new FakeRuntimeFactory { Runtime = runtime },
            router);
        await coordinator.LoginAsync(ServerBaseUri.AbsoluteUri, "shell-user", "secret");
        await WaitUntilAsync(() => coordinator.ConversationList.Status ==
            LocalCacheOperationStatus.Ready);
        coordinator.SelectConversation(firstConversationId);
        await WaitUntilAsync(() => coordinator.MessageList.Status ==
            ClientMessageListStatus.Ready &&
            coordinator.MessageList.Messages.Any(item => item.ServerMessageId == 10));

        var send = coordinator.SendTextMessageAsync("persist across selection", 10);
        await sendEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        coordinator.SelectConversation(secondConversationId);
        await WaitUntilAsync(() => coordinator.MessageList.Status ==
            ClientMessageListStatus.Ready &&
            coordinator.MessageList.ConversationId == secondConversationId);

        Assert.False(sentToken.IsCancellationRequested);
        releaseSend.TrySetResult();
        var outcome = await send;

        Assert.Equal(firstConversationId, sentConversationId);
        Assert.Equal(10, sentReplyToMessageId);
        Assert.Equal(ClientMessageSendStatus.Completed, outcome.Status);
        Assert.Equal(secondConversationId, coordinator.MessageList.ConversationId);
    }

    [Fact]
    public async Task SendAttachmentsAsync_WhenReplyAndMentionsAreValid_ForwardsProgressAndCanonicalPayload()
    {
        var session = CreateSession();
        var conversationId = Guid.NewGuid();
        var target = CreateMessage(10, conversationId);
        var source = new ClientAttachmentUploadSource(
            "one.png",
            "image/png",
            1,
            _ => ValueTask.FromResult<Stream>(new MemoryStream([1], writable: false)));
        var progress = new Progress<ClientAttachmentSendProgress>(_ => { });
        Guid? sentConversationId = null;
        MessageType? sentType = null;
        IReadOnlyList<ClientAttachmentUploadSource>? sentSources = null;
        long? sentReplyToMessageId = null;
        IReadOnlyList<Guid>? sentMentionUserIds = null;
        IProgress<ClientAttachmentSendProgress>? sentProgress = null;
        var firstMention = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var secondMention = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var runtime = new FakeRuntime(session)
        {
            ConversationListOutcome = CreateConversationListOutcome(conversationId, 0),
            MessagePageReadAction = (id, _, _, _) => Task.FromResult(
                new LocalMessagePageReadOutcome(
                    LocalCacheOperationStatus.Ready,
                    id,
                    [target],
                    NextBeforeMessageId: null,
                    HasMoreBefore: false)),
            AttachmentSendAction = (
                conversation,
                type,
                sources,
                replyToMessageId,
                mentions,
                _,
                reportedProgress) =>
            {
                sentConversationId = conversation;
                sentType = type;
                sentSources = sources;
                sentReplyToMessageId = replyToMessageId;
                sentMentionUserIds = mentions;
                sentProgress = reportedProgress;
                return Task.FromResult(new ClientMessageSendOutcome(
                    ClientMessageSendStatus.Completed,
                    PendingCommitted: true));
            },
        };
        using var router = CreateRouter();
        await using var coordinator = CreateCoordinator(
            Authenticated(session),
            new FakeRuntimeFactory { Runtime = runtime },
            router);
        await coordinator.LoginAsync(ServerBaseUri.AbsoluteUri, "shell-user", "secret");
        await WaitUntilAsync(() => coordinator.ConversationList.Status ==
            LocalCacheOperationStatus.Ready);
        coordinator.SelectConversation(conversationId);
        await WaitUntilAsync(() => coordinator.MessageList.Status ==
            ClientMessageListStatus.Ready &&
            coordinator.MessageList.Messages.Any(item => item.ServerMessageId == 10));

        var outcome = await coordinator.SendAttachmentsAsync(
            MessageType.Image,
            [source],
            10,
            [secondMention, firstMention],
            progress: progress);

        Assert.Equal(ClientMessageSendStatus.Completed, outcome.Status);
        Assert.Equal(conversationId, sentConversationId);
        Assert.Equal(MessageType.Image, sentType);
        Assert.Equal([source], sentSources);
        Assert.Equal(10, sentReplyToMessageId);
        Assert.Equal([firstMention, secondMention], sentMentionUserIds);
        Assert.Same(progress, sentProgress);
    }

    [Fact]
    public async Task SendAttachmentsAsync_WhenReplyOrMentionsAreInvalid_DoesNotInvokeRuntime()
    {
        var session = CreateSession();
        var conversationId = Guid.NewGuid();
        var source = new ClientAttachmentUploadSource(
            "one.bin",
            "application/octet-stream",
            1,
            _ => ValueTask.FromResult<Stream>(new MemoryStream([1], writable: false)));
        var sendCount = 0;
        var duplicateMention = Guid.NewGuid();
        var runtime = new FakeRuntime(session)
        {
            ConversationListOutcome = CreateConversationListOutcome(conversationId, 0),
            MessagePageReadAction = (id, _, _, _) => Task.FromResult(
                new LocalMessagePageReadOutcome(
                    LocalCacheOperationStatus.Ready,
                    id,
                    [CreateMessage(10, id)],
                    NextBeforeMessageId: null,
                    HasMoreBefore: false)),
            AttachmentSendAction = (_, _, _, _, _, _, _) =>
            {
                Interlocked.Increment(ref sendCount);
                return Task.FromResult(ClientMessageSendOutcome.Failure(
                    ClientMessageSendStatus.RemoteFailure));
            },
        };
        using var router = CreateRouter();
        await using var coordinator = CreateCoordinator(
            Authenticated(session),
            new FakeRuntimeFactory { Runtime = runtime },
            router);
        await coordinator.LoginAsync(ServerBaseUri.AbsoluteUri, "shell-user", "secret");
        await WaitUntilAsync(() => coordinator.ConversationList.Status ==
            LocalCacheOperationStatus.Ready);
        coordinator.SelectConversation(conversationId);
        await WaitUntilAsync(() => coordinator.MessageList.Status ==
            ClientMessageListStatus.Ready);

        var missingReply = await coordinator.SendAttachmentsAsync(
            MessageType.File,
            [source],
            replyToMessageId: 999);
        var invalidMentions = await coordinator.SendAttachmentsAsync(
            MessageType.File,
            [source],
            mentionUserIds: [duplicateMention, duplicateMention]);

        Assert.Equal(ClientMessageSendStatus.Unavailable, missingReply.Status);
        Assert.Equal(ClientMessageSendStatus.ValidationFailed, invalidMentions.Status);
        Assert.Equal(0, Volatile.Read(ref sendCount));
    }

    [Fact]
    public async Task SendAttachmentsAsync_WhenAuthenticationExpires_EndsSession()
    {
        var session = CreateSession();
        var conversationId = Guid.NewGuid();
        var source = new ClientAttachmentUploadSource(
            "one.bin",
            "application/octet-stream",
            1,
            _ => ValueTask.FromResult<Stream>(new MemoryStream([1], writable: false)));
        var runtime = new FakeRuntime(session)
        {
            ConversationListOutcome = CreateConversationListOutcome(conversationId, 0),
            MessagePageReadAction = (id, _, _, _) => Task.FromResult(
                new LocalMessagePageReadOutcome(
                    LocalCacheOperationStatus.Ready,
                    id,
                    Array.Empty<MessageDto>(),
                    NextBeforeMessageId: null,
                    HasMoreBefore: false)),
            AttachmentSendAction = (_, _, _, _, _, _, _) => Task.FromResult(
                ClientMessageSendOutcome.Failure(
                    ClientMessageSendStatus.AuthenticationRequired)),
        };
        using var router = CreateRouter();
        await using var coordinator = CreateCoordinator(
            Authenticated(session),
            new FakeRuntimeFactory { Runtime = runtime },
            router);
        await coordinator.LoginAsync(ServerBaseUri.AbsoluteUri, "shell-user", "secret");
        await WaitUntilAsync(() => coordinator.ConversationList.Status ==
            LocalCacheOperationStatus.Ready);
        coordinator.SelectConversation(conversationId);
        await WaitUntilAsync(() => coordinator.MessageList.Status ==
            ClientMessageListStatus.Ready);

        var outcome = await coordinator.SendAttachmentsAsync(MessageType.File, [source]);

        Assert.Equal(ClientMessageSendStatus.AuthenticationRequired, outcome.Status);
        Assert.Equal(ClientAccountShellPhase.SignedOut, coordinator.Snapshot.Phase);
        Assert.False(coordinator.Snapshot.HasActiveAccount);
        Assert.Equal(1, runtime.DisposeCount);
    }

    [Fact]
    public async Task RetryPendingMessageAsync_WhenFailedRowIsVisible_UsesOriginalClientKey()
    {
        var session = CreateSession();
        var conversationId = Guid.NewGuid();
        var clientMessageId = Guid.NewGuid();
        Guid? retriedConversationId = null;
        Guid? retriedClientMessageId = null;
        var pending = new LocalPendingMessage(
            LocalId: 1,
            ClientMessageId: clientMessageId,
            ConversationId: conversationId,
            SenderId: session.UserId!.Value,
            SenderDisplayName: "Sender",
            Type: MessageType.Text,
            Content: "failed",
            ReplyToMessageId: null,
            MentionUserIds: Array.Empty<Guid>(),
            CreatedAt: DateTimeOffset.Parse("2026-08-03T03:00:00Z"),
            SendStatus: MessageSendStatus.Failed);
        var runtime = new FakeRuntime(session)
        {
            ConversationListOutcome = CreateConversationListOutcome(conversationId, 0),
            MessagePageReadAction = (id, _, _, _) => Task.FromResult(
                new LocalMessagePageReadOutcome(
                    LocalCacheOperationStatus.Ready,
                    id,
                    Array.Empty<MessageDto>(),
                    NextBeforeMessageId: null,
                    HasMoreBefore: false,
                    PendingMessages: [pending])),
            MessageRetryAction = (conversation, clientMessage, _) =>
            {
                retriedConversationId = conversation;
                retriedClientMessageId = clientMessage;
                return Task.FromResult(new ClientMessageSendOutcome(
                    ClientMessageSendStatus.Completed,
                    PendingCommitted: true));
            },
        };
        using var router = CreateRouter();
        await using var coordinator = CreateCoordinator(
            Authenticated(session),
            new FakeRuntimeFactory { Runtime = runtime },
            router);
        await coordinator.LoginAsync(ServerBaseUri.AbsoluteUri, "shell-user", "secret");
        await WaitUntilAsync(() => coordinator.ConversationList.Status ==
            LocalCacheOperationStatus.Ready);
        coordinator.SelectConversation(conversationId);
        await WaitUntilAsync(() => coordinator.MessageList.Status ==
            ClientMessageListStatus.Ready &&
            coordinator.MessageList.Messages.Count == 1);

        var outcome = await coordinator.RetryPendingMessageAsync(clientMessageId);

        Assert.Equal(ClientMessageSendStatus.Completed, outcome.Status);
        Assert.Equal(conversationId, retriedConversationId);
        Assert.Equal(clientMessageId, retriedClientMessageId);
    }

    [Fact]
    public async Task DisposeAsync_WhenCalledConcurrently_SharesCleanupAndRevokesActivation()
    {
        var session = CreateSession();
        var releaseDispose = NewSignal();
        var runtime = new FakeRuntime(session)
        {
            DisposeAction = async () => await releaseDispose.Task,
        };
        using var router = CreateRouter();
        var coordinator = CreateCoordinator(
            Authenticated(session),
            new FakeRuntimeFactory { Runtime = runtime },
            router);
        await coordinator.LoginAsync(ServerBaseUri.AbsoluteUri, "shell-user", "secret");

        var first = coordinator.DisposeAsync().AsTask();
        var second = coordinator.DisposeAsync().AsTask();
        releaseDispose.TrySetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Same(first, second);
        Assert.Equal(1, runtime.DisposeCount);
        Assert.Equal(
            ClientNotificationActivationRouteStatus.NoActiveAccount,
            router.TryRoute(ClientNotificationActivationTarget.UnreadOverview(runtime.Identity.Id)));
    }

    [Fact]
    public async Task DisposeAsync_WithRetryAndLogoutQueued_CancelsWithoutDisposedPrimitiveRace()
    {
        var session = CreateSession();
        var retryEntered = NewSignal();
        var releaseRetry = NewSignal();
        var runtime = new FakeRuntime(session)
        {
            RetryAction = async _ =>
            {
                retryEntered.TrySetResult();
                await releaseRetry.Task;
                return new ClientSyncRunOutcome(
                    ClientSyncRunStatus.Completed,
                    SyncReason.Reconnect,
                    RoundsExecuted: 1);
            },
        };
        using var router = CreateRouter();
        var coordinator = CreateCoordinator(
            Authenticated(session),
            new FakeRuntimeFactory { Runtime = runtime },
            router);
        await coordinator.LoginAsync(ServerBaseUri.AbsoluteUri, "shell-user", "secret");

        var retry = coordinator.RetryAsync();
        await retryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var logout = coordinator.LogoutAsync();
        var dispose = coordinator.DisposeAsync().AsTask();
        releaseRetry.TrySetResult();
        await retry.WaitAsync(TimeSpan.FromSeconds(5));
        var cancellation = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => logout);
        await dispose.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsNotType<ObjectDisposedException>(cancellation);
        Assert.Equal(1, runtime.DisposeCount);
        Assert.Equal(ClientAccountShellPhase.Stopping, coordinator.Snapshot.Phase);
    }

    [Fact]
    public void SnapshotToString_RedactsAccountIdentity()
    {
        var snapshot = new ClientAccountShellSnapshot(
            ClientAccountShellPhase.Active,
            PersistentClientAuthenticationStatus.Authenticated,
            "classified display name",
            new Uri("https://classified.example/secret/"),
            ConnectionState.Connected,
            ClientSyncRunStatus.Completed,
            LastLogoutStatus: null,
            RetryAfter: null);

        var text = snapshot.ToString();

        Assert.DoesNotContain("classified", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[REDACTED]", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Presenter_WhenAuthenticationIsRateLimited_ShowsRetryWithoutIdentityLeak()
    {
        var snapshot = ClientAccountShellSnapshot.SignedOut(
            PersistentClientAuthenticationStatus.RateLimited,
            retryAfter: TimeSpan.FromSeconds(9.2));

        var presentation = ClientAccountShellPresenter.Present(snapshot);

        Assert.True(presentation.ShowLogin);
        Assert.False(presentation.IsBusy);
        Assert.Contains("10 秒", presentation.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", presentation.ToString(), StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", presentation.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Presenter_WhenAuthenticationFailsAndCredentialClearFails_ShowsBothFailures()
    {
        var snapshot = ClientAccountShellSnapshot.SignedOut(
            PersistentClientAuthenticationStatus.AuthenticationFailed,
            ClientLogoutStatus.CredentialClearFailed);

        var presentation = ClientAccountShellPresenter.Present(snapshot);

        Assert.Contains("服务器未接受", presentation.Detail, StringComparison.Ordinal);
        Assert.Contains("本地凭据清理未完全成功", presentation.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Presenter_WhenAccountIsRetrying_DisablesDuplicateRetryButAllowsLogout()
    {
        var snapshot = new ClientAccountShellSnapshot(
            ClientAccountShellPhase.Retrying,
            PersistentClientAuthenticationStatus.Authenticated,
            "Shell User",
            ServerBaseUri,
            ConnectionState.Reconnecting,
            ClientSyncRunStatus.TransientFailure,
            LastLogoutStatus: null,
            RetryAfter: null);

        var presentation = ClientAccountShellPresenter.Present(snapshot);

        Assert.False(presentation.ShowLogin);
        Assert.True(presentation.IsBusy);
        Assert.False(presentation.CanRetry);
        Assert.True(presentation.CanLogout);
        Assert.Equal("实时连接：重连中", presentation.ConnectionLabel);
    }

    [Fact]
    public void Presenter_WhileSigningIn_KeepsDisabledLoginSurfaceVisible()
    {
        var snapshot = new ClientAccountShellSnapshot(
            ClientAccountShellPhase.SigningIn,
            AuthenticationStatus: null,
            DisplayName: null,
            ServerBaseUri: null,
            ConnectionState.Disconnected,
            LastSyncStatus: null,
            LastLogoutStatus: null,
            RetryAfter: null);

        var presentation = ClientAccountShellPresenter.Present(snapshot);

        Assert.True(presentation.ShowLogin);
        Assert.True(presentation.IsBusy);
        Assert.False(presentation.CanRetry);
        Assert.False(presentation.CanLogout);
    }

    [Fact]
    public void Presenter_WhenSystemNotificationsAreUnavailable_ShowsNonBlockingDegradation()
    {
        Assert.Equal(
            "系统通知：不可用（账户仍可使用）",
            ClientAccountShellPresenter.DescribeNotificationAvailability(false));
        Assert.Equal(
            "系统通知：可用",
            ClientAccountShellPresenter.DescribeNotificationAvailability(true));
        Assert.Equal(
            "系统通知：初始化中",
            ClientAccountShellPresenter.DescribeNotificationAvailability(null));
    }

    [Fact]
    public async Task Composition_DisposeAsync_WhenCalledTwice_SharesCompletionAndRedactsPath()
    {
        var root = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            "RelayCove.Composition.Tests",
            Guid.NewGuid().ToString("N")));
        using var router = CreateRouter();
        var composition = ClientAccountComposition.Create(
            root,
            router,
            NoOpClientNotificationAttention.Instance,
            NullLoggerFactory.Instance);

        var first = composition.DisposeAsync().AsTask();
        var second = composition.DisposeAsync().AsTask();
        await Task.WhenAll(first, second);

        Assert.Same(first, second);
        Assert.DoesNotContain(root, composition.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[REDACTED]", composition.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Composition_DetachForProcessExit_LeavesAbandonedHttpClientUsable()
    {
        using var router = CreateRouter();
        var coordinator = CreateCoordinator(
            new FakeAuthentication(),
            new FakeRuntimeFactory(),
            router);
        using var httpClient = new HttpClient(new DelegateHttpHandler());
        var composition = new ClientAccountComposition(httpClient, coordinator);

        composition.DetachForProcessExit();

        using var response = await httpClient.GetAsync("https://example.com/health");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await composition.DisposeAsync();
    }

    [Fact]
    public async Task Composition_DisposeAsync_WithSeparateUploadClient_DisposesEachHandlerExactlyOnce()
    {
        using var router = CreateRouter();
        var coordinator = CreateCoordinator(
            new FakeAuthentication(),
            new FakeRuntimeFactory(),
            router);
        var standardHandler = new CountingHttpHandler();
        var uploadHandler = new CountingHttpHandler();
        var standardClient = new HttpClient(standardHandler);
        var uploadClient = new HttpClient(uploadHandler);
        var composition = new ClientAccountComposition(standardClient, uploadClient, coordinator);

        await Task.WhenAll(composition.DisposeAsync().AsTask(), composition.DisposeAsync().AsTask());

        Assert.Equal(1, standardHandler.DisposeCount);
        Assert.Equal(1, uploadHandler.DisposeCount);
    }

    [Fact]
    public async Task Composition_DetachForProcessExit_WithSeparateUploadClient_LeavesBothUsable()
    {
        using var router = CreateRouter();
        var coordinator = CreateCoordinator(
            new FakeAuthentication(),
            new FakeRuntimeFactory(),
            router);
        using var standardClient = new HttpClient(new CountingHttpHandler());
        using var uploadClient = new HttpClient(new CountingHttpHandler());
        var composition = new ClientAccountComposition(standardClient, uploadClient, coordinator);

        composition.DetachForProcessExit();

        using var standardResponse = await standardClient.GetAsync("https://example.com/health");
        using var uploadResponse = await uploadClient.GetAsync("https://example.com/api/attachments");
        Assert.Equal(HttpStatusCode.NoContent, standardResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, uploadResponse.StatusCode);
        await composition.DisposeAsync();
    }

    [Fact]
    public void Composition_HttpClientProfiles_KeepNormalTimeoutAndDisableUploadRedirects()
    {
        using var standardClient = ClientAccountComposition.CreateDefaultHttpClient();
        using var uploadClient = ClientAccountComposition.CreateAttachmentUploadHttpClient();
        using var uploadHandler = ClientAccountComposition.CreateAttachmentUploadHttpHandler();

        Assert.Equal(TimeSpan.FromSeconds(30), standardClient.Timeout);
        Assert.Equal(TimeSpan.FromMinutes(10), uploadClient.Timeout);
        Assert.False(uploadHandler.AllowAutoRedirect);
        Assert.Equal(DecompressionMethods.None, uploadHandler.AutomaticDecompression);
    }

    private static ClientAccountShellCoordinator CreateCoordinator(
        IClientPersistentAuthentication authentication,
        IClientAccountRuntimeFactory factory,
        ClientNotificationActivationRouter router,
        ILogger<ClientAccountShellCoordinator>? logger = null) =>
        new(
            authentication,
            factory,
            router,
            logger ?? NullLogger<ClientAccountShellCoordinator>.Instance,
            deviceNameProvider: () => "test-device",
            clientVersionProvider: () => "1.0.0");

    private static ClientNotificationActivationRouter CreateRouter(
        Action<ClientNotificationActivationTarget>? navigate = null) =>
        new(
            navigate ?? (_ => { }),
            NullLogger<ClientNotificationActivationRouter>.Instance);

    private static FakeAuthentication Authenticated(ClientAuthenticationSession session) =>
        new()
        {
            LoginOutcome = PersistentClientAuthenticationOutcome.Authenticated(
                session,
                isCredentialPersisted: true),
        };

    private static ClientAuthenticationSession CreateSession() =>
        new(
            ServerBaseUri,
            new HttpClient(new DelegateHttpHandler()),
            NullLogger<ClientAuthenticationClient>.Instance,
            new LoginResponse(
                UserId,
                "Shell User",
                "classified-access-token",
                "classified-refresh-token",
                Now.AddHours(1),
                "1.0.0",
                "1.0.0"),
            new FixedTimeProvider(Now));

    private static ClientAccountRuntimeStartOutcome Started() =>
        new(
            ConnectionState.Connected,
            new ClientSyncRunOutcome(
                ClientSyncRunStatus.Completed,
                SyncReason.Startup,
                RoundsExecuted: 1));

    private static LocalConversationListReadOutcome CreateConversationListOutcome(
        Guid conversationId,
        int totalUnreadCount) =>
        new(
            LocalCacheOperationStatus.Ready,
            [
                new LocalConversationListItem(
                    conversationId,
                    ConversationType.PrivateChannel,
                    "Conversation",
                    null,
                    10,
                    MessageType.Text,
                    "preview",
                    Now,
                    totalUnreadCount,
                    false,
                    Now),
            ],
            totalUnreadCount,
            Revision: 1);

    private static LocalConversationListReadOutcome CreateConversationListOutcome(
        Guid firstConversationId,
        Guid secondConversationId) =>
        new(
            LocalCacheOperationStatus.Ready,
            [
                CreateConversationListItem(firstConversationId),
                CreateConversationListItem(secondConversationId),
            ],
            TotalUnreadCount: 0,
            Revision: 1);

    private static LocalConversationListItem CreateConversationListItem(
        Guid conversationId) =>
        new(
            conversationId,
            ConversationType.PrivateChannel,
            "Conversation",
            AvatarUrl: null,
            LastMessageId: 10,
            MessageType.Text,
            LastMessageContent: "preview",
            LastMessageCreatedAt: Now,
            UnreadCount: 0,
            IsMuted: false,
            UpdatedAt: Now);

    private static MessageDto CreateMessage(long id, Guid conversationId) => new(
        id,
        Guid.NewGuid(),
        conversationId,
        Guid.NewGuid(),
        "Sender",
        MessageType.Text,
        $"message {id}",
        ReplyToMessageId: null,
        Array.Empty<AttachmentDto>(),
        Array.Empty<Guid>(),
        Now.AddSeconds(id));

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!predicate())
        {
            if (DateTime.UtcNow >= timeout)
            {
                throw new TimeoutException("The expected coordinator state was not published.");
            }

            await Task.Delay(10);
        }
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class FakeAuthentication : IClientPersistentAuthentication
    {
        public PersistentClientAuthenticationOutcome RestoreOutcome { get; init; } =
            PersistentClientAuthenticationOutcome.Failure(
                PersistentClientAuthenticationStatus.NoStoredCredential);

        public PersistentClientAuthenticationOutcome LoginOutcome { get; init; } =
            PersistentClientAuthenticationOutcome.Failure(
                PersistentClientAuthenticationStatus.AuthenticationFailed);

        public int RestoreCount { get; private set; }

        public int LoginCount { get; private set; }

        public LoginRequest? LastLoginRequest { get; private set; }

        public Func<CancellationToken, Task<PersistentClientAuthenticationOutcome>>? LoginAction
        {
            get;
            init;
        }

        public Task<PersistentClientAuthenticationOutcome> RestoreAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RestoreCount++;
            return Task.FromResult(RestoreOutcome);
        }

        public Task<PersistentClientAuthenticationOutcome> LoginAsync(
            Uri serverBaseUri,
            LoginRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = serverBaseUri;
            LoginCount++;
            LastLoginRequest = request;
            return LoginAction?.Invoke(cancellationToken) ?? Task.FromResult(LoginOutcome);
        }
    }

    private sealed class FakeRuntimeFactory : IClientAccountRuntimeFactory
    {
        public FakeRuntime? Runtime { get; init; }

        public Exception? CreateException { get; init; }

        public Task<IClientAccountRuntime> CreateAsync(
            ClientAuthenticationSession authenticationSession,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (CreateException is not null)
            {
                throw CreateException;
            }

            Assert.Same(authenticationSession, Runtime?.Session);
            return Task.FromResult<IClientAccountRuntime>(Runtime!);
        }
    }

    private sealed class FakeRuntime : IClientAccountRuntime
    {
        public FakeRuntime(ClientAuthenticationSession session)
        {
            Session = session;
            Identity = AccountScopeIdentity.Create(
                session.ServerBaseUri,
                session.UserId!.Value,
                Path.GetFullPath(Path.GetTempPath()));
        }

        public ClientAuthenticationSession Session { get; }

        public event Action<ConnectionState>? ConnectionStateChanged;

        public event Action<long>? ConversationStateChanged;

        public AccountScopeIdentity Identity { get; }

        public ConnectionState ConnectionState => ConnectionStateValue;

        public ConnectionState ConnectionStateValue { get; set; } = ConnectionState.Connected;

        public LocalConversationListReadOutcome ConversationListOutcome { get; set; } =
            new(
                LocalCacheOperationStatus.Ready,
                Array.Empty<LocalConversationListItem>(),
                TotalUnreadCount: 0,
                Revision: 1);

        public Func<CancellationToken, Task<LocalConversationListReadOutcome>>?
            ConversationListReadAction
        {
            get;
            set;
        }

        public Func<Guid, long?, int, CancellationToken, Task<LocalMessagePageReadOutcome>>?
            MessagePageReadAction
        {
            get;
            set;
        }

        public Func<Guid, long?, int, CancellationToken, Task<ClientMessageHistoryPageOutcome>>?
            MessageHistoryLoadAction
        {
            get;
            set;
        }

        public Func<Guid, long, int, int, CancellationToken, Task<ClientMessageAroundOutcome>>?
            MessageAroundLoadAction
        {
            get;
            set;
        }

        public Func<Guid, string?, int, CancellationToken, Task<ClientMentionCandidateOutcome>>?
            MentionCandidateSearchAction
        {
            get;
            set;
        }

        public Func<Guid, string?, long?, IReadOnlyList<Guid>?, CancellationToken,
            Task<ClientMessageSendOutcome>>?
            MessageSendAction
        {
            get;
            set;
        }

        public Func<Guid, MessageType, IReadOnlyList<ClientAttachmentUploadSource>?, long?,
            IReadOnlyList<Guid>?, CancellationToken, IProgress<ClientAttachmentSendProgress>?,
            Task<ClientMessageSendOutcome>>?
            AttachmentSendAction
        {
            get;
            set;
        }

        public Func<Guid, Guid, CancellationToken, Task<ClientMessageSendOutcome>>?
            MessageRetryAction
        {
            get;
            set;
        }

        public Func<Guid, Guid, CancellationToken,
            IProgress<ClientAttachmentDownloadProgress>?,
            Task<ClientAttachmentDownloadOutcome>>?
            AttachmentDownloadAction
        {
            get;
            set;
        }

        public Func<Guid, Guid, ClientAttachmentRevealCommit, CancellationToken,
            Task<ClientAttachmentRevealOutcome>>?
            AttachmentRevealAction
        {
            get;
            set;
        }

        public Func<Guid, long, CancellationToken, Task<LocalCacheOperationStatus>>?
            MarkRenderedAction
        {
            get;
            set;
        }

        public ClientSyncRunOutcome RetryOutcome { get; init; } = new(
            ClientSyncRunStatus.Completed,
            SyncReason.Reconnect,
            RoundsExecuted: 1);

        public ClientAccountRuntimeStartOutcome StartOutcome { get; init; } = Started();

        public Func<CancellationToken, Task<ClientAccountRuntimeStartOutcome>>? StartAction
        {
            get;
            init;
        }

        public Func<CancellationToken, Task<ClientLogoutStatus>>? LogoutAction { get; set; }

        public Func<CancellationToken, Task<ClientSyncRunOutcome>>? RetryAction
        {
            get;
            set;
        }

        public Func<Task>? DisposeAction { get; init; }

        public int StartCount { get; private set; }

        public int RetryCount { get; private set; }

        public int LogoutCount { get; private set; }

        public int DisposeCount { get; private set; }

        public ClientActivitySnapshot? LastActivity { get; private set; }

        public bool TryAuthorizeNotificationTarget(ClientNotificationActivationTarget target) =>
            target.AccountScopeId == Identity.Id;

        public void UpdateActivity(ClientActivitySnapshot snapshot) => LastActivity = snapshot;

        public Task<LocalConversationListReadOutcome> ReadConversationListAsync(
            CancellationToken cancellationToken = default) =>
            ConversationListReadAction?.Invoke(cancellationToken) ??
            Task.FromResult(ConversationListOutcome);

        public Task<LocalMessagePageReadOutcome> ReadMessagePageAsync(
            Guid conversationId,
            long? beforeMessageId,
            int limit,
            CancellationToken cancellationToken = default) =>
            MessagePageReadAction?.Invoke(
                conversationId,
                beforeMessageId,
                limit,
                cancellationToken) ??
            Task.FromResult(new LocalMessagePageReadOutcome(
                LocalCacheOperationStatus.Ready,
                conversationId,
                Array.Empty<MessageDto>(),
                NextBeforeMessageId: null,
                HasMoreBefore: false));

        public Task<ClientMessageHistoryPageOutcome> LoadMessageHistoryAsync(
            Guid conversationId,
            long? beforeMessageId,
            int limit,
            CancellationToken cancellationToken = default) =>
            MessageHistoryLoadAction?.Invoke(
                conversationId,
                beforeMessageId,
                limit,
                cancellationToken) ??
            Task.FromResult(new ClientMessageHistoryPageOutcome(
                ClientMessageLoadStatus.Completed,
                Array.Empty<MessageDto>(),
                NextBeforeMessageId: null,
                HasMore: false));

        public Task<ClientMessageAroundOutcome> LoadMessageAroundAsync(
            Guid conversationId,
            long messageId,
            int before,
            int after,
            CancellationToken cancellationToken = default) =>
            MessageAroundLoadAction?.Invoke(
                conversationId,
                messageId,
                before,
                after,
                cancellationToken) ??
            Task.FromResult(ClientMessageAroundOutcome.Failure(
                ClientMessageLoadStatus.RemoteFailure));

        public Task<ClientMentionCandidateOutcome> SearchMentionCandidatesAsync(
            Guid conversationId,
            string? query,
            int limit = ClientMentionCandidateCoordinator.DefaultLimit,
            CancellationToken cancellationToken = default) =>
            MentionCandidateSearchAction?.Invoke(
                conversationId,
                query,
                limit,
                cancellationToken) ??
            Task.FromResult(ClientMentionCandidateOutcome.Failure(
                ClientMentionCandidateStatus.RemoteFailure));

        public Task<ClientMessageSendOutcome> SendTextMessageAsync(
            Guid conversationId,
            string? content,
            long? replyToMessageId = null,
            IReadOnlyList<Guid>? mentionUserIds = null,
            CancellationToken cancellationToken = default) =>
            MessageSendAction?.Invoke(
                conversationId,
                content,
                replyToMessageId,
                mentionUserIds,
                cancellationToken) ??
            Task.FromResult(ClientMessageSendOutcome.Failure(
                ClientMessageSendStatus.RemoteFailure));

        public Task<ClientMessageSendOutcome> SendAttachmentsAsync(
            Guid conversationId,
            MessageType type,
            IReadOnlyList<ClientAttachmentUploadSource>? sources,
            long? replyToMessageId = null,
            IReadOnlyList<Guid>? mentionUserIds = null,
            CancellationToken cancellationToken = default,
            IProgress<ClientAttachmentSendProgress>? progress = null) =>
            AttachmentSendAction?.Invoke(
                conversationId,
                type,
                sources,
                replyToMessageId,
                mentionUserIds,
                cancellationToken,
                progress) ??
            Task.FromResult(ClientMessageSendOutcome.Failure(
                ClientMessageSendStatus.RemoteFailure));

        public Task<ClientMessageSendOutcome> RetryPendingMessageAsync(
            Guid conversationId,
            Guid clientMessageId,
            CancellationToken cancellationToken = default) =>
            MessageRetryAction?.Invoke(
                conversationId,
                clientMessageId,
                cancellationToken) ??
            Task.FromResult(ClientMessageSendOutcome.Failure(
                ClientMessageSendStatus.RemoteFailure));

        public Task<ClientAttachmentDownloadOutcome> DownloadAttachmentAsync(
            Guid conversationId,
            Guid attachmentId,
            CancellationToken cancellationToken = default,
            IProgress<ClientAttachmentDownloadProgress>? progress = null) =>
            AttachmentDownloadAction?.Invoke(
                conversationId,
                attachmentId,
                cancellationToken,
                progress) ??
            Task.FromResult(ClientAttachmentDownloadOutcome.Failure(
                ClientAttachmentDownloadStatus.RemoteFailure));

        public Task<ClientAttachmentRevealOutcome> RevealAttachmentInFolderAsync(
            Guid conversationId,
            Guid attachmentId,
            ClientAttachmentRevealCommit commit,
            CancellationToken cancellationToken = default) =>
            AttachmentRevealAction?.Invoke(
                conversationId,
                attachmentId,
                commit,
                cancellationToken) ??
            Task.FromResult(ClientAttachmentRevealOutcome.FromStatus(
                ClientAttachmentRevealStatus.ShellUnavailable));

        public Task<LocalCacheOperationStatus> MarkConversationRenderedThroughAsync(
            Guid conversationId,
            long messageId,
            CancellationToken cancellationToken = default) =>
            MarkRenderedAction?.Invoke(conversationId, messageId, cancellationToken) ??
            Task.FromResult(LocalCacheOperationStatus.Ready);

        public void RaiseConnectionStateChanged(ConnectionState state)
        {
            ConnectionStateValue = state;
            ConnectionStateChanged?.Invoke(state);
        }

        public void RaiseConversationStateChanged(long revision) =>
            ConversationStateChanged?.Invoke(revision);

        public Task<ClientAccountRuntimeStartOutcome> StartAsync(
            CancellationToken cancellationToken = default)
        {
            StartCount++;
            return StartAction?.Invoke(cancellationToken) ?? Task.FromResult(StartOutcome);
        }

        public Task<ClientSyncRunOutcome> RetryRealtimeAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RetryCount++;
            return RetryAction?.Invoke(cancellationToken) ?? Task.FromResult(RetryOutcome);
        }

        public Task<ClientLogoutStatus> LogoutAsync(
            CancellationToken cancellationToken = default)
        {
            LogoutCount++;
            return LogoutAction?.Invoke(cancellationToken) ??
                Task.FromResult(ClientLogoutStatus.LoggedOut);
        }

        public async ValueTask DisposeAsync()
        {
            DisposeCount++;
            if (DisposeAction is not null)
            {
                await DisposeAction();
            }

            await Session.DisposeAsync();
        }
    }

    private sealed class DelegateHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
    }

    private sealed class CountingHttpHandler : HttpMessageHandler
    {
        private int disposeCount;

        public int DisposeCount => Volatile.Read(ref disposeCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Interlocked.Increment(ref disposeCount);
            }

            base.Dispose(disposing);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<string> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue(formatter(state, exception));
    }
}
