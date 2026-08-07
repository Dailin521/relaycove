using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RelayCove.Client.Storage;
using RelayCove.Client.Sync;
using RelayCove.Shared.Conversations;
using RelayCove.Shared.Errors;
using RelayCove.Shared.Messages;

namespace RelayCove.Client.Tests.Sync;

[Collection(SqliteTestCollection.Name)]
public sealed class ClientMentionCandidateCoordinatorTests : IDisposable
{
    private static readonly Guid UserId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Uri ServerBaseUri = new("https://relaycove.example/team/");
    private readonly string rootDirectory = Path.Combine(
        Path.GetTempPath(),
        "RelayCove.MentionCandidate.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SearchAsync_WhenResponseIsValid_UsesScopedAuthenticatedRequest()
    {
        await using var prepared = await CreatePreparedAsync();
        var firstId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var secondId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        using var httpClient = new HttpClient(new DelegateHttpHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(
                $"/team/api/conversations/{prepared.Conversation.Id:D}/mention-candidates",
                request.RequestUri!.AbsolutePath);
            Assert.Equal("query=al_&limit=2", request.RequestUri.Query.TrimStart('?'));
            Assert.Equal(
                new AuthenticationHeaderValue("Bearer", "access-token"),
                request.Headers.Authorization);
            return Task.FromResult(Ok(new MentionCandidateListResponse(
                prepared.Conversation.Id,
                [
                    new MentionCandidateDto(firstId, "Al_one", "First"),
                    new MentionCandidateDto(secondId, "al_two", "Second"),
                ],
                HasMore: true)));
        }));
        await using var coordinator = CreateCoordinator(prepared, httpClient);

        var outcome = await coordinator.SearchAsync(
            prepared.Conversation.Id,
            "al_",
            limit: 2);

