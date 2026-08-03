using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RelayCove.Client.Activation;
using RelayCove.Client.Notifications;
using RelayCove.Client.Storage;
using RelayCove.Shared.Conversations;

namespace RelayCove.Client.Tests.Activation;

[Collection(SqliteTestCollection.Name)]
public sealed class ClientActivationDispatcherTests : IDisposable
{
    private const string AccountScopeId =
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string OtherAccountScopeId =
        "ixtbwSB8U_2_R3Yb4lTASV38xQVX5opLhGkUGXOymEY";
    private static readonly Guid ConversationId =
        Guid.Parse("11111111-1111-1111-1111-111111111111");
    private readonly string rootDirectory = Path.Combine(
        Path.GetTempPath(),
        "RelayCove.Client.Activation.Tests",
        Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(rootDirectory))
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    [Fact]
    public void TryRoute_WhenNoAccount_ParksAndRevalidatesAfterAccountBecomesActive()
    {
        var navigated = new List<ClientNotificationActivationTarget>();
        using var router = CreateRouter(navigated.Add);
        var target = ClientNotificationActivationTarget.Message(
            AccountScopeId,
            ConversationId,
            1);

        var rejected = router.TryRoute(target);
        using var account = router.ActivateAccount(
            AccountScopeId,
            _ => true);
        var duplicate = router.TryRoute(target);

        Assert.Equal(ClientNotificationActivationRouteStatus.NoActiveAccount, rejected);
        Assert.Equal(ClientNotificationActivationRouteStatus.Duplicate, duplicate);
        Assert.Equal([target], navigated);
    }

    [Fact]
    public void TryRoute_WhenAccountDiffers_RejectsWithoutCheckingConversation()
    {
        var accessCalls = 0;
        using var router = CreateRouter(_ => throw new InvalidOperationException());
        using var account = router.ActivateAccount(
            AccountScopeId,
            _ =>
            {
                accessCalls++;
                return true;
            });

        var result = router.TryRoute(ClientNotificationActivationTarget.Message(
            OtherAccountScopeId,
            ConversationId,
            1));

        Assert.Equal(ClientNotificationActivationRouteStatus.AccountMismatch, result);
        Assert.Equal(0, accessCalls);
    }

    [Theory]
    [InlineData(LocalCacheOperationStatus.UnknownConversation)]
    [InlineData(LocalCacheOperationStatus.RevokedConversation)]
    [InlineData(LocalCacheOperationStatus.FatalScope)]
    [InlineData(LocalCacheOperationStatus.AuthoritativeSnapshotRequired)]
    [InlineData(LocalCacheOperationStatus.TransientFailure)]
    public void TryRoute_WhenMessageAccessIsNotReady_FailsClosed(
        LocalCacheOperationStatus status)
    {
        var sinkCalls = 0;
        using var router = CreateRouter(_ => sinkCalls++);
        using var account = router.ActivateAccount(
            AccountScopeId,
            _ => status == LocalCacheOperationStatus.Ready);

        var result = router.TryRoute(ClientNotificationActivationTarget.Message(
            AccountScopeId,
            ConversationId,
            1));

        Assert.Equal(ClientNotificationActivationRouteStatus.AccessDenied, result);
        Assert.Equal(0, sinkCalls);
    }

    [Fact]
    public void TryRoute_WhenUnreadOverviewAccessIsDenied_FailsClosed()
    {
        var sinkCalls = 0;
        var accessCalls = 0;
        using var router = CreateRouter(_ => sinkCalls++);
        using var account = router.ActivateAccount(
            AccountScopeId,
            _ =>
            {
                accessCalls++;
                return false;
            });

        var result = router.TryRoute(
            ClientNotificationActivationTarget.UnreadOverview(AccountScopeId));

        Assert.Equal(ClientNotificationActivationRouteStatus.AccessDenied, result);
        Assert.Equal(0, sinkCalls);
        Assert.Equal(1, accessCalls);
    }

