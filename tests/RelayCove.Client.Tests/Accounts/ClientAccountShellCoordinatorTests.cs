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
        using var loggerFactory = NullLoggerFactory.Instance;
        var composition = ClientAccountComposition.Create(
            root,
            router,
            NoOpClientNotificationAttention.Instance,
            loggerFactory);

        var first = composition.DisposeAsync().AsTask();
        var second = composition.DisposeAsync().AsTask();
        await Task.WhenAll(first, second);

        Assert.Same(first, second);
        Assert.DoesNotContain(root, composition.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[REDACTED]", composition.ToString(), StringComparison.Ordinal);
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

        public AccountScopeIdentity Identity { get; }

        public ConnectionState ConnectionState => ConnectionStateValue;

        public ConnectionState ConnectionStateValue { get; init; } = ConnectionState.Connected;

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
            init;
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
