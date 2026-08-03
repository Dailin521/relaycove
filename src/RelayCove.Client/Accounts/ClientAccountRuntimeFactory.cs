using System.Net.Http;
using Microsoft.Extensions.Logging;
using RelayCove.Client.Auth;
using RelayCove.Client.Notifications;
using RelayCove.Client.Realtime;
using RelayCove.Client.Storage;
using RelayCove.Client.Sync;
using RelayCove.Shared.Messages;

namespace RelayCove.Client.Accounts;

internal sealed class ClientAccountRuntimeFactory : IClientAccountRuntimeFactory
{
    private readonly HttpClient httpClient;
    private readonly string accountDataRootDirectory;
    private readonly ILoggerFactory loggerFactory;
    private readonly Func<
        Uri,
        Func<Task<string?>>,
        IRealtimeEventSink,
        ILogger<ClientRealtimeConnection>,
        IClientAccountRealtimeConnection> createRealtimeConnection;
    private readonly IClientNotificationPlatform notificationPlatform;
    private readonly Func<ClientNotificationSettingsSnapshot> notificationSettingsProvider;
    private readonly IClientNotificationAttention notificationAttention;

    public ClientAccountRuntimeFactory(
        HttpClient httpClient,
        string accountDataRootDirectory,
        ILoggerFactory loggerFactory,
        IClientNotificationAttention? notificationAttention = null)
        : this(
            httpClient,
            accountDataRootDirectory,
            loggerFactory,
            createRealtimeConnection: null,
            notificationPlatform: null,
            notificationSettingsProvider: null,
            notificationAttention: notificationAttention)
    {
    }