        Assert.Equal(ClientMentionCandidateStatus.Completed, outcome.Status);
        Assert.Equal([firstId, secondId], outcome.Candidates.Select(value => value.UserId));
        Assert.True(outcome.HasMore);
        Assert.Contains("[REDACTED]", outcome.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Al_one", outcome.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, 20)]
    [InlineData("bad query", 20)]
    [InlineData("prefix", 0)]
    [InlineData("prefix", 51)]
    public async Task SearchAsync_WhenInputIsInvalid_DoesNotSend(
        string? query,
        int limit)
    {
        await using var prepared = await CreatePreparedAsync();
        var requestCount = 0;
        using var httpClient = new HttpClient(new DelegateHttpHandler((_, _) =>
        {
            Interlocked.Increment(ref requestCount);
            throw new InvalidOperationException("Invalid input must not reach HTTP.");
        }));
        await using var coordinator = CreateCoordinator(prepared, httpClient);

        var outcome = await coordinator.SearchAsync(
            prepared.Conversation.Id,
            query,
            limit);

        Assert.Equal(ClientMentionCandidateStatus.ValidationFailed, outcome.Status);
        Assert.Equal(0, Volatile.Read(ref requestCount));
    }

    [Fact]
    public async Task SearchAsync_WhenQueryIsEmpty_RequestsBoundedAllMemberPage()
    {
        await using var prepared = await CreatePreparedAsync();
        using var httpClient = new HttpClient(new DelegateHttpHandler((request, _) =>
        {
            Assert.Equal("query=&limit=50", request.RequestUri!.Query.TrimStart('?'));
            return Task.FromResult(Ok(new MentionCandidateListResponse(
                prepared.Conversation.Id,
                [new MentionCandidateDto(Guid.NewGuid(), "alice", "Alice")],
                HasMore: false)));
        }));
        await using var coordinator = CreateCoordinator(prepared, httpClient);

        var outcome = await coordinator.SearchAsync(prepared.Conversation.Id, string.Empty);

        Assert.Equal(ClientMentionCandidateStatus.Completed, outcome.Status);
        Assert.Single(outcome.Candidates);
    }

    [Theory]
    [InlineData("conversation")]
    [InlineData("prefix")]
    [InlineData("duplicate-id")]
    [InlineData("duplicate-name")]
    [InlineData("order")]
    [InlineData("short-more")]
    [InlineData("invalid-name")]
    [InlineData("invalid-display")]
    public async Task SearchAsync_WhenResponseInvariantIsInvalid_ReturnsProtocolError(
        string scenario)
    {
        await using var prepared = await CreatePreparedAsync();
        var firstId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var secondId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var conversationId = scenario == "conversation"
            ? Guid.NewGuid()
            : prepared.Conversation.Id;
        var first = new MentionCandidateDto(
            firstId,
            scenario == "prefix" ? "beta" : scenario == "invalid-name" ? ".._" : "alpha",
            scenario == "invalid-display" ? " " : "Alpha");
        var second = new MentionCandidateDto(
            scenario == "duplicate-id" ? firstId : secondId,
            scenario == "duplicate-name" ? "ALPHA" : scenario == "order" ? "aardvark" : "alpine",
            "Alpine");
        var candidates = scenario == "short-more"
            ? new[] { first }
            : new[] { first, second };
        using var httpClient = new HttpClient(new DelegateHttpHandler((_, _) =>
            Task.FromResult(Ok(new MentionCandidateListResponse(
                conversationId,
                candidates,
                HasMore: scenario == "short-more")))));
        await using var coordinator = CreateCoordinator(prepared, httpClient);

        var outcome = await coordinator.SearchAsync(
            prepared.Conversation.Id,
            "al",
            limit: 2);

        Assert.Equal(ClientMentionCandidateStatus.ProtocolError, outcome.Status);
        Assert.Empty(outcome.Candidates);
    }

    [Fact]
    public async Task SearchAsync_WhenUnauthorized_RefreshesExactlyOnce()
    {
        await using var prepared = await CreatePreparedAsync();
        var authentication = new FakeAuthenticationSession(
            "rejected-token",
            refreshedToken: "fresh-token");
        var requestCount = 0;
        using var httpClient = new HttpClient(new DelegateHttpHandler((request, _) =>
        {
            var current = Interlocked.Increment(ref requestCount);
            if (current == 1)
            {
                Assert.Equal("Bearer rejected-token", request.Headers.Authorization!.ToString());
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            }

            Assert.Equal("Bearer fresh-token", request.Headers.Authorization!.ToString());
            return Task.FromResult(Ok(new MentionCandidateListResponse(
                prepared.Conversation.Id,
                Array.Empty<MentionCandidateDto>(),
                HasMore: false)));
        }));
        await using var coordinator = CreateCoordinator(
            prepared,
            httpClient,
            authenticationSession: authentication);

        var outcome = await coordinator.SearchAsync(prepared.Conversation.Id, "nobody");

        Assert.Equal(ClientMentionCandidateStatus.Completed, outcome.Status);
        Assert.Equal(1, authentication.RefreshCount);
        Assert.Equal(2, Volatile.Read(ref requestCount));
    }

    [Fact]
    public async Task SearchAsync_WhenRefreshedTokenIsRejected_DoesNotLoop()
    {
        await using var prepared = await CreatePreparedAsync();
        var authentication = new FakeAuthenticationSession(
            "rejected-token",
            refreshedToken: "also-rejected");
        var requestCount = 0;
        using var httpClient = new HttpClient(new DelegateHttpHandler((_, _) =>
        {
            Interlocked.Increment(ref requestCount);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        }));
        await using var coordinator = CreateCoordinator(
            prepared,
            httpClient,
            authenticationSession: authentication);

        var outcome = await coordinator.SearchAsync(prepared.Conversation.Id, "nobody");

        Assert.Equal(ClientMentionCandidateStatus.AuthenticationRequired, outcome.Status);
        Assert.Equal(1, authentication.RefreshCount);
        Assert.Equal(2, Volatile.Read(ref requestCount));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, (int)ClientMentionCandidateStatus.ProtocolError)]
    [InlineData(HttpStatusCode.TooManyRequests,
        (int)ClientMentionCandidateStatus.TransientFailure)]
    [InlineData(HttpStatusCode.InternalServerError,
        (int)ClientMentionCandidateStatus.TransientFailure)]
    [InlineData(HttpStatusCode.Gone, (int)ClientMentionCandidateStatus.RemoteFailure)]
    public async Task SearchAsync_WhenHttpFailureReturned_ClassifiesWithoutRetry(
        HttpStatusCode statusCode,
        int expectedStatus)
    {
        await using var prepared = await CreatePreparedAsync();
        var requestCount = 0;
        using var httpClient = new HttpClient(new DelegateHttpHandler((_, _) =>
        {
            Interlocked.Increment(ref requestCount);
            return Task.FromResult(new HttpResponseMessage(statusCode));
        }));
        await using var coordinator = CreateCoordinator(prepared, httpClient);

        var outcome = await coordinator.SearchAsync(prepared.Conversation.Id, "abc");

        Assert.Equal((ClientMentionCandidateStatus)expectedStatus, outcome.Status);
        Assert.Equal(1, Volatile.Read(ref requestCount));
    }

