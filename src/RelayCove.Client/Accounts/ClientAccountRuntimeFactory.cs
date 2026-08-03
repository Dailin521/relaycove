using System.Net.Http;
using Microsoft.Extensions.Logging;
using RelayCove.Client.Auth;
using RelayCove.Client.Realtime;
using RelayCove.Client.Storage;
using RelayCove.Client.Sync;
using RelayCove.Shared.Messages;

namespace RelayCove.Client.Accounts;

internal sealed class ClientAccountRuntimeFactory
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

    public ClientAccountRuntimeFactory(
        HttpClient httpClient,
        string accountDataRootDirectory,
        ILoggerFactory loggerFactory)
        : this(
            httpClient,
            accountDataRootDirectory,
            loggerFactory,
            createRealtimeConnection: null)
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
            IClientAccountRealtimeConnection>? createRealtimeConnection)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        ArgumentException.ThrowIfNullOrWhiteSpace(accountDataRootDirectory);
        this.accountDataRootDirectory = accountDataRootDirectory;
        this.loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        this.createRealtimeConnection = createRealtimeConnection ??
            CreateDefaultRealtimeConnection;
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
        ClientSyncCoordinator? syncCoordinator = null;
        IClientAccountRealtimeConnection? realtimeConnection = null;
        try
        {
            var activityState = new ClientActivityState();
            cache = await AccountScopedLocalCache.CreateAsync(
                    identity,
                    loggerFactory.CreateLogger<AccountScopedLocalCache>(),
                    cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            syncCoordinator = new ClientSyncCoordinator(
                identity,
                httpClient,
                authenticationSession,
                cache,
                loggerFactory.CreateLogger<ClientSyncCoordinator>(),
                activityState.GetForegroundConversationId);
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
                loggerFactory.CreateLogger<LocalCacheRealtimeEventSink>());
            var realtimeSink = new ClientAccountRealtimeEventSink(cacheSink, syncRequestor);
            realtimeConnection = createRealtimeConnection(
                identity.CanonicalServerBaseUri,
                async () => await authenticationSession.GetAccessTokenAsync()
                    .ConfigureAwait(false),
                realtimeSink,
                loggerFactory.CreateLogger<ClientRealtimeConnection>());

            return new ClientAccountRuntime(
                identity,
                authenticationSession,
                realtimeConnection,
                syncCoordinator,
                cache,
                activityState,
                loggerFactory.CreateLogger<ClientAccountRuntime>());
        }
        catch (Exception creationFailure)
        {
            var cleanupFailures = new List<Exception>();
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
