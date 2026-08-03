using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using RelayCove.Client.Auth;
using RelayCove.Shared.Auth;

namespace RelayCove.Client.Tests.Auth;

public sealed class PersistentClientAuthenticationTests
{
    private static readonly DateTimeOffset Now = new(
        2026,
        8,
        3,
        16,
        0,
        0,
        TimeSpan.Zero);
    private static readonly Guid UserId =
        Guid.Parse("1b92d8fa-8d31-4aef-b8e4-289516bce786");
    private static readonly Uri ServerBaseUri = new("https://example.com/proxy/");
    private const string UserName = "classified-user";
    private const string Password = "classified-password";
    private const string DisplayName = "Classified Display";
    private const string InitialAccessToken = "initial.access.token";
    private const string InitialRefreshToken = "initial-refresh-token";
    private const string RotatedAccessToken = "rotated.access.token";
    private const string RotatedRefreshToken = "rotated-refresh-token";

    [Fact]
    public async Task LoginAsync_WhenSuccessful_PersistsRefreshTokenBeforeReturningSession()
    {
        using var directory = new TemporaryDirectory();
        var handler = new DelegateHttpHandler(async (request, cancellationToken) =>
        {
            Assert.Equal("/proxy/api/auth/login", request.RequestUri!.AbsolutePath);
            var body = await request.Content!.ReadFromJsonAsync<LoginRequest>(cancellationToken);
            Assert.Equal(UserName, body!.UserName);
            Assert.Equal(Password, body.Password);
            return Ok(CreateLoginResponse());
        });
        var context = CreateContext(directory.Path, handler);

        var outcome = await context.Authentication.LoginAsync(
            ServerBaseUri,
            CreateLoginRequest());
        var stored = await context.Store.LoadAsync();

        Assert.Equal(PersistentClientAuthenticationStatus.Authenticated, outcome.Status);
        Assert.True(outcome.IsCredentialPersisted);
        Assert.NotNull(outcome.Session);
        Assert.True(outcome.Session.IsCredentialPersisted);
        Assert.Equal(ClientCredentialReadStatus.Loaded, stored.Status);
        Assert.Equal(InitialRefreshToken, stored.Credential!.RefreshToken);
        Assert.Empty(context.AuthenticationLogger.Entries);
        Assert.Single(context.ManagerLogger.Entries);
        Assert.DoesNotContain(UserName, context.ManagerLogger.Entries.Single(), StringComparison.Ordinal);
        await outcome.Session.DisposeAsync();
    }

    [Fact]
    public async Task RestoreAsync_WhenStoredCredentialIsValid_RotatesOnceAndPersistsReplacement()
    {
        using var directory = new TemporaryDirectory();
        var capturedTokens = new ConcurrentQueue<string>();
        var handler = new DelegateHttpHandler(async (request, cancellationToken) =>
        {
            Assert.Equal("/proxy/api/auth/refresh", request.RequestUri!.AbsolutePath);
            var body = await request.Content!
                .ReadFromJsonAsync<RefreshTokenRequest>(cancellationToken);
            capturedTokens.Enqueue(body!.RefreshToken);
            return Ok(CreateRotatedResponse());
        });
        var context = CreateContext(directory.Path, handler);
        Assert.True(await context.Store.SaveAsync(
            ServerBaseUri,
            UserId,
            InitialRefreshToken));

        var outcome = await context.Authentication.RestoreAsync();
        var stored = await context.Store.LoadAsync();

        Assert.Equal(PersistentClientAuthenticationStatus.Authenticated, outcome.Status);
        Assert.True(outcome.IsCredentialPersisted);
        Assert.Equal(RotatedAccessToken, await outcome.Session!.GetAccessTokenAsync());
        Assert.Equal([InitialRefreshToken], capturedTokens);
        Assert.Equal(1, handler.RequestCountFor("/refresh"));
        Assert.Equal(RotatedRefreshToken, stored.Credential!.RefreshToken);
        await outcome.Session.DisposeAsync();
    }

