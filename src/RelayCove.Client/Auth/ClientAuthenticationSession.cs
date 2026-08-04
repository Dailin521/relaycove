using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RelayCove.Client.Sync;
using RelayCove.Shared.Auth;

namespace RelayCove.Client.Auth;

public sealed class ClientAuthenticationSession : IClientAuthenticationSession, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object stateGate = new();
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private readonly TaskCompletionSource disposeCompletion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Uri refreshUri;
    private readonly Uri logoutUri;
    private readonly HttpClient httpClient;
    private readonly ILogger logger;
    private readonly TimeProvider timeProvider;
    private SessionState? state;
    private TaskCompletionSource<bool>? activeRefresh;
    private ClientCredentialStore? credentialStore;
    private bool credentialPersisted;
    private int disposeStarted;

    internal ClientAuthenticationSession(
        Uri serverBaseUri,
        HttpClient httpClient,
        ILogger logger,
        LoginResponse loginResponse,
        TimeProvider timeProvider)
    {
        ServerBaseUri = serverBaseUri ?? throw new ArgumentNullException(nameof(serverBaseUri));
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        ArgumentNullException.ThrowIfNull(loginResponse);
        state = SessionState.From(loginResponse);
        refreshUri = new Uri(ServerBaseUri, "api/auth/refresh");
        logoutUri = new Uri(ServerBaseUri, "api/auth/logout");
    }

    public Uri ServerBaseUri { get; }

    public bool IsAuthenticated
    {
        get
        {
            lock (stateGate)
            {
                return state is not null && Volatile.Read(ref disposeStarted) == 0;
            }
        }
    }

    public Guid? UserId
    {
        get
        {
            lock (stateGate)
            {
                return state?.UserId;
            }
        }
    }

    public string? DisplayName
    {
        get
        {
            lock (stateGate)
            {
                return state?.DisplayName;
            }
        }
    }

    public DateTimeOffset? ExpiresAt
    {
        get
        {
            lock (stateGate)
            {
                return state?.ExpiresAt;
            }
        }
    }

    public string? ServerVersion
    {
        get
        {
            lock (stateGate)
            {
                return state?.ServerVersion;
            }
        }
    }

    public string? MinimumSupportedClientVersion
    {
        get
        {
            lock (stateGate)
            {
                return state?.MinimumSupportedClientVersion;
            }
        }
    }

    public long AccessTokenVersion
    {
        get
        {
            lock (stateGate)
            {
                return state?.AccessTokenVersion ?? 0;
            }
        }
    }

    public bool IsCredentialPersisted
    {
        get
        {
            lock (stateGate)
            {
                return state is not null &&
                    credentialStore is not null &&
                    credentialPersisted &&
                    Volatile.Read(ref disposeStarted) == 0;
            }
        }
    }

    internal bool IsDisposeCompleted => disposeCompletion.Task.IsCompleted;

    public override string ToString() =>
        $"{nameof(ClientAuthenticationSession)} {{ IsAuthenticated = {IsAuthenticated}, " +
        "UserId = [REDACTED], DisplayName = [REDACTED], ServerBaseUri = [REDACTED], " +
        "AccessToken = [REDACTED], RefreshToken = [REDACTED] }";

    internal async Task<bool> AttachCredentialStoreAsync(ClientCredentialStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        SessionState current;
        lock (stateGate)
        {
            if (Volatile.Read(ref disposeStarted) != 0 || state is null)
            {
                return false;
            }

            if (credentialStore is not null && !ReferenceEquals(credentialStore, store))
            {
                throw new InvalidOperationException(
                    "The authentication session already has a different credential store.");
            }

            current = state;
        }

        var persisted = await store.SaveAsync(
                ServerBaseUri,
                current.UserId,
                current.RefreshToken,
                CancellationToken.None)
            .ConfigureAwait(false);
        if (!persisted)
        {
            _ = await store.ClearAsync(CancellationToken.None).ConfigureAwait(false);
        }

        lock (stateGate)
        {
            if (Volatile.Read(ref disposeStarted) != 0 || state is null)
            {
                return false;
            }

            credentialStore = store;
            credentialPersisted = persisted;
            return persisted;
        }
    }

    public ValueTask<string?> GetAccessTokenAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (stateGate)
        {
            return ValueTask.FromResult(
                Volatile.Read(ref disposeStarted) == 0 ? state?.AccessToken : null);
        }
    }

    public Task<bool> TryRefreshAccessTokenAsync(
        string rejectedAccessToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rejectedAccessToken);
        cancellationToken.ThrowIfCancellationRequested();

        Task<bool> sharedRefresh;
        TaskCompletionSource<bool>? refreshToStart = null;
        lock (stateGate)
        {
            if (Volatile.Read(ref disposeStarted) != 0 || state is null)
            {
                return Task.FromResult(false);
            }

            if (!string.Equals(
                    state.AccessToken,
                    rejectedAccessToken,
                    StringComparison.Ordinal))
            {
                return Task.FromResult(true);
            }

            if (activeRefresh is null)
            {
                refreshToStart = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                activeRefresh = refreshToStart;
            }

            sharedRefresh = activeRefresh.Task;
        }

        if (refreshToStart is not null)
        {
            _ = ExecuteRefreshAsync(rejectedAccessToken, refreshToStart);
        }

        return cancellationToken.CanBeCanceled
            ? sharedRefresh.WaitAsync(cancellationToken)
            : sharedRefresh;
    }

    public async Task<ClientLogoutStatus> LogoutAsync(
        CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref disposeStarted) != 0)
        {
            return ClientLogoutStatus.LoggedOut;
        }

        await operationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        var credentialCleared = true;
        try
        {
            string? refreshToken;
            ClientCredentialStore? store;
            lock (stateGate)
            {
                refreshToken = state?.RefreshToken;
                store = credentialStore;
                state = null;
                credentialPersisted = false;
            }

            credentialCleared = store is null ||
                await store.ClearAsync(CancellationToken.None).ConfigureAwait(false);
            if (refreshToken is null)
            {
                return credentialCleared
                    ? ClientLogoutStatus.LoggedOut
                    : ClientLogoutStatus.CredentialClearFailed;
            }

            ClientLogoutStatus remoteStatus;
            if (credentialCleared)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    lifetimeCancellation.Token);
                remoteStatus = await SendLogoutAsync(refreshToken, linkedCancellation.Token)
                    .ConfigureAwait(false);
            }
            else
            {
                remoteStatus = await SendLogoutAsync(
                        refreshToken,
                        lifetimeCancellation.Token)
                    .ConfigureAwait(false);
            }

            if (!credentialCleared && remoteStatus != ClientLogoutStatus.LoggedOut)
            {
                logger.LogWarning(
                    "Remote logout did not complete after credential cleanup failed; " +
                    "remoteStatus={RemoteStatus}.",
                    remoteStatus);
            }

            return credentialCleared
                ? remoteStatus
                : ClientLogoutStatus.CredentialClearFailed;
        }
        catch (OperationCanceledException)
        {
            if (!credentialCleared)
            {
                logger.LogWarning(
                    "Remote logout was canceled after credential cleanup failed.");
                return ClientLogoutStatus.CredentialClearFailed;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return ClientLogoutStatus.ServiceUnavailable;
        }
        finally
        {
            operationGate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposeStarted, 1) == 0)
        {
            _ = DisposeCoreAsync();
        }

        return new ValueTask(disposeCompletion.Task);
    }

    private async Task ExecuteRefreshAsync(
        string rejectedAccessToken,
        TaskCompletionSource<bool> completion)
    {
        var refreshed = false;
        try
        {
            await operationGate.WaitAsync(lifetimeCancellation.Token).ConfigureAwait(false);
            try
            {
                SessionState? expected;
                ClientCredentialStore? persistenceStore;
                lock (stateGate)
                {
                    expected = state;
                    persistenceStore = credentialStore;
                    if (Volatile.Read(ref disposeStarted) != 0 || expected is null)
                    {
                        return;
                    }

                    if (!string.Equals(
                            expected.AccessToken,
                            rejectedAccessToken,
                            StringComparison.Ordinal))
                    {
                        refreshed = true;
                        return;
                    }
                }

                var response = await SendRefreshAsync(
                        expected.RefreshToken,
                        lifetimeCancellation.Token)
                    .ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    await ClearIfCurrentAsync(rejectedAccessToken).ConfigureAwait(false);
                    return;
                }

                if (!response.IsSuccessStatusCode)
                {
                    return;
                }

                if (response.LoginResponse is null ||
                    response.LoginResponse.UserId != expected.UserId ||
                    !ClientAuthenticationResponseValidator.IsValid(
                        response.LoginResponse,
                        timeProvider.GetUtcNow()))
                {
                    await ClearIfCurrentAsync(rejectedAccessToken).ConfigureAwait(false);
                    return;
                }

                var persisted = false;
                if (persistenceStore is not null)
                {
                    persisted = await persistenceStore.SaveAsync(
                            ServerBaseUri,
                            expected.UserId,
                            response.LoginResponse.RefreshToken,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    if (!persisted)
                    {
                        _ = await persistenceStore
                            .ClearAsync(CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                }

                lock (stateGate)
                {
                    if (Volatile.Read(ref disposeStarted) != 0 || state is null)
                    {
                        return;
                    }

                    if (!string.Equals(
                            state.AccessToken,
                            rejectedAccessToken,
                            StringComparison.Ordinal))
                    {
                        refreshed = true;
                        return;
                    }

                    state = SessionState.From(response.LoginResponse);
                    credentialPersisted = persistenceStore is not null && persisted;
                    refreshed = true;
                }
            }
            finally
            {
                operationGate.Release();
            }
        }
        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested)
        {
            refreshed = false;
        }
        catch (OperationCanceledException)
        {
            refreshed = false;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            logger.LogWarning(
                "Authentication HTTP request failed; operation={Operation}; errorType={ErrorType}.",
                "Refresh",
                exception.GetType().Name);
        }
        finally
        {
            lock (stateGate)
            {
                completion.TrySetResult(refreshed);
                if (ReferenceEquals(activeRefresh, completion))
                {
                    activeRefresh = null;
                }
            }
        }
    }

    private async Task<RefreshHttpResult> SendRefreshAsync(
        string refreshToken,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, refreshUri)
        {
            Content = JsonContent.Create(
                new RefreshTokenRequest(refreshToken),
                options: JsonOptions),
        };
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await httpClient.SendAsync(
                message,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return new RefreshHttpResult(response.StatusCode, loginResponse: null);
        }

        try
        {
            var loginResponse = await response.Content
                .ReadFromJsonAsync<LoginResponse>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            return new RefreshHttpResult(response.StatusCode, loginResponse);
        }
        catch (Exception exception) when (
            exception is JsonException or NotSupportedException or InvalidOperationException)
        {
            return new RefreshHttpResult(response.StatusCode, loginResponse: null);
        }
        catch (OperationCanceledException)
        {
            return new RefreshHttpResult(response.StatusCode, loginResponse: null);
        }
    }

    private async Task<ClientLogoutStatus> SendLogoutAsync(
        string refreshToken,
        CancellationToken cancellationToken)
    {
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, logoutUri)
            {
                Content = JsonContent.Create(
                    new LogoutRequest(refreshToken),
                    options: JsonOptions),
            };
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await httpClient.SendAsync(
                    message,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return ClientLogoutStatus.LoggedOut;
            }

            return response.StatusCode == HttpStatusCode.RequestTimeout ||
                response.StatusCode == HttpStatusCode.TooManyRequests ||
                response.StatusCode >= HttpStatusCode.InternalServerError
                ? ClientLogoutStatus.ServiceUnavailable
                : ClientLogoutStatus.RemoteFailure;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            logger.LogWarning(
                "Authentication HTTP request failed; operation={Operation}; errorType={ErrorType}.",
                "Logout",
                exception.GetType().Name);
            return ClientLogoutStatus.ServiceUnavailable;
        }
    }

    private async Task ClearIfCurrentAsync(string rejectedAccessToken)
    {
        ClientCredentialStore? store = null;
        lock (stateGate)
        {
            if (state is not null &&
                string.Equals(
                    state.AccessToken,
                    rejectedAccessToken,
                    StringComparison.Ordinal))
            {
                state = null;
                credentialPersisted = false;
                store = credentialStore;
            }
        }

        if (store is not null)
        {
            _ = await store.ClearAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task DisposeCoreAsync()
    {
        try
        {
            lifetimeCancellation.Cancel();
            Task? refreshTask;
            lock (stateGate)
            {
                refreshTask = activeRefresh?.Task;
            }

            if (refreshTask is not null)
            {
                await refreshTask.ConfigureAwait(false);
            }

            await operationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                lock (stateGate)
                {
                    state = null;
                }
            }
            finally
            {
                operationGate.Release();
            }

            disposeCompletion.TrySetResult();
        }
        catch (Exception exception)
        {
            disposeCompletion.TrySetException(exception);
        }
    }

    private sealed class SessionState
    {
        private SessionState(LoginResponse response)
        {
            UserId = response.UserId;
            DisplayName = response.DisplayName;
            AccessToken = response.AccessToken;
            RefreshToken = response.RefreshToken;
            ExpiresAt = response.ExpiresAt;
            ServerVersion = response.ServerVersion;
            MinimumSupportedClientVersion = response.MinimumSupportedClientVersion;
            AccessTokenVersion = response.AccessTokenVersion;
        }

        public Guid UserId { get; }

        public string DisplayName { get; }

        public string AccessToken { get; }

        public string RefreshToken { get; }

        public DateTimeOffset ExpiresAt { get; }

        public string ServerVersion { get; }

        public string MinimumSupportedClientVersion { get; }

        public long AccessTokenVersion { get; }

        public static SessionState From(LoginResponse response) =>
            new(response);

        public override string ToString() =>
            $"{nameof(SessionState)} {{ UserId = [REDACTED], DisplayName = [REDACTED], " +
            "AccessToken = [REDACTED], RefreshToken = [REDACTED] }";
    }

    private sealed class RefreshHttpResult
    {
        public RefreshHttpResult(
            HttpStatusCode statusCode,
            LoginResponse? loginResponse)
        {
            StatusCode = statusCode;
            LoginResponse = loginResponse;
        }

        public HttpStatusCode StatusCode { get; }

        public LoginResponse? LoginResponse { get; }

        public bool IsSuccessStatusCode =>
            (int)StatusCode >= 200 && (int)StatusCode <= 299;

        public override string ToString() =>
            $"{nameof(RefreshHttpResult)} {{ StatusCode = {StatusCode}, " +
            "LoginResponse = [REDACTED] }";
    }
}
