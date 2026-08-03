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

    public override string ToString() =>
        $"{nameof(ClientAuthenticationSession)} {{ IsAuthenticated = {IsAuthenticated}, " +
        "UserId = [REDACTED], DisplayName = [REDACTED], ServerBaseUri = [REDACTED], " +
        "AccessToken = [REDACTED], RefreshToken = [REDACTED] }";

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
        try
        {
            string? refreshToken;
            lock (stateGate)
            {
                refreshToken = state?.RefreshToken;
                state = null;
            }

            if (refreshToken is null)
            {
                return ClientLogoutStatus.LoggedOut;
            }

            cancellationToken.ThrowIfCancellationRequested();
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                lifetimeCancellation.Token);
            return await SendLogoutAsync(refreshToken, linkedCancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
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
                lock (stateGate)
                {
                    expected = state;
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
                    ClearIfCurrent(rejectedAccessToken);
                    return;
                }

                if (!response.IsSuccessStatusCode || response.LoginResponse is null)
                {
                    return;
                }

                if (response.LoginResponse.UserId != expected.UserId)
                {
                    ClearIfCurrent(rejectedAccessToken);
                    return;
                }

                if (!ClientAuthenticationResponseValidator.IsValid(
                        response.LoginResponse,
                        timeProvider.GetUtcNow()))
                {
                    return;
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

    private void ClearIfCurrent(string rejectedAccessToken)
    {
        lock (stateGate)
        {
            if (state is not null &&
                string.Equals(
                    state.AccessToken,
                    rejectedAccessToken,
                    StringComparison.Ordinal))
            {
                state = null;
            }
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
        }

        public Guid UserId { get; }

        public string DisplayName { get; }

        public string AccessToken { get; }

        public string RefreshToken { get; }

        public DateTimeOffset ExpiresAt { get; }

        public string ServerVersion { get; }

        public string MinimumSupportedClientVersion { get; }

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