    [Fact]
    public async Task TryRoute_WithRealCache_RejectsUnknownAndRevokedButAcceptsAuthorized()
    {
        var identity = AccountScopeIdentity.Create(
            new Uri("https://relaycove.example/team/"),
            Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            rootDirectory);
        await using var cache = await AccountScopedLocalCache.CreateAsync(
            identity,
            NullLogger<AccountScopedLocalCache>.Instance);
        var conversation = new ConversationDto(
            ConversationId,
            ConversationType.PrivateChannel,
            "Private",
            null,
            DateTimeOffset.Parse("2026-08-04T00:00:00Z"),
            DateTimeOffset.Parse("2026-08-04T00:00:00Z"),
            3,
            0,
            3);
        var sinkCalls = 0;
        using var router = CreateRouter(_ => sinkCalls++);
        using var account = router.ActivateAccount(
            identity.Id,
            target => target.Kind switch
            {
                ClientNotificationActivationKind.Message =>
                    cache.GetNotificationConversationAccessStatus(
                        target.ConversationId!.Value) == LocalCacheOperationStatus.Ready,
                ClientNotificationActivationKind.UnreadOverview =>
                    cache.GetNotificationOverviewAccessStatus() ==
                        LocalCacheOperationStatus.Ready,
                _ => false,
            });

        var unknown = router.TryRoute(ClientNotificationActivationTarget.Message(
            identity.Id,
            ConversationId,
            1));
        Assert.Equal(
            LocalCacheOperationStatus.Ready,
            await cache.ApplyAuthoritativeConversationSnapshotAsync(
                new ConversationListResponse([conversation], Complete: true)));
        var authorized = router.TryRoute(ClientNotificationActivationTarget.Message(
            identity.Id,
            ConversationId,
            2));
        Assert.Equal(
            LocalCacheOperationStatus.RevokedConversation,
            await cache.RevokeConversationAccessAsync(ConversationId));
        var revoked = router.TryRoute(ClientNotificationActivationTarget.Message(
            identity.Id,
            ConversationId,
            3));

        Assert.Equal(ClientNotificationActivationRouteStatus.AccessDenied, unknown);
        Assert.Equal(ClientNotificationActivationRouteStatus.Accepted, authorized);
        Assert.Equal(ClientNotificationActivationRouteStatus.AccessDenied, revoked);
        Assert.Equal(1, sinkCalls);
    }

    [Fact]
    public async Task NotificationOverview_WithRealCache_RequiresAuthoritativeSnapshot()
    {
        var identity = AccountScopeIdentity.Create(
            new Uri("https://relaycove.example/team/"),
            Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            rootDirectory);
        await using var cache = await AccountScopedLocalCache.CreateAsync(
            identity,
            NullLogger<AccountScopedLocalCache>.Instance);

        Assert.Equal(
            LocalCacheOperationStatus.AuthoritativeSnapshotRequired,
            cache.GetNotificationOverviewAccessStatus());
        Assert.Equal(
            LocalCacheOperationStatus.Ready,
            await cache.ApplyAuthoritativeConversationSnapshotAsync(
                new ConversationListResponse([], Complete: true)));
        Assert.Equal(
            LocalCacheOperationStatus.Ready,
            cache.GetNotificationOverviewAccessStatus());
    }

    [Fact]
    public void TryRoute_WhenSuccessful_DeduplicatesOnlyExactCompletedTarget()
    {
        var navigated = new List<ClientNotificationActivationTarget>();
        using var router = CreateRouter(navigated.Add);
        using var account = router.ActivateAccount(
            AccountScopeId,
            _ => true);
        var first = ClientNotificationActivationTarget.Message(
            AccountScopeId,
            ConversationId,
            1);
        var second = ClientNotificationActivationTarget.Message(
            AccountScopeId,
            ConversationId,
            2);

        Assert.Equal(ClientNotificationActivationRouteStatus.Accepted, router.TryRoute(first));
        Assert.Equal(ClientNotificationActivationRouteStatus.Duplicate, router.TryRoute(first));
        Assert.Equal(ClientNotificationActivationRouteStatus.Accepted, router.TryRoute(second));

        Assert.Equal([first, second], navigated);
    }