    [Fact]
    public async Task RestoreAsync_WhenCredentialIsMissing_DoesNotSendHttp()
    {
        using var directory = new TemporaryDirectory();
        var handler = new DelegateHttpHandler((_, _) =>
            throw new InvalidOperationException("HTTP must not be called."));
        var context = CreateContext(directory.Path, handler);

        var outcome = await context.Authentication.RestoreAsync();

        Assert.Equal(PersistentClientAuthenticationStatus.NoStoredCredential, outcome.Status);
        Assert.Null(outcome.Session);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task RestoreAsync_WhenCredentialIsCorrupt_PreservesEvidenceAndDoesNotSendHttp()
    {
        using var directory = new TemporaryDirectory();
        var handler = new DelegateHttpHandler((_, _) =>
            throw new InvalidOperationException("HTTP must not be called."));
        var context = CreateContext(directory.Path, handler);
        await File.WriteAllBytesAsync(context.Store.CredentialPath, [1, 2, 3]);

        var outcome = await context.Authentication.RestoreAsync();

        Assert.Equal(PersistentClientAuthenticationStatus.CredentialCorrupt, outcome.Status);
        Assert.True(File.Exists(context.Store.CredentialPath));
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task RestoreAsync_WhenCredentialIsUnavailable_DoesNotSendHttp()
    {
        using var directory = new TemporaryDirectory();
        var handler = new DelegateHttpHandler((_, _) =>
            throw new InvalidOperationException("HTTP must not be called."));
        var context = CreateContext(directory.Path, handler);
        Directory.CreateDirectory(context.Store.CredentialPath);

        var outcome = await context.Authentication.RestoreAsync();

        Assert.Equal(PersistentClientAuthenticationStatus.CredentialUnavailable, outcome.Status);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task RestoreAsync_WhenRefreshIsUnauthorized_ClearsStoredCredential()
    {
        using var directory = new TemporaryDirectory();
        var handler = new DelegateHttpHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        var context = CreateContext(directory.Path, handler);
        Assert.True(await context.Store.SaveAsync(
            ServerBaseUri,
            UserId,
            InitialRefreshToken));

        var outcome = await context.Authentication.RestoreAsync();

        Assert.Equal(PersistentClientAuthenticationStatus.AuthenticationFailed, outcome.Status);
        Assert.Equal(ClientCredentialReadStatus.NotFound, (await context.Store.LoadAsync()).Status);
        Assert.Equal(1, handler.RequestCountFor("/refresh"));
    }

    [Fact]
    public async Task RestoreAsync_WhenResponseUserDoesNotMatch_ClearsStoredCredential()
    {
        using var directory = new TemporaryDirectory();
        var handler = new DelegateHttpHandler((_, _) => Task.FromResult(
            Ok(CreateRotatedResponse() with { UserId = Guid.NewGuid() })));
        var context = CreateContext(directory.Path, handler);
        Assert.True(await context.Store.SaveAsync(
            ServerBaseUri,
            UserId,
            InitialRefreshToken));

        var outcome = await context.Authentication.RestoreAsync();

        Assert.Equal(PersistentClientAuthenticationStatus.ProtocolError, outcome.Status);
        Assert.Null(outcome.Session);
        Assert.Equal(ClientCredentialReadStatus.NotFound, (await context.Store.LoadAsync()).Status);
    }

    [Fact]
    public async Task RestoreAsync_WhenResponseIsMalformed_ClearsStoredCredentialWithoutRetry()
    {
        using var directory = new TemporaryDirectory();
        var handler = new DelegateHttpHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{not-json"),
            }));
        var context = CreateContext(directory.Path, handler);
        Assert.True(await context.Store.SaveAsync(
            ServerBaseUri,
            UserId,
            InitialRefreshToken));

        var outcome = await context.Authentication.RestoreAsync();

        Assert.Equal(PersistentClientAuthenticationStatus.ProtocolError, outcome.Status);
        Assert.Equal(ClientCredentialReadStatus.NotFound, (await context.Store.LoadAsync()).Status);
        Assert.Equal(1, handler.RequestCountFor("/refresh"));
    }

    [Fact]
    public async Task RestoreAsync_WhenSuccessfulBodyReadCancels_ClearsStoredCredential()
    {
        using var directory = new TemporaryDirectory();
        var handler = new DelegateHttpHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new CanceledHttpContent(),
            }));
        var context = CreateContext(directory.Path, handler);
        Assert.True(await context.Store.SaveAsync(
            ServerBaseUri,
            UserId,
            InitialRefreshToken));

        var outcome = await context.Authentication.RestoreAsync();

        Assert.Equal(PersistentClientAuthenticationStatus.ProtocolError, outcome.Status);
        Assert.Equal(ClientCredentialReadStatus.NotFound, (await context.Store.LoadAsync()).Status);
        Assert.Equal(1, handler.RequestCountFor("/refresh"));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, (int)PersistentClientAuthenticationStatus.RateLimited)]
    [InlineData(HttpStatusCode.ServiceUnavailable, (int)PersistentClientAuthenticationStatus.ServiceUnavailable)]
    public async Task RestoreAsync_WhenFailureMayBeTransient_PreservesStoredCredential(
        HttpStatusCode statusCode,
        int expectedStatus)
    {
        using var directory = new TemporaryDirectory();
        var handler = new DelegateHttpHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(statusCode)));
        var context = CreateContext(directory.Path, handler);
        Assert.True(await context.Store.SaveAsync(
            ServerBaseUri,
            UserId,
            InitialRefreshToken));

        var outcome = await context.Authentication.RestoreAsync();
        var stored = await context.Store.LoadAsync();

        Assert.Equal((PersistentClientAuthenticationStatus)expectedStatus, outcome.Status);
        Assert.Equal(InitialRefreshToken, stored.Credential!.RefreshToken);
        Assert.Equal(1, handler.RequestCountFor("/refresh"));
    }

    [Fact]
    public async Task LoginAsync_WhenCredentialSaveFails_ReturnsUnpersistedSessionAndClearsOldCredential()
    {
        using var directory = new TemporaryDirectory();
        FileStream? lockedTemporaryFile = null;
        var handler = new DelegateHttpHandler((_, _) =>
        {
            var temporaryPath = System.IO.Path.Combine(
                directory.Path,
                ClientCredentialStore.CredentialFileName + ".tmp");
            File.WriteAllBytes(temporaryPath, [1]);
            lockedTemporaryFile = new FileStream(
                temporaryPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);
            return Task.FromResult(Ok(CreateLoginResponse()));
        });
        var context = CreateContext(directory.Path, handler);
        Assert.True(await context.Store.SaveAsync(
            ServerBaseUri,
            UserId,
            "old-refresh-token"));

        var outcome = await context.Authentication.LoginAsync(
            ServerBaseUri,
            CreateLoginRequest());
        lockedTemporaryFile!.Dispose();

        Assert.Equal(PersistentClientAuthenticationStatus.Authenticated, outcome.Status);
        Assert.False(outcome.IsCredentialPersisted);
        Assert.False(outcome.Session!.IsCredentialPersisted);
        Assert.Equal(InitialAccessToken, await outcome.Session.GetAccessTokenAsync());
        Assert.Equal(ClientCredentialReadStatus.NotFound, (await context.Store.LoadAsync()).Status);
        await outcome.Session.DisposeAsync();
    }

    [Fact]
    public async Task SessionRefresh_WhenSuccessful_PersistsRotatedCredential()
    {
        using var directory = new TemporaryDirectory();
        var handler = LoginThenRefreshHandler(CreateRotatedResponse());
        var context = CreateContext(directory.Path, handler);
        var login = await context.Authentication.LoginAsync(
            ServerBaseUri,
            CreateLoginRequest());

        var refreshed = await login.Session!
            .TryRefreshAccessTokenAsync(InitialAccessToken);
        var stored = await context.Store.LoadAsync();

        Assert.True(refreshed);
        Assert.True(login.Session.IsCredentialPersisted);
        Assert.Equal(RotatedAccessToken, await login.Session.GetAccessTokenAsync());
        Assert.Equal(RotatedRefreshToken, stored.Credential!.RefreshToken);
        Assert.Equal(1, handler.RequestCountFor("/refresh"));
        await login.Session.DisposeAsync();
    }

    [Fact]
    public async Task SessionRefresh_WhenCredentialSaveFails_UsesNewMemoryTokensAndClearsOldFile()
    {
        using var directory = new TemporaryDirectory();
        var handler = LoginThenRefreshHandler(CreateRotatedResponse());
        var context = CreateContext(directory.Path, handler);
        var login = await context.Authentication.LoginAsync(
            ServerBaseUri,
            CreateLoginRequest());
        var temporaryPath = context.Store.CredentialPath + ".tmp";
        await File.WriteAllBytesAsync(temporaryPath, [1]);

        await using (var lockedTemporaryFile = new FileStream(
            temporaryPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None))
        {
            Assert.True(await login.Session!
                .TryRefreshAccessTokenAsync(InitialAccessToken));
        }

        Assert.Equal(RotatedAccessToken, await login.Session!.GetAccessTokenAsync());
        Assert.False(login.Session.IsCredentialPersisted);
        Assert.Equal(ClientCredentialReadStatus.NotFound, (await context.Store.LoadAsync()).Status);
        await login.Session.DisposeAsync();
    }

    [Fact]
    public async Task SessionRefresh_WhenUnauthorized_ClearsSessionAndStoredCredential()
    {
        using var directory = new TemporaryDirectory();
        var handler = new DelegateHttpHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("/login", StringComparison.Ordinal)
                ? Ok(CreateLoginResponse())
                : new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        var context = CreateContext(directory.Path, handler);
        var login = await context.Authentication.LoginAsync(
            ServerBaseUri,
            CreateLoginRequest());

        var refreshed = await login.Session!
            .TryRefreshAccessTokenAsync(InitialAccessToken);

        Assert.False(refreshed);
        Assert.False(login.Session.IsAuthenticated);
        Assert.Equal(ClientCredentialReadStatus.NotFound, (await context.Store.LoadAsync()).Status);
        await login.Session.DisposeAsync();
    }

    [Fact]
    public async Task SessionRefresh_WhenResponseUserDoesNotMatch_ClearsSessionAndStoredCredential()
    {
        using var directory = new TemporaryDirectory();
        var handler = LoginThenRefreshHandler(
            CreateRotatedResponse() with { UserId = Guid.NewGuid() });
        var context = CreateContext(directory.Path, handler);
        var login = await context.Authentication.LoginAsync(
            ServerBaseUri,
            CreateLoginRequest());

        var refreshed = await login.Session!
            .TryRefreshAccessTokenAsync(InitialAccessToken);

        Assert.False(refreshed);
        Assert.False(login.Session.IsAuthenticated);
        Assert.Equal(ClientCredentialReadStatus.NotFound, (await context.Store.LoadAsync()).Status);
        await login.Session.DisposeAsync();
    }

    [Fact]
    public async Task SessionRefresh_WhenSuccessfulResponseIsMalformed_ClearsSessionAndStoredCredential()
    {
        using var directory = new TemporaryDirectory();
        var handler = new DelegateHttpHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("/login", StringComparison.Ordinal)
                ? Ok(CreateLoginResponse())
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{not-json"),
                }));
        var context = CreateContext(directory.Path, handler);
        var login = await context.Authentication.LoginAsync(
            ServerBaseUri,
            CreateLoginRequest());

        var refreshed = await login.Session!
            .TryRefreshAccessTokenAsync(InitialAccessToken);

        Assert.False(refreshed);
        Assert.False(login.Session.IsAuthenticated);
        Assert.Equal(ClientCredentialReadStatus.NotFound, (await context.Store.LoadAsync()).Status);
        Assert.Equal(1, handler.RequestCountFor("/refresh"));
        await login.Session.DisposeAsync();
    }

    [Fact]
    public async Task SessionRefresh_WhenSuccessfulBodyReadCancels_ClearsSessionAndStoredCredential()
    {
        using var directory = new TemporaryDirectory();
        var handler = new DelegateHttpHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("/login", StringComparison.Ordinal)
                ? Ok(CreateLoginResponse())
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new CanceledHttpContent(),
                }));
        var context = CreateContext(directory.Path, handler);
        var login = await context.Authentication.LoginAsync(
            ServerBaseUri,
            CreateLoginRequest());

        var refreshed = await login.Session!
            .TryRefreshAccessTokenAsync(InitialAccessToken);

        Assert.False(refreshed);
        Assert.False(login.Session.IsAuthenticated);
        Assert.Equal(ClientCredentialReadStatus.NotFound, (await context.Store.LoadAsync()).Status);
        await login.Session.DisposeAsync();
    }

    [Fact]
    public async Task LogoutAsync_WhenPersistent_ClearsCredentialBeforeRemoteRequest()
    {
        using var directory = new TemporaryDirectory();
        TestContext? context = null;
        ClientAuthenticationSession? session = null;
        var handler = new DelegateHttpHandler(async (request, cancellationToken) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/login", StringComparison.Ordinal))
            {
                return Ok(CreateLoginResponse());
            }

            Assert.False(session!.IsAuthenticated);
            Assert.Equal(
                ClientCredentialReadStatus.NotFound,
                (await context!.Store.LoadAsync(cancellationToken)).Status);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        context = CreateContext(directory.Path, handler);
        var login = await context.Authentication.LoginAsync(
            ServerBaseUri,
            CreateLoginRequest());
        session = login.Session;

        var status = await session!.LogoutAsync();

        Assert.Equal(ClientLogoutStatus.LoggedOut, status);
        Assert.False(session.IsCredentialPersisted);
        Assert.Equal(1, handler.RequestCountFor("/logout"));
        await session.DisposeAsync();
    }

    [Fact]
    public async Task LogoutAsync_WhenCredentialClearFails_IgnoresCallerCancellationForRemoteRevoke()
    {
        using var directory = new TemporaryDirectory();
        var logoutCalled = false;
        var handler = new DelegateHttpHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/login", StringComparison.Ordinal))
            {
                return Task.FromResult(Ok(CreateLoginResponse()));
            }

            logoutCalled = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        });
        var context = CreateContext(directory.Path, handler);
        var login = await context.Authentication.LoginAsync(
            ServerBaseUri,
            CreateLoginRequest());
        File.Delete(context.Store.CredentialPath);
        Directory.CreateDirectory(context.Store.CredentialPath);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var status = await login.Session!.LogoutAsync(cancellation.Token);

        Assert.Equal(ClientLogoutStatus.CredentialClearFailed, status);
        Assert.True(logoutCalled);
        Assert.False(login.Session.IsAuthenticated);
        Assert.Equal(1, handler.RequestCountFor("/logout"));
        await login.Session.DisposeAsync();
    }

    [Fact]
    public async Task LogoutAsync_WhenLocalClearAndRemoteRevokeFail_SuppressesNextRestore()
    {
        using var directory = new TemporaryDirectory();
        var handler = new DelegateHttpHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/login", StringComparison.Ordinal))
            {
                return Task.FromResult(Ok(CreateLoginResponse()));
            }

            if (request.RequestUri.AbsolutePath.EndsWith("/logout", StringComparison.Ordinal))
            {
                return Task.FromException<HttpResponseMessage>(
                    new HttpRequestException("simulated offline logout"));
            }

            return Task.FromResult(Ok(CreateRotatedResponse()));
        });
        var context = CreateContext(directory.Path, handler);
        var login = await context.Authentication.LoginAsync(
            ServerBaseUri,
            CreateLoginRequest());
        using var lockedCredential = new FileStream(
            context.Store.CredentialPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);

        var logoutStatus = await login.Session!.LogoutAsync();
        await login.Session.DisposeAsync();
        var restore = await context.Authentication.RestoreAsync();

        Assert.Equal(ClientLogoutStatus.CredentialClearFailed, logoutStatus);
        Assert.Equal(
            PersistentClientAuthenticationStatus.NoStoredCredential,
            restore.Status);
        Assert.Null(restore.Session);
        Assert.Equal(1, handler.RequestCountFor("/logout"));
        Assert.Equal(0, handler.RequestCountFor("/refresh"));
    }

    [Fact]
    public async Task DisposeAsync_WhenCredentialIsPersisted_KeepsItForNextRestore()
    {
        using var directory = new TemporaryDirectory();
        var handler = new DelegateHttpHandler((_, _) =>
            Task.FromResult(Ok(CreateLoginResponse())));
        var context = CreateContext(directory.Path, handler);
        var login = await context.Authentication.LoginAsync(
            ServerBaseUri,
            CreateLoginRequest());

        await login.Session!.DisposeAsync();
        var stored = await context.Store.LoadAsync();

        Assert.Equal(ClientCredentialReadStatus.Loaded, stored.Status);
        Assert.Equal(InitialRefreshToken, stored.Credential!.RefreshToken);
    }

    [Fact]
    public async Task LoginAsync_WhenPriorSessionIsNotDisposed_RejectsCredentialOwnerOverlap()
    {
        using var directory = new TemporaryDirectory();
        var handler = new DelegateHttpHandler((_, _) =>
            Task.FromResult(Ok(CreateLoginResponse())));
        var context = CreateContext(directory.Path, handler);
        var first = await context.Authentication.LoginAsync(
            ServerBaseUri,
            CreateLoginRequest());

        var rejected = await context.Authentication.LoginAsync(
            ServerBaseUri,
            CreateLoginRequest());

        Assert.Equal(PersistentClientAuthenticationStatus.SessionAlreadyActive, rejected.Status);
        Assert.Equal(1, handler.RequestCountFor("/login"));

        await first.Session!.DisposeAsync();
        var second = await context.Authentication.LoginAsync(
            ServerBaseUri,
            CreateLoginRequest());
        Assert.Equal(PersistentClientAuthenticationStatus.Authenticated, second.Status);
        Assert.Equal(2, handler.RequestCountFor("/login"));
        await second.Session!.DisposeAsync();
    }

    [Fact]
    public async Task OutcomesAndLogs_RedactAuthenticationMaterial()
    {
        using var directory = new TemporaryDirectory();
        var handler = new DelegateHttpHandler((_, _) =>
            Task.FromResult(Ok(CreateLoginResponse())));
        var context = CreateContext(directory.Path, handler);

        var outcome = await context.Authentication.LoginAsync(
            ServerBaseUri,
            CreateLoginRequest());
        var text = context.Authentication + " " + outcome + " " +
            string.Join(' ', context.ManagerLogger.Entries);

        Assert.DoesNotContain("example.com", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(UserName, text, StringComparison.Ordinal);
        Assert.DoesNotContain(DisplayName, text, StringComparison.Ordinal);
        Assert.DoesNotContain(UserId.ToString(), text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(InitialAccessToken, text, StringComparison.Ordinal);
        Assert.DoesNotContain(InitialRefreshToken, text, StringComparison.Ordinal);
        await outcome.Session!.DisposeAsync();
    }

    private static TestContext CreateContext(
        string rootDirectory,
        DelegateHttpHandler handler)
    {
        var authenticationLogger = new RecordingLogger<ClientAuthenticationClient>();
        var managerLogger = new RecordingLogger<PersistentClientAuthentication>();
        var store = new ClientCredentialStore(
            rootDirectory,
            new RecordingLogger<ClientCredentialStore>());
        return new TestContext(
            new PersistentClientAuthentication(
                new HttpClient(handler),
                store,
                authenticationLogger,
                managerLogger,
                new FixedTimeProvider(Now)),
            store,
            authenticationLogger,
            managerLogger);
    }

    private static DelegateHttpHandler LoginThenRefreshHandler(LoginResponse refreshResponse) =>
        new((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("/login", StringComparison.Ordinal)
                ? Ok(CreateLoginResponse())
                : Ok(refreshResponse)));

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

    private sealed record TestContext(
        PersistentClientAuthentication Authentication,
        ClientCredentialStore Store,
        RecordingLogger<ClientAuthenticationClient> AuthenticationLogger,
        RecordingLogger<PersistentClientAuthentication> ManagerLogger);

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

    private sealed class CanceledHttpContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context) =>
            Task.FromCanceled(new CancellationToken(canceled: true));

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
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

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            var testRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "RelayCove.PersistentAuth.Tests"));
            Path = System.IO.Path.GetFullPath(System.IO.Path.Combine(
                testRoot,
                Guid.NewGuid().ToString("N")));
            var relativePath = System.IO.Path.GetRelativePath(testRoot, Path);
            if (System.IO.Path.IsPathFullyQualified(relativePath) ||
                relativePath.StartsWith("..", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Test directory escaped its root.");
            }

            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
