using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using RelayCove.Client.Auth;
using RelayCove.Shared.Auth;

namespace RelayCove.Client.Tests.Auth;

public sealed class ClientAuthenticationClientTests
{
    private static readonly DateTimeOffset Now = new(
        2026,
        8,
        3,
        12,
        0,
        0,
        TimeSpan.Zero);
    private static readonly Guid UserId = Guid.Parse("9e7f5c51-5d3a-4a14-a5bf-e88b8afe7d7c");
    private const string UserName = "classified-user";
    private const string Password = "classified-password";
    private const string DisplayName = "Classified Display";
    private const string InitialAccessToken = "initial.access.token";
    private const string InitialRefreshToken = "initial-refresh-token";
    private const string RotatedAccessToken = "rotated.access.token";
    private const string RotatedRefreshToken = "rotated-refresh-token";

    [Fact]
    public async Task LoginAsync_WhenResponseIsValid_CreatesRedactedSessionAndUsesProxyPath()
    {
        LoginRequest? capturedRequest = null;
        var handler = new DelegateHttpHandler(async (request, cancellationToken) =>
        {
            Assert.Equal("/proxy/api/auth/login", request.RequestUri!.AbsolutePath);
            capturedRequest = await request.Content!.ReadFromJsonAsync<LoginRequest>(cancellationToken);
            return Ok(CreateLoginResponse());
        });
        var (client, logger) = CreateClient(handler);

        var outcome = await client.LoginAsync(CreateLoginRequest());

        Assert.Equal(ClientLoginStatus.Authenticated, outcome.Status);
        Assert.NotNull(outcome.Session);
        Assert.Equal(UserName, capturedRequest!.UserName);
        Assert.Equal(Password, capturedRequest.Password);
        Assert.Equal(new Uri("https://example.com/proxy/"), client.ServerBaseUri);
        Assert.Equal(UserId, outcome.Session.UserId);
        Assert.Equal(DisplayName, outcome.Session.DisplayName);
        Assert.Equal(Now.AddHours(1), outcome.Session.ExpiresAt);
        Assert.Equal(InitialAccessToken, await outcome.Session.GetAccessTokenAsync());

        var text = client + " " + outcome + " " + outcome.Session;
        Assert.DoesNotContain("example.com", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(UserId.ToString(), text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(DisplayName, text, StringComparison.Ordinal);
        Assert.DoesNotContain(InitialAccessToken, text, StringComparison.Ordinal);
        Assert.DoesNotContain(InitialRefreshToken, text, StringComparison.Ordinal);
        Assert.Empty(logger.Entries);
        await outcome.Session.DisposeAsync();
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, ClientLoginStatus.ValidationFailed)]
    [InlineData(HttpStatusCode.Unauthorized, ClientLoginStatus.AuthenticationFailed)]
    [InlineData(HttpStatusCode.RequestTimeout, ClientLoginStatus.ServiceUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError, ClientLoginStatus.ServiceUnavailable)]
    [InlineData(HttpStatusCode.BadGateway, ClientLoginStatus.ServiceUnavailable)]
    [InlineData(HttpStatusCode.Forbidden, ClientLoginStatus.RemoteFailure)]
    public async Task LoginAsync_WhenServerRejects_ClassifiesWithoutRetry(
        HttpStatusCode statusCode,
        ClientLoginStatus expectedStatus)
    {
        var handler = new DelegateHttpHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(statusCode)));
        var (client, _) = CreateClient(handler);

        var outcome = await client.LoginAsync(CreateLoginRequest());