    [Fact]
    public void TryRoute_WhenDuplicate_ReactivatesWindowWithoutNavigatingAgain()
    {
        var navigationCalls = 0;
        var windowCalls = 0;
        using var router = CreateRouter(
            _ => navigationCalls++,
            windowActivationSink: () => windowCalls++);
        using var account = router.ActivateAccount(AccountScopeId, _ => true);
        var target = ClientNotificationActivationTarget.Message(
            AccountScopeId,
            ConversationId,
            1);

        Assert.Equal(ClientNotificationActivationRouteStatus.Accepted, router.TryRoute(target));
        Assert.Equal(ClientNotificationActivationRouteStatus.Duplicate, router.TryRoute(target));
        Assert.Equal(1, navigationCalls);
        Assert.Equal(2, windowCalls);
    }

    [Fact]
    public void TryRoute_WhenDedupeTtlExpires_NavigatesAgain()
    {
        var timeProvider = new MutableTimeProvider(
            DateTimeOffset.Parse("2026-08-04T00:00:00Z"));
        var navigationCalls = 0;
        using var router = CreateRouter(
            _ => navigationCalls++,
            timeProvider: timeProvider,
            completedTargetTtl: TimeSpan.FromSeconds(5));
        using var account = router.ActivateAccount(AccountScopeId, _ => true);
        var target = ClientNotificationActivationTarget.Message(
            AccountScopeId,
            ConversationId,
            1);

        Assert.Equal(ClientNotificationActivationRouteStatus.Accepted, router.TryRoute(target));
        timeProvider.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(ClientNotificationActivationRouteStatus.Accepted, router.TryRoute(target));
        Assert.Equal(2, navigationCalls);
    }

    [Fact]
    public void ActivateAccount_WhenPendingTargetExpired_DoesNotReplayIt()
    {
        var timeProvider = new MutableTimeProvider(
            DateTimeOffset.Parse("2026-08-04T00:00:00Z"));
        var navigationCalls = 0;
        using var router = CreateRouter(
            _ => navigationCalls++,
            timeProvider: timeProvider,
            pendingTargetTtl: TimeSpan.FromSeconds(30));
        var target = ClientNotificationActivationTarget.UnreadOverview(AccountScopeId);

        Assert.Equal(
            ClientNotificationActivationRouteStatus.NoActiveAccount,
            router.TryRoute(target));
        timeProvider.Advance(TimeSpan.FromSeconds(30));
        using var account = router.ActivateAccount(AccountScopeId, _ => true);

        Assert.Equal(0, navigationCalls);
        Assert.Equal(ClientNotificationActivationRouteStatus.Accepted, router.TryRoute(target));
    }

    [Fact]
    public void ActivateAccount_WhenPendingReplayIsDenied_KeepsItForLaterRevalidation()
    {
        var authorized = false;
        var navigationCalls = 0;
        using var router = CreateRouter(_ => navigationCalls++);
        var target = ClientNotificationActivationTarget.UnreadOverview(AccountScopeId);
        Assert.Equal(
            ClientNotificationActivationRouteStatus.NoActiveAccount,
            router.TryRoute(target));

        using var firstAccount = router.ActivateAccount(
            AccountScopeId,
            _ => authorized);
        Assert.Equal(0, navigationCalls);
        authorized = true;
        using var readyAccount = router.ActivateAccount(
            AccountScopeId,
            _ => authorized);

        Assert.Equal(1, navigationCalls);
        Assert.Equal(
            ClientNotificationActivationRouteStatus.Duplicate,
            router.TryRoute(target));
    }