    internal ClientAccountRuntimeFactory(
        HttpClient httpClient,
        string accountDataRootDirectory,
        ILoggerFactory loggerFactory,
        Func<
            Uri,
            Func<Task<string?>>,
            IRealtimeEventSink,
            ILogger<ClientRealtimeConnection>,
            IClientAccountRealtimeConnection>? createRealtimeConnection,
        IClientNotificationPlatform? notificationPlatform = null,
        Func<ClientNotificationSettingsSnapshot>? notificationSettingsProvider = null,
        IClientNotificationAttention? notificationAttention = null)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        ArgumentException.ThrowIfNullOrWhiteSpace(accountDataRootDirectory);
        this.accountDataRootDirectory = accountDataRootDirectory;
        this.loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        this.createRealtimeConnection = createRealtimeConnection ??
            CreateDefaultRealtimeConnection;
        this.notificationAttention = notificationAttention ??
            NoOpClientNotificationAttention.Instance;
        if (notificationPlatform is null)
        {
            var windowsPlatform = new WindowsClientNotificationPlatform(
                WindowsAppSdkNotificationManager.Shared,
                loggerFactory.CreateLogger<WindowsClientNotificationPlatform>());
            this.notificationPlatform = windowsPlatform;
            this.notificationSettingsProvider = notificationSettingsProvider ??
                windowsPlatform.GetSettingsSnapshot;
        }
        else
        {
            this.notificationPlatform = notificationPlatform;
            this.notificationSettingsProvider = notificationSettingsProvider ??
                (static () => ClientNotificationSettingsSnapshot.Unavailable);
        }
    }

    public async Task<ClientAccountRuntime> CreateAsync(
        ClientAuthenticationSession authenticationSession,
        CancellationToken cancellationToken = default)
    {
        // Ownership transfers to the returned runtime only after construction succeeds.
        // On failure the authenticated session remains owned by the caller.
        ArgumentNullException.ThrowIfNull(authenticationSession);
        cancellationToken.ThrowIfCancellationRequested();
        var userId = authenticationSession.UserId;
        if (!authenticationSession.IsAuthenticated ||
            !userId.HasValue ||
            userId.Value == Guid.Empty)
        {
            throw new InvalidOperationException(
                "An authenticated client session is required to create an account runtime.");
        }

        var identity = AccountScopeIdentity.Create(
            authenticationSession.ServerBaseUri,
            userId.Value,
            accountDataRootDirectory);
        AccountScopedLocalCache? cache = null;
        ClientReadThroughCoordinator? readThroughCoordinator = null;
        ClientMessageHistoryCoordinator? messageHistoryCoordinator = null;
        ClientMessageSendCoordinator? messageSendCoordinator = null;
        ClientAutomaticSyncScheduler? automaticSyncScheduler = null;
        ClientSyncCoordinator? syncCoordinator = null;
        IClientNotificationRoundCoordinator? unownedNotificationRoundCoordinator = null;
        IClientAccountRealtimeConnection? realtimeConnection = null;
        try
        {
            var activityState = new ClientActivityState();
            var stateHub = new ClientAccountRuntimeStateHub(
                loggerFactory.CreateLogger<ClientAccountRuntimeStateHub>());
            cache = await AccountScopedLocalCache.CreateAsync(
                    identity,
                    loggerFactory.CreateLogger<AccountScopedLocalCache>(),
                    cancellationToken)
                .ConfigureAwait(false);
            await cache.AdoptNotificationStateAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var notificationCoordinator = new ClientNotificationCoordinator(
                identity,
                cache,
                notificationPlatform,
                notificationSettingsProvider,
                activityState.GetForegroundConversationId,
                loggerFactory.CreateLogger<ClientNotificationCoordinator>(),
                notificationAttention);
            var notificationRoundCoordinator = new ClientNotificationRoundCoordinator(
                cache,
                notificationCoordinator,
                activityState,
                loggerFactory.CreateLogger<ClientNotificationRoundCoordinator>());
            unownedNotificationRoundCoordinator = notificationRoundCoordinator;
            readThroughCoordinator = new ClientReadThroughCoordinator(
                identity,
                httpClient,
                authenticationSession,
                cache,
                loggerFactory.CreateLogger<ClientReadThroughCoordinator>(),
                notificationRoundCoordinator.ConversationRevokedAsync);
            var readThroughRequestor = new ClientReadThroughRequestor(
                readThroughCoordinator,
                loggerFactory.CreateLogger<ClientReadThroughRequestor>());
            messageHistoryCoordinator = new ClientMessageHistoryCoordinator(
                identity,
                httpClient,
                authenticationSession,
                cache,
                loggerFactory.CreateLogger<ClientMessageHistoryCoordinator>(),
                notificationRoundCoordinator.ConversationRevokedAsync);
            messageSendCoordinator = new ClientMessageSendCoordinator(
                identity,
                authenticationSession.DisplayName ?? string.Empty,
                httpClient,
                authenticationSession,
                cache,
                loggerFactory.CreateLogger<ClientMessageSendCoordinator>(),
                notificationRoundCoordinator.ConversationRevokedAsync);
            syncCoordinator = new ClientSyncCoordinator(
                identity,
                httpClient,
                authenticationSession,
                cache,
                loggerFactory.CreateLogger<ClientSyncCoordinator>(),
                activityState.GetForegroundConversationId,
                readThroughRequestor.Request,
                notificationRoundCoordinator,
                ownsNotificationRoundCoordinator: false);
            var syncRequestor = new ClientAccountSyncRequestor(
                syncCoordinator,
                loggerFactory.CreateLogger<ClientAccountSyncRequestor>());
            var cacheSink = new LocalCacheRealtimeEventSink(
                cache,
                (_, _) =>
                {
                    syncRequestor.Request(SyncReason.Reconnect);
                    return Task.CompletedTask;
                },
                activityState.GetForegroundConversationId,
                loggerFactory.CreateLogger<LocalCacheRealtimeEventSink>(),
                readThroughRequestor.Request,
                notificationRoundCoordinator);
            cache.ConversationStateChanged += stateHub.PublishConversationStateChanged;
            var realtimeSink = new ClientAccountRealtimeEventSink(
                cacheSink,
                syncRequestor,
                stateHub.PublishConnectionState);
            realtimeConnection = createRealtimeConnection(
                identity.CanonicalServerBaseUri,
                async () => await authenticationSession.GetAccessTokenAsync()
                    .ConfigureAwait(false),
                realtimeSink,
                loggerFactory.CreateLogger<ClientRealtimeConnection>());
            automaticSyncScheduler = new ClientAutomaticSyncScheduler(
                syncCoordinator,
                loggerFactory.CreateLogger<ClientAutomaticSyncScheduler>());

            var runtime = new ClientAccountRuntime(
                identity,
                authenticationSession,
                realtimeConnection,
                syncCoordinator,
                readThroughCoordinator,
                notificationRoundCoordinator,
                cache,
                activityState,
                loggerFactory.CreateLogger<ClientAccountRuntime>(),
                automaticSyncScheduler,
                target => target.Kind switch
                {
                    ClientNotificationActivationKind.Message =>
                        cache.GetNotificationConversationAccessStatus(
                            target.ConversationId!.Value) == LocalCacheOperationStatus.Ready,
                    ClientNotificationActivationKind.UnreadOverview =>
                        cache.GetNotificationOverviewAccessStatus() ==
                            LocalCacheOperationStatus.Ready,
                    _ => false,
                },
                cache,
                stateHub,
                messageHistoryCoordinator,
                messageSendCoordinator);
            unownedNotificationRoundCoordinator = null;
            automaticSyncScheduler = null;
            return runtime;
        }
        catch (Exception creationFailure)
        {
            var cleanupFailures = new List<Exception>();
            if (automaticSyncScheduler is not null)
            {
                await CaptureCleanupFailureAsync(
                        () => automaticSyncScheduler.DisposeAsync(),
                        cleanupFailures)
                    .ConfigureAwait(false);
            }
            if (realtimeConnection is not null)
            {
                await CaptureCleanupFailureAsync(
                        () => realtimeConnection.DisposeAsync(),
                        cleanupFailures)
                    .ConfigureAwait(false);
            }

            if (syncCoordinator is not null)
            {
                await CaptureCleanupFailureAsync(
                        () => syncCoordinator.DisposeAsync(),
                        cleanupFailures)
                    .ConfigureAwait(false);
            }
            if (readThroughCoordinator is not null)
            {
                await CaptureCleanupFailureAsync(
                        () => readThroughCoordinator.DisposeAsync(),
                        cleanupFailures)
                    .ConfigureAwait(false);
            }
            if (messageHistoryCoordinator is not null)
            {
                await CaptureCleanupFailureAsync(
                        () => messageHistoryCoordinator.DisposeAsync(),
                        cleanupFailures)
                    .ConfigureAwait(false);
            }
            if (messageSendCoordinator is not null)
            {
                await CaptureCleanupFailureAsync(
                        () => messageSendCoordinator.DisposeAsync(),
                        cleanupFailures)
                    .ConfigureAwait(false);
            }

            if (unownedNotificationRoundCoordinator is not null)
            {
                await CaptureCleanupFailureAsync(
                        () => unownedNotificationRoundCoordinator.DisposeAsync(),
                        cleanupFailures)
                    .ConfigureAwait(false);
            }

            if (cache is not null)
            {
                await CaptureCleanupFailureAsync(
                        () => cache.DisposeAsync(),
                        cleanupFailures)
                    .ConfigureAwait(false);
            }

            if (cleanupFailures.Count != 0)
            {
                var failures = new List<Exception> { creationFailure };
                failures.AddRange(cleanupFailures);
                throw new AggregateException(
                    "Account runtime creation and cleanup failed.",
                    failures);
            }

            throw;
        }
    }

    async Task<IClientAccountRuntime> IClientAccountRuntimeFactory.CreateAsync(
        ClientAuthenticationSession authenticationSession,
        CancellationToken cancellationToken) =>
        await CreateAsync(authenticationSession, cancellationToken).ConfigureAwait(false);

    private static IClientAccountRealtimeConnection CreateDefaultRealtimeConnection(
        Uri serverBaseUri,
        Func<Task<string?>> accessTokenProvider,
        IRealtimeEventSink sink,
        ILogger<ClientRealtimeConnection> logger) =>
        new ClientRealtimeConnection(
            serverBaseUri,
            accessTokenProvider,
            sink,
            logger);

    private static async ValueTask CaptureCleanupFailureAsync(
        Func<ValueTask> cleanup,
        ICollection<Exception> failures)
    {
        try
        {
            await cleanup().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }
}