        Assert.Equal(expectedStatus, outcome.Status);
        Assert.Null(outcome.Session);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task LoginAsync_WhenRateLimited_ReturnsRetryAfterWithoutRetry()
    {
        var handler = new DelegateHttpHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
                TimeSpan.FromSeconds(17));
            return Task.FromResult(response);
        });
        var (client, _) = CreateClient(handler);

        var outcome = await client.LoginAsync(CreateLoginRequest());

        Assert.Equal(ClientLoginStatus.RateLimited, outcome.Status);
        Assert.Equal(TimeSpan.FromSeconds(17), outcome.RetryAfter);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task LoginAsync_WhenTransportFails_DoesNotRetryOrLogSecrets()
    {
        var handler = new DelegateHttpHandler((_, _) =>
            throw new HttpRequestException("classified transport detail"));
        var (client, logger) = CreateClient(handler);

        var outcome = await client.LoginAsync(CreateLoginRequest());

        Assert.Equal(ClientLoginStatus.ServiceUnavailable, outcome.Status);
        Assert.Equal(1, handler.RequestCount);
        var logs = string.Join(' ', logger.Entries);
        Assert.DoesNotContain("classified transport detail", logs, StringComparison.Ordinal);
        Assert.DoesNotContain(UserName, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, logs, StringComparison.Ordinal);
        Assert.DoesNotContain("example.com", logs, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoginAsync_WhenSuccessPayloadIsMalformed_ReturnsProtocolError()
    {
        var handler = new DelegateHttpHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{not-json"),
            }));
        var (client, _) = CreateClient(handler);

        var outcome = await client.LoginAsync(CreateLoginRequest());

        Assert.Equal(ClientLoginStatus.ProtocolError, outcome.Status);
        Assert.Null(outcome.Session);
    }

    [Theory]
    [MemberData(nameof(InvalidLoginResponses))]
    public async Task LoginAsync_WhenSuccessPayloadViolatesContract_ReturnsProtocolError(
        LoginResponse invalidResponse)
    {
        var handler = new DelegateHttpHandler((_, _) => Task.FromResult(Ok(invalidResponse)));
        var (client, _) = CreateClient(handler);

        var outcome = await client.LoginAsync(CreateLoginRequest());

        Assert.Equal(ClientLoginStatus.ProtocolError, outcome.Status);
        Assert.Null(outcome.Session);
    }

    [Fact]
    public async Task TryRefreshAccessTokenAsync_WhenConcurrent_RotatesOnlyOnce()
    {
        var refreshEntered = NewSignal();
        var releaseRefresh = NewSignal();
        var capturedRefreshTokens = new ConcurrentQueue<string>();
        var handler = new DelegateHttpHandler(async (request, cancellationToken) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/login", StringComparison.Ordinal))
            {
                return Ok(CreateLoginResponse());
            }

            var body = await request.Content!.ReadFromJsonAsync<RefreshTokenRequest>(cancellationToken);
            capturedRefreshTokens.Enqueue(body!.RefreshToken);
            refreshEntered.TrySetResult();
            await releaseRefresh.Task.WaitAsync(cancellationToken);
            return Ok(CreateRotatedResponse());
        });
        var session = await LoginSessionAsync(handler);

        var refreshes = Enumerable.Range(0, 20)
            .Select(_ => session.TryRefreshAccessTokenAsync(InitialAccessToken))
            .ToArray();
        await refreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, handler.RequestCountFor("/refresh"));
        releaseRefresh.TrySetResult();

        Assert.All(await Task.WhenAll(refreshes), Assert.True);
        Assert.Equal(RotatedAccessToken, await session.GetAccessTokenAsync());
        Assert.Equal([InitialRefreshToken], capturedRefreshTokens);
        Assert.True(await session.TryRefreshAccessTokenAsync(InitialAccessToken));
        Assert.Equal(1, handler.RequestCountFor("/refresh"));
        await session.DisposeAsync();
    }

    [Fact]
    public async Task TryRefreshAccessTokenAsync_WhenOneWaiterCancels_KeepsSharedRefreshAlive()
    {
        var refreshEntered = NewSignal();
        var releaseRefresh = NewSignal();
        var handler = CreateRefreshBlockingHandler(refreshEntered, releaseRefresh);
        var session = await LoginSessionAsync(handler);
        using var callerCancellation = new CancellationTokenSource();

        var canceledWaiter = session.TryRefreshAccessTokenAsync(
            InitialAccessToken,
            callerCancellation.Token);
        var survivingWaiter = session.TryRefreshAccessTokenAsync(InitialAccessToken);
        await refreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        callerCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledWaiter);

        releaseRefresh.TrySetResult();
        Assert.True(await survivingWaiter);
        Assert.Equal(RotatedAccessToken, await session.GetAccessTokenAsync());
        Assert.Equal(1, handler.RequestCountFor("/refresh"));
        await session.DisposeAsync();
    }

    [Fact]
    public async Task TryRefreshAccessTokenAsync_WhenUnauthorized_ClearsSession()
    {
        var handler = CreateLoginThenResponseHandler(
            new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var session = await LoginSessionAsync(handler);

        var refreshed = await session.TryRefreshAccessTokenAsync(InitialAccessToken);

        Assert.False(refreshed);
        Assert.False(session.IsAuthenticated);
        Assert.Null(session.UserId);
        Assert.Null(await session.GetAccessTokenAsync());
        Assert.Equal(1, handler.RequestCountFor("/refresh"));
        await session.DisposeAsync();
    }

    [Fact]
    public async Task TryRefreshAccessTokenAsync_WhenUserChanges_ClearsSession()
    {
        var handler = CreateLoginThenResponseHandler(
            Ok(CreateRotatedResponse() with { UserId = Guid.NewGuid() }));
        var session = await LoginSessionAsync(handler);

        var refreshed = await session.TryRefreshAccessTokenAsync(InitialAccessToken);

        Assert.False(refreshed);
        Assert.False(session.IsAuthenticated);
        Assert.Null(await session.GetAccessTokenAsync());
        await session.DisposeAsync();
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task TryRefreshAccessTokenAsync_WhenResponseIsUncertain_KeepsCurrentStateWithoutRetry(
        HttpStatusCode statusCode)
    {
        var handler = CreateLoginThenResponseHandler(new HttpResponseMessage(statusCode));
        var session = await LoginSessionAsync(handler);

        var refreshed = await session.TryRefreshAccessTokenAsync(InitialAccessToken);

        Assert.False(refreshed);
        Assert.True(session.IsAuthenticated);
        Assert.Equal(InitialAccessToken, await session.GetAccessTokenAsync());
        Assert.Equal(1, handler.RequestCountFor("/refresh"));
        await session.DisposeAsync();
    }

    [Fact]
    public async Task TryRefreshAccessTokenAsync_WhenTransportFails_KeepsStateAndRedactsLog()
    {
        var logger = new RecordingLogger<ClientAuthenticationClient>();
        var handler = new DelegateHttpHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/login", StringComparison.Ordinal))
            {
                return Task.FromResult(Ok(CreateLoginResponse()));
            }

            throw new HttpRequestException("classified refresh failure");
        });
        var session = await LoginSessionAsync(handler, logger);

        var refreshed = await session.TryRefreshAccessTokenAsync(InitialAccessToken);

        Assert.False(refreshed);
        Assert.Equal(InitialAccessToken, await session.GetAccessTokenAsync());
        Assert.Equal(1, handler.RequestCountFor("/refresh"));
        var logs = string.Join(' ', logger.Entries);
        Assert.DoesNotContain("classified refresh failure", logs, StringComparison.Ordinal);
        Assert.DoesNotContain(InitialRefreshToken, logs, StringComparison.Ordinal);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task TryRefreshAccessTokenAsync_WhenSuccessPayloadIsMalformed_ClearsCurrentState()
    {
        var handler = CreateLoginThenResponseHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{not-json"),
            });
        var session = await LoginSessionAsync(handler);

        var refreshed = await session.TryRefreshAccessTokenAsync(InitialAccessToken);

        Assert.False(refreshed);
        Assert.False(session.IsAuthenticated);
        Assert.Null(await session.GetAccessTokenAsync());
        Assert.Equal(1, handler.RequestCountFor("/refresh"));
        await session.DisposeAsync();
    }

    [Fact]
    public async Task LogoutAsync_WhenRefreshIsRunning_UsesRotatedTokenAndClearsBeforeSend()
    {
        var refreshEntered = NewSignal();
        var releaseRefresh = NewSignal();
        var logoutEntered = NewSignal();
        ClientAuthenticationSession? session = null;
        string? logoutToken = null;
        var handler = new DelegateHttpHandler(async (request, cancellationToken) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/login", StringComparison.Ordinal))
            {
                return Ok(CreateLoginResponse());
            }

            if (request.RequestUri.AbsolutePath.EndsWith("/refresh", StringComparison.Ordinal))
            {
                refreshEntered.TrySetResult();
                await releaseRefresh.Task.WaitAsync(cancellationToken);
                return Ok(CreateRotatedResponse());
            }

            var body = await request.Content!.ReadFromJsonAsync<LogoutRequest>(cancellationToken);
            logoutToken = body!.RefreshToken;
            Assert.False(session!.IsAuthenticated);
            Assert.Null(await session.GetAccessTokenAsync(cancellationToken));
            logoutEntered.TrySetResult();
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        session = await LoginSessionAsync(handler);
        var refreshTask = session.TryRefreshAccessTokenAsync(InitialAccessToken);
        await refreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var logoutTask = session.LogoutAsync();
        Assert.Equal(0, handler.RequestCountFor("/logout"));
        releaseRefresh.TrySetResult();

        Assert.True(await refreshTask);
        Assert.Equal(ClientLogoutStatus.LoggedOut, await logoutTask);
        await logoutEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RotatedRefreshToken, logoutToken);
        Assert.False(session.IsAuthenticated);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task LogoutAsync_WhenCallerCancelsWhileWaiting_StillClearsAfterRefresh()
    {
        var refreshEntered = NewSignal();
        var releaseRefresh = NewSignal();
        var handler = CreateRefreshBlockingHandler(refreshEntered, releaseRefresh);
        var session = await LoginSessionAsync(handler);
        var refreshTask = session.TryRefreshAccessTokenAsync(InitialAccessToken);
        await refreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var callerCancellation = new CancellationTokenSource();

        var logoutTask = session.LogoutAsync(callerCancellation.Token);
        callerCancellation.Cancel();
        releaseRefresh.TrySetResult();

        Assert.True(await refreshTask);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => logoutTask);
        Assert.False(session.IsAuthenticated);
        Assert.Null(await session.GetAccessTokenAsync());
        Assert.Equal(0, handler.RequestCountFor("/logout"));
        await session.DisposeAsync();
    }

    [Fact]
    public async Task LogoutAsync_WhenTransportFails_RemainsLoggedOutWithoutRetry()
    {
        var handler = new DelegateHttpHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/login", StringComparison.Ordinal))
            {
                return Task.FromResult(Ok(CreateLoginResponse()));
            }

            throw new HttpRequestException("logout failed");
        });
        var session = await LoginSessionAsync(handler);

        var status = await session.LogoutAsync();

        Assert.Equal(ClientLogoutStatus.ServiceUnavailable, status);
        Assert.False(session.IsAuthenticated);
        Assert.Null(await session.GetAccessTokenAsync());
        Assert.Equal(1, handler.RequestCountFor("/logout"));
        await session.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_WhenRefreshIsRunning_CancelsFlightAndClearsSession()
    {
        var refreshEntered = NewSignal();
        var handler = new DelegateHttpHandler(async (request, cancellationToken) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/login", StringComparison.Ordinal))
            {
                return Ok(CreateLoginResponse());
            }

            refreshEntered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        });
        var session = await LoginSessionAsync(handler);
        var refreshTask = session.TryRefreshAccessTokenAsync(InitialAccessToken);
        await refreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var firstDispose = session.DisposeAsync().AsTask();
        var secondDispose = session.DisposeAsync().AsTask();

        await Task.WhenAll(firstDispose, secondDispose);
        Assert.False(await refreshTask);
        Assert.False(session.IsAuthenticated);
        Assert.Null(await session.GetAccessTokenAsync());
    }

    [Theory]
    [InlineData("ftp://example.com/")]
    [InlineData("https://user@example.com/")]
    [InlineData("https://example.com/?query=1")]
    [InlineData("https://example.com/#fragment")]
    public void Constructor_WhenServerUriIsUnsafe_Throws(string value)
    {
        using var httpClient = new HttpClient(new DelegateHttpHandler((_, _) =>
            throw new InvalidOperationException()));
        var logger = new RecordingLogger<ClientAuthenticationClient>();

        Assert.Throws<ArgumentException>(() => new ClientAuthenticationClient(
            new Uri(value),
            httpClient,
            logger));
    }

    public static TheoryData<LoginResponse> InvalidLoginResponses => new()
    {
        CreateLoginResponse() with { UserId = Guid.Empty },
        CreateLoginResponse() with { DisplayName = " " },
        CreateLoginResponse() with { DisplayName = new string('d', 129) },
        CreateLoginResponse() with { AccessToken = " " },
        CreateLoginResponse() with { AccessToken = "bad token" },
        CreateLoginResponse() with { AccessToken = "bad,token" },
        CreateLoginResponse() with { RefreshToken = "bad\ntoken" },
        CreateLoginResponse() with { ExpiresAt = Now },
        CreateLoginResponse() with { ServerVersion = "" },
        CreateLoginResponse() with { ServerVersion = new string('v', 65) },
        CreateLoginResponse() with { MinimumSupportedClientVersion = "" },
    };

    private static (ClientAuthenticationClient Client, RecordingLogger<ClientAuthenticationClient> Logger)
        CreateClient(DelegateHttpHandler handler)
    {
        var logger = new RecordingLogger<ClientAuthenticationClient>();
        return (
            new ClientAuthenticationClient(
                new Uri("HTTPS://EXAMPLE.COM:443/proxy"),
                new HttpClient(handler),
                logger,
                new FixedTimeProvider(Now)),
            logger);
    }

    private static async Task<ClientAuthenticationSession> LoginSessionAsync(
        DelegateHttpHandler handler,
        RecordingLogger<ClientAuthenticationClient>? logger = null)
    {
        logger ??= new RecordingLogger<ClientAuthenticationClient>();
        var client = new ClientAuthenticationClient(
            new Uri("https://example.com/proxy/"),
            new HttpClient(handler),
            logger,
            new FixedTimeProvider(Now));
        var outcome = await client.LoginAsync(CreateLoginRequest());
        Assert.Equal(ClientLoginStatus.Authenticated, outcome.Status);
        return Assert.IsType<ClientAuthenticationSession>(outcome.Session);
    }

    private static DelegateHttpHandler CreateLoginThenResponseHandler(
        HttpResponseMessage response) =>
        new((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("/login", StringComparison.Ordinal)
                ? Ok(CreateLoginResponse())
                : response));

    private static DelegateHttpHandler CreateRefreshBlockingHandler(
        TaskCompletionSource refreshEntered,
        TaskCompletionSource releaseRefresh) =>
        new(async (request, cancellationToken) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/login", StringComparison.Ordinal))
            {
                return Ok(CreateLoginResponse());
            }

            refreshEntered.TrySetResult();
            await releaseRefresh.Task.WaitAsync(cancellationToken);
            return Ok(CreateRotatedResponse());
        });

    private static LoginRequest CreateLoginRequest() =>
        new(UserName, Password, "test-device", "1.0.0");

    private static LoginResponse CreateLoginResponse() =>
        new(
            UserId,
            DisplayName,
            InitialAccessToken,
            InitialRefreshToken,
            Now.AddHours(1),
            "1.0.0",
            "1.0.0");

    private static LoginResponse CreateRotatedResponse() =>
        CreateLoginResponse() with
        {
            AccessToken = RotatedAccessToken,
            RefreshToken = RotatedRefreshToken,
            ExpiresAt = Now.AddHours(2),
        };

    private static HttpResponseMessage Ok(LoginResponse response) =>
        new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(response),
        };

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class DelegateHttpHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync) :
        HttpMessageHandler
    {
        private readonly ConcurrentQueue<string> requestPaths = new();
        private int requestCount;

        public int RequestCount => Volatile.Read(ref requestCount);

        public int RequestCountFor(string suffix) =>
            requestPaths.Count(path => path.EndsWith(suffix, StringComparison.Ordinal));

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref requestCount);
            requestPaths.Enqueue(request.RequestUri!.AbsolutePath);
            return sendAsync(request, cancellationToken);
        }
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

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