    [Fact]
    public void TryRoute_WhenAccessRecovers_RejectedTargetWasNotConsumed()
    {
        var authorized = false;
        var navigationCalls = 0;
        using var router = CreateRouter(_ => navigationCalls++);
        using var account = router.ActivateAccount(AccountScopeId, _ => authorized);
        var target = ClientNotificationActivationTarget.UnreadOverview(AccountScopeId);

        Assert.Equal(ClientNotificationActivationRouteStatus.AccessDenied, router.TryRoute(target));
        authorized = true;
        Assert.Equal(ClientNotificationActivationRouteStatus.Accepted, router.TryRoute(target));
        Assert.Equal(1, navigationCalls);
    }

    [Fact]
    public void TryRoute_WhenNavigationFails_DoesNotConsumeFutureRetryOrLeakExceptionMessage()
    {
        var calls = 0;
        var logger = new RecordingLogger<ClientNotificationActivationRouter>();
        using var router = CreateRouter(
            _ =>
            {
                if (calls++ == 0)
                {
                    throw new InvalidOperationException("secret navigation");
                }
            },
            logger);
        using var account = router.ActivateAccount(
            AccountScopeId,
            _ => true);
        var target = ClientNotificationActivationTarget.Message(
            AccountScopeId,
            ConversationId,
            1);

        var failed = router.TryRoute(target);
        var retried = router.TryRoute(target);

        Assert.Equal(ClientNotificationActivationRouteStatus.NavigationFailed, failed);
        Assert.Equal(ClientNotificationActivationRouteStatus.Accepted, retried);
        Assert.Equal(2, calls);
        Assert.DoesNotContain(
            logger.Entries,
            entry => entry.Contains("secret navigation", StringComparison.Ordinal));
    }

    [Fact]
    public void ActivateAccount_WhenOldLeaseDisposes_DoesNotClearNewAccount()
    {
        using var router = CreateRouter(_ => { });
        var oldAccount = router.ActivateAccount(
            OtherAccountScopeId,
            _ => true);
        using var currentAccount = router.ActivateAccount(
            AccountScopeId,
            _ => true);

        oldAccount.Dispose();
        var result = router.TryRoute(
            ClientNotificationActivationTarget.UnreadOverview(AccountScopeId));

        Assert.Equal(ClientNotificationActivationRouteStatus.Accepted, result);
    }

    [Fact]
    public void ActivateAccount_WhenNewLifecycleStarts_DoesNotReuseOldDedupeState()
    {
        var sinkCalls = 0;
        using var router = CreateRouter(_ => sinkCalls++);
        var target = ClientNotificationActivationTarget.Message(
            AccountScopeId,
            ConversationId,
            1);
        using (router.ActivateAccount(
                   AccountScopeId,
                   _ => true))
        {
            Assert.Equal(
                ClientNotificationActivationRouteStatus.Accepted,
                router.TryRoute(target));
            Assert.Equal(
                ClientNotificationActivationRouteStatus.Duplicate,
                router.TryRoute(target));
        }

        using var nextLifecycle = router.ActivateAccount(
            AccountScopeId,
            _ => true);

        Assert.Equal(
            ClientNotificationActivationRouteStatus.Accepted,
            router.TryRoute(target));
        Assert.Equal(2, sinkCalls);
    }

    [Fact]
    public async Task TryRoute_WhenExactTargetIsConcurrent_NavigatesOnce()
    {
        var sinkCalls = 0;
        using var router = CreateRouter(_ => Interlocked.Increment(ref sinkCalls));
        using var account = router.ActivateAccount(
            AccountScopeId,
            _ => true);
        var target = ClientNotificationActivationTarget.Message(
            AccountScopeId,
            ConversationId,
            1);

        var results = await Task.WhenAll(
            Enumerable.Range(0, 100).Select(_ => Task.Run(() => router.TryRoute(target))));

        Assert.Equal(1, results.Count(
            result => result == ClientNotificationActivationRouteStatus.Accepted));
        Assert.Equal(99, results.Count(
            result => result == ClientNotificationActivationRouteStatus.Duplicate));
        Assert.Equal(1, sinkCalls);
    }