    [Fact]
    public async Task SearchAsync_WhenStableRevocationReturned_PurgesAndNotifies()
    {
        await using var prepared = await CreatePreparedAsync();
        var revoked = new List<Guid>();
        using var httpClient = new HttpClient(new DelegateHttpHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = JsonContent.Create(new ApiErrorResponse(
                    ApiErrorCodes.ConversationAccessRevoked,
                    "revoked",
                    TraceId: null)),
            })));
        await using var coordinator = CreateCoordinator(
            prepared,
            httpClient,
            (conversationId, _) =>
            {
                revoked.Add(conversationId);
                return Task.CompletedTask;
            });

        var outcome = await coordinator.SearchAsync(prepared.Conversation.Id, "abc");

        Assert.Equal(ClientMentionCandidateStatus.AccessRevoked, outcome.Status);
        Assert.Equal([prepared.Conversation.Id], revoked);
        Assert.Equal(0, Scalar(prepared.Identity, "SELECT COUNT(*) FROM LocalConversations;"));
    }

    [Fact]
    public async Task SearchAsync_WhenAccessDenied_DoesNotPurgeConversation()
    {
        await using var prepared = await CreatePreparedAsync();
        using var httpClient = new HttpClient(new DelegateHttpHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = JsonContent.Create(new ApiErrorResponse(
                    "AccessDenied",
                    "denied",
                    TraceId: null)),
            })));
        await using var coordinator = CreateCoordinator(prepared, httpClient);

        var outcome = await coordinator.SearchAsync(prepared.Conversation.Id, "abc");

        Assert.Equal(ClientMentionCandidateStatus.AccessDenied, outcome.Status);
        Assert.Equal(1, Scalar(prepared.Identity, "SELECT COUNT(*) FROM LocalConversations;"));
    }

    [Fact]
    public async Task SearchAsync_WhenRequestFails_DoesNotLogCandidateData()
    {
        const string query = "secretprefix";
        await using var prepared = await CreatePreparedAsync();
        var logger = new RecordingLogger<ClientMentionCandidateCoordinator>();
        using var httpClient = new HttpClient(new DelegateHttpHandler((_, _) =>
            throw new HttpRequestException("network failure")));
        await using var coordinator = CreateCoordinator(
            prepared,
            httpClient,
            logger: logger);

        var outcome = await coordinator.SearchAsync(prepared.Conversation.Id, query);

        Assert.Equal(ClientMentionCandidateStatus.TransientFailure, outcome.Status);
        Assert.NotEmpty(logger.Messages);
        Assert.All(logger.Messages, message =>
        {
            Assert.DoesNotContain(query, message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                prepared.Conversation.Id.ToString(),
                message,
                StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task SearchAsync_WhenSuccessPayloadExceedsBound_ReturnsProtocolError()
    {
        await using var prepared = await CreatePreparedAsync();
        using var httpClient = new HttpClient(new DelegateHttpHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[(64 * 1024) + 1]),
            })));
        await using var coordinator = CreateCoordinator(prepared, httpClient);

        var outcome = await coordinator.SearchAsync(prepared.Conversation.Id, "abc");

        Assert.Equal(ClientMentionCandidateStatus.ProtocolError, outcome.Status);
        Assert.Empty(outcome.Candidates);
    }

    [Fact]
    public async Task DisposeAsync_WhenRequestIsInFlight_CancelsSearch()
    {
        await using var prepared = await CreatePreparedAsync();
        var requestSeen = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var httpClient = new HttpClient(new DelegateHttpHandler(async (_, token) =>
        {
            requestSeen.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Canceled request must not continue.");
        }));
        var coordinator = CreateCoordinator(prepared, httpClient);
        var search = coordinator.SearchAsync(prepared.Conversation.Id, "abc");
        await requestSeen.Task;

        await coordinator.DisposeAsync();
        var outcome = await search;

        Assert.Equal(ClientMentionCandidateStatus.Canceled, outcome.Status);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(rootDirectory))
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    private async Task<PreparedCandidate> CreatePreparedAsync()
    {
        var identity = AccountScopeIdentity.Create(ServerBaseUri, UserId, rootDirectory);
        var cache = await AccountScopedLocalCache.CreateAsync(
            identity,
            NullLogger<AccountScopedLocalCache>.Instance);
        var conversation = new ConversationDto(
            Guid.NewGuid(),
            ConversationType.PrivateChannel,
            "Conversation",
            AvatarUrl: null,
            CreatedAt: DateTimeOffset.Parse("2026-08-03T01:00:00Z"),
            UpdatedAt: DateTimeOffset.Parse("2026-08-03T02:00:00Z"),
            LastMessageId: 0,
            LastReadMessageId: 0,
            UnreadCount: 0);
        Assert.Equal(
            LocalCacheOperationStatus.Ready,
            await cache.ApplyAuthoritativeConversationSnapshotAsync(
                new ConversationListResponse([conversation], Complete: true)));
        return new PreparedCandidate(identity, cache, conversation);
    }

    private static ClientMentionCandidateCoordinator CreateCoordinator(
        PreparedCandidate prepared,
        HttpClient httpClient,
        Func<Guid, CancellationToken, Task>? conversationRevokedAsync = null,
        IClientAuthenticationSession? authenticationSession = null,
        ILogger<ClientMentionCandidateCoordinator>? logger = null) =>
        new(
            prepared.Identity,
            httpClient,
            authenticationSession ?? new FakeAuthenticationSession("access-token"),
            prepared.Cache,
            logger ?? NullLogger<ClientMentionCandidateCoordinator>.Instance,
            conversationRevokedAsync);

    private static long Scalar(AccountScopeIdentity identity, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = identity.DatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            ForeignKeys = true,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static HttpResponseMessage Ok<T>(T value) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(value),
    };

    private sealed record PreparedCandidate(
        AccountScopeIdentity Identity,
        AccountScopedLocalCache Cache,
        ConversationDto Conversation) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Cache.DisposeAsync();
    }

    private sealed class FakeAuthenticationSession(
        string? accessToken,
        string? refreshedToken = null) : IClientAuthenticationSession
    {
        private string? currentAccessToken = accessToken;
        private int refreshCount;

        public int RefreshCount => Volatile.Read(ref refreshCount);

        public ValueTask<string?> GetAccessTokenAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(currentAccessToken);

        public Task<bool> TryRefreshAccessTokenAsync(
            string rejectedAccessToken,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref refreshCount);
            if (refreshedToken is null)
            {
                return Task.FromResult(false);
            }

            currentAccessToken = refreshedToken;
            return Task.FromResult(true);
        }
    }

    private sealed class DelegateHttpHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync) :
        HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            sendAsync(request, cancellationToken);
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