    [Fact]
    public void TryRoute_WhenBoundIsExceeded_EvictsOldestCompletedIdentity()
    {
        var sinkCalls = 0;
        using var router = CreateRouter(_ => sinkCalls++, completedTargetLimit: 2);
        using var account = router.ActivateAccount(
            AccountScopeId,
            _ => true);
        var first = ClientNotificationActivationTarget.Message(
            AccountScopeId,
            ConversationId,
            1);

        Assert.Equal(ClientNotificationActivationRouteStatus.Accepted, router.TryRoute(first));
        Assert.Equal(
            ClientNotificationActivationRouteStatus.Accepted,
            router.TryRoute(ClientNotificationActivationTarget.Message(
                AccountScopeId,
                ConversationId,
                2)));
        Assert.Equal(
            ClientNotificationActivationRouteStatus.Accepted,
            router.TryRoute(ClientNotificationActivationTarget.Message(
                AccountScopeId,
                ConversationId,
                3)));
        Assert.Equal(ClientNotificationActivationRouteStatus.Accepted, router.TryRoute(first));
        Assert.Equal(4, sinkCalls);
    }

    [Fact]
    public void Dispatcher_WhenRawAndTypedNotificationRepeat_RoutesSuccessfulTargetOnce()
    {
        var target = ClientNotificationActivationTarget.Message(
            AccountScopeId,
            ConversationId,
            1);
        var navigated = new List<ClientNotificationActivationTarget>();
        using var router = CreateRouter(navigated.Add);
        using var account = router.ActivateAccount(
            AccountScopeId,
            _ => true);
        using var dispatcher = new ClientActivationDispatcher(
            router,
            () => { },
            new RecordingLogger<ClientActivationDispatcher>());

        dispatcher.Dispatch(WindowsProcessActivation.AppNotification(
            WindowsNotificationActivationCodec.EncodeToArgument(target)));
        var duplicate = dispatcher.Dispatch(target);

        Assert.Equal(ClientNotificationActivationRouteStatus.Duplicate, duplicate);
        Assert.Equal([target], navigated);
    }

    [Fact]
    public void Dispatcher_WhenLaunchRepeats_InvokesIdempotentWindowSinkEachTime()
    {
        var launchCalls = 0;
        using var router = CreateRouter(_ => { });
        using var dispatcher = new ClientActivationDispatcher(
            router,
            () => launchCalls++,
            new RecordingLogger<ClientActivationDispatcher>());

        dispatcher.Dispatch(WindowsProcessActivation.Launch());
        dispatcher.Dispatch(WindowsProcessActivation.Launch());

        Assert.Equal(2, launchCalls);
    }

    [Fact]
    public void Dispatcher_WhenArgumentIsInvalid_RejectsWithoutLoggingPayload()
    {
        var logger = new RecordingLogger<ClientActivationDispatcher>();
        using var router = CreateRouter(_ => throw new InvalidOperationException());
        using var dispatcher = new ClientActivationDispatcher(router, () => { }, logger);

        dispatcher.Dispatch(WindowsProcessActivation.AppNotification("secret-invalid"));

        Assert.DoesNotContain(
            logger.Entries,
            entry => entry.Contains("secret-invalid", StringComparison.Ordinal));
    }

    private static ClientNotificationActivationRouter CreateRouter(
        Action<ClientNotificationActivationTarget> sink,
        RecordingLogger<ClientNotificationActivationRouter>? logger = null,
        int completedTargetLimit = 64,
        Action? windowActivationSink = null,
        TimeProvider? timeProvider = null,
        TimeSpan? completedTargetTtl = null,
        TimeSpan? pendingTargetTtl = null) =>
        new(
            sink,
            logger ?? new RecordingLogger<ClientNotificationActivationRouter>(),
            completedTargetLimit,
            windowActivationSink,
            timeProvider,
            completedTargetTtl,
            pendingTargetTtl);

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

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;

        public void Advance(TimeSpan delta) => utcNow += delta;
    }
}
