using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RelayCove.Client.Search;
using RelayCove.Client.Storage;
using RelayCove.Client.Sync;
using RelayCove.Shared.Conversations;
using RelayCove.Shared.Errors;
using RelayCove.Shared.Messages;

namespace RelayCove.Client.Tests.Sync;

public sealed class ClientSearchCoordinatorTests : IDisposable
{
    private static readonly Guid UserId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Uri ServerBaseUri = new("https://relaycove.example/team/");
    private readonly string rootDirectory = Path.Combine(
        Path.GetTempPath(),
        "RelayCove.Search.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SearchAsync_WhenResponseIsValid_UsesNormalizedScopedAuthenticatedRequest()
    {
        await using var prepared = await CreatePreparedAsync();
        var first = CreateResult(20, prepared.Conversation.Id);
        var second = CreateResult(10, prepared.Conversation.Id);
        using var httpClient = new HttpClient(new DelegateHttpHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/team/api/search", request.RequestUri!.AbsolutePath);
            Assert.Equal(
                "keyword=%E4%B8%AD%E6%96%87&conversationId=" +
                $"{prepared.Conversation.Id:D}&limit=2",
                request.RequestUri.Query.TrimStart('?'));
            Assert.Equal(
                new AuthenticationHeaderValue("Bearer", "access-token"),
                request.Headers.Authorization);
            return Task.FromResult(Ok(new SearchResponse([first, second], HasMore: true)));
        }));
        await using var coordinator = CreateCoordinator(prepared, httpClient);

        var outcome = await coordinator.SearchAsync("  中文  ", prepared.Conversation.Id, limit: 2);

        Assert.Equal(ClientSearchStatus.Completed, outcome.Status);
        Assert.Equal([20L, 10L], outcome.Results.Select(value => value.MessageId));
        Assert.True(outcome.HasMore);
        Assert.Null(outcome.RetryAfterSeconds);
        Assert.Contains("[REDACTED]", outcome.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(first.ConversationName, outcome.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(first.MessageId.ToString(), outcome.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchAsync_WhenGlobal_UsesNoConversationQuery()
    {
        await using var prepared = await CreatePreparedAsync();
        using var httpClient = new HttpClient(new DelegateHttpHandler((request, _) =>
        {
            Assert.Equal("keyword=attachment&limit=50", request.RequestUri!.Query.TrimStart('?'));
            return Task.FromResult(Ok(new SearchResponse([], HasMore: false)));
        }));
        await using var coordinator = CreateCoordinator(prepared, httpClient);

        var outcome = await coordinator.SearchAsync("attachment", conversationId: null);

        Assert.Equal(ClientSearchStatus.Completed, outcome.Status);
    }

    [Fact]
    public async Task SearchAsync_WhenMaximumUnicodePageIsReturned_AcceptsAllFiftyResults()
    {
        await using var prepared = await CreatePreparedAsync();
        var conversationName = RepeatRune("😀", ClientSearchPolicy.MaximumConversationNameScalars);
        var senderName = RepeatRune("😀", ClientSearchPolicy.MaximumSenderNameScalars);
        var snippet = RepeatRune("😀", ClientSearchPolicy.MaximumSnippetScalars);
        var attachmentName = RepeatRune(
            "😀",
            ClientSearchPolicy.MaximumAttachmentFileNameScalars);
        var results = Enumerable.Range(0, ClientSearchCoordinator.MaximumLimit)
            .Select(index => new SearchResultDto(
                ClientSearchCoordinator.MaximumLimit - index,
                prepared.Conversation.Id,
                conversationName,
                senderName,
                snippet,
                DateTimeOffset.Parse("2026-08-04T03:00:00Z"),
                attachmentName))
            .ToArray();
        using var httpClient = new HttpClient(new DelegateHttpHandler((_, _) =>
            Task.FromResult(Ok(new SearchResponse(results, HasMore: true)))));
        await using var coordinator = CreateCoordinator(prepared, httpClient);

        var outcome = await coordinator.SearchAsync("needle", prepared.Conversation.Id);

        Assert.Equal(ClientSearchStatus.Completed, outcome.Status);
        Assert.Equal(ClientSearchCoordinator.MaximumLimit, outcome.Results.Count);
        Assert.True(outcome.HasMore);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad\u0001control")]
    public async Task SearchAsync_WhenKeywordIsInvalid_DoesNotSend(string? keyword)
    {
        await using var prepared = await CreatePreparedAsync();
        var requestCount = 0;
        using var httpClient = new HttpClient(new DelegateHttpHandler((_, _) =>
        {
            Interlocked.Increment(ref requestCount);
            throw new InvalidOperationException("Invalid input must not reach HTTP.");
        }));
        await using var coordinator = CreateCoordinator(prepared, httpClient);

        var outcome = await coordinator.SearchAsync(keyword, prepared.Conversation.Id);

        Assert.Equal(ClientSearchStatus.ValidationFailed, outcome.Status);
        Assert.Equal(0, Volatile.Read(ref requestCount));
    }

    [Fact]
    public async Task SearchAsync_WhenKeywordHasMalformedUtf16_DoesNotSend()
    {
        await using var prepared = await CreatePreparedAsync();
        var requestCount = 0;
        using var httpClient = new HttpClient(new DelegateHttpHandler((_, _) =>
        {
            Interlocked.Increment(ref requestCount);
            throw new InvalidOperationException("Invalid input must not reach HTTP.");
        }));
        await using var coordinator = CreateCoordinator(prepared, httpClient);

        var outcome = await coordinator.SearchAsync(new string('\uD800', 1), prepared.Conversation.Id);

        Assert.Equal(ClientSearchStatus.ValidationFailed, outcome.Status);
        Assert.Equal(0, Volatile.Read(ref requestCount));
    }

    [Fact]
    public void IsValidResult_WhenResponseFieldHasMalformedUtf16_ReturnsFalse()
    {
        var result = CreateResult(1, Guid.NewGuid()) with
        {
            SenderName = new string('\uD800', 1),
        };

        Assert.False(ClientSearchPolicy.IsValidResult(result));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("ascending")]
    [InlineData("wrong-scope")]
    [InlineData("short-more")]
    [InlineData("empty-content")]
    [InlineData("bad-control")]
    [InlineData("long-snippet")]
    public async Task SearchAsync_WhenResponseInvariantIsInvalid_ReturnsProtocolError(string scenario)
    {
        await using var prepared = await CreatePreparedAsync();
        var newest = CreateResult(20, prepared.Conversation.Id);
        var older = CreateResult(10, prepared.Conversation.Id);
        var results = scenario switch
        {
            "duplicate" => new[] { newest, newest with { Snippet = "other" } },
            "ascending" => new[] { older, newest },
            "wrong-scope" => new[] { newest with { ConversationId = Guid.NewGuid() }, older },
            "empty-content" => new[] { newest with { Snippet = string.Empty, MatchedAttachmentFileName = null }, older },
            "bad-control" => new[] { newest with { SenderName = "bad\u0001" }, older },
            "long-snippet" => new[] { newest with { Snippet = new string('a', 161) }, older },
            _ => new[] { newest },
        };
        using var httpClient = new HttpClient(new DelegateHttpHandler((_, _) =>
            Task.FromResult(Ok(new SearchResponse(results, HasMore: scenario == "short-more")))));
        await using var coordinator = CreateCoordinator(prepared, httpClient);

        var outcome = await coordinator.SearchAsync("needle", prepared.Conversation.Id, limit: 2);

        Assert.Equal(ClientSearchStatus.ProtocolError, outcome.Status);
        Assert.Empty(outcome.Results);
        Assert.False(outcome.HasMore);
    }

    [Fact]
    public async Task SearchAsync_WhenUnauthorized_RefreshesExactlyOnce()
    {
        await using var prepared = await CreatePreparedAsync();
        var authentication = new FakeAuthenticationSession("rejected", refreshedToken: "fresh");
        var requestCount = 0;
        using var httpClient = new HttpClient(new DelegateHttpHandler((request, _) =>
        {
            var current = Interlocked.Increment(ref requestCount);
            Assert.Equal(current == 1 ? "Bearer rejected" : "Bearer fresh", request.Headers.Authorization!.ToString());
            return Task.FromResult(current == 1
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : Ok(new SearchResponse([], HasMore: false)));
        }));
        await using var coordinator = CreateCoordinator(prepared, httpClient, authentication);

        var outcome = await coordinator.SearchAsync("needle", conversationId: null);

        Assert.Equal(ClientSearchStatus.Completed, outcome.Status);
        Assert.Equal(1, authentication.RefreshCount);
        Assert.Equal(2, Volatile.Read(ref requestCount));
    }

    [Fact]
    public async Task SearchAsync_WhenScopedStableRevocation_ReturnsAccessRevokedAndPurges()
    {
        await using var prepared = await CreatePreparedAsync();
        var revoked = new List<Guid>();
        using var httpClient = new HttpClient(new DelegateHttpHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = JsonContent.Create(new ApiErrorResponse(
                    ApiErrorCodes.ConversationAccessRevoked,
                    "revoked")),
            })));
        await using var coordinator = CreateCoordinator(
            prepared,
            httpClient,
            conversationRevokedAsync: (conversationId, _) =>
            {
                revoked.Add(conversationId);
                return Task.CompletedTask;
            });

        var outcome = await coordinator.SearchAsync("needle", prepared.Conversation.Id);

        Assert.Equal(ClientSearchStatus.AccessRevoked, outcome.Status);
        Assert.Equal([prepared.Conversation.Id], revoked);
        Assert.Equal(0, Scalar(prepared.Identity, "SELECT COUNT(*) FROM LocalConversations;"));
    }

    [Fact]
    public async Task SearchAsync_WhenOrdinaryForbidden_DoesNotPurgeConversation()
    {
        await using var prepared = await CreatePreparedAsync();
        using var httpClient = new HttpClient(new DelegateHttpHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = JsonContent.Create(new ApiErrorResponse("AccessDenied", "denied")),
            })));
        await using var coordinator = CreateCoordinator(prepared, httpClient);

        var outcome = await coordinator.SearchAsync("needle", prepared.Conversation.Id);

        Assert.Equal(ClientSearchStatus.AccessDenied, outcome.Status);
        Assert.Equal(1, Scalar(prepared.Identity, "SELECT COUNT(*) FROM LocalConversations;"));
    }

    [Theory]
    [InlineData("120", 120)]
    [InlineData("0", null)]
    [InlineData("3601", null)]
    public async Task SearchAsync_WhenRateLimited_MapsBoundedRetryAfterWithoutRetry(
        string retryAfter,
        int? expectedRetryAfter)
    {
        await using var prepared = await CreatePreparedAsync();
        var requestCount = 0;
        using var httpClient = new HttpClient(new DelegateHttpHandler((_, _) =>
        {
            Interlocked.Increment(ref requestCount);
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
            return Task.FromResult(response);
        }));
        await using var coordinator = CreateCoordinator(prepared, httpClient);

        var outcome = await coordinator.SearchAsync("needle", conversationId: null);

        Assert.Equal(ClientSearchStatus.RateLimited, outcome.Status);
        Assert.Equal(expectedRetryAfter, outcome.RetryAfterSeconds);
        Assert.Empty(outcome.Results);
        Assert.Equal(1, Volatile.Read(ref requestCount));
    }

    [Fact]
    public async Task SearchAsync_WhenCallerCancels_ReturnsCanceledRatherThanTimeout()
    {
        await using var prepared = await CreatePreparedAsync();
        using var cancellation = new CancellationTokenSource();
        using var httpClient = new HttpClient(new DelegateHttpHandler((_, token) =>
            Task.FromCanceled<HttpResponseMessage>(token)));
        await using var coordinator = CreateCoordinator(prepared, httpClient);
        cancellation.Cancel();

        var outcome = await coordinator.SearchAsync("needle", conversationId: null, cancellationToken: cancellation.Token);

        Assert.Equal(ClientSearchStatus.Canceled, outcome.Status);
    }

    [Fact]
    public async Task SearchAsync_WhenTimeoutOccurs_ReturnsTimeout()
    {
        await using var prepared = await CreatePreparedAsync();
        using var httpClient = new HttpClient(new DelegateHttpHandler((_, _) =>
            Task.FromException<HttpResponseMessage>(new OperationCanceledException())));
        await using var coordinator = CreateCoordinator(prepared, httpClient);

        var outcome = await coordinator.SearchAsync("needle", conversationId: null);

        Assert.Equal(ClientSearchStatus.Timeout, outcome.Status);
    }

    [Fact]
    public async Task SearchAsync_WhenRequestFails_DoesNotLogSearchData()
    {
        const string keyword = "private needle";
        await using var prepared = await CreatePreparedAsync();
        var logger = new RecordingLogger<ClientSearchCoordinator>();
        using var httpClient = new HttpClient(new DelegateHttpHandler((_, _) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("network failure"))));
        await using var coordinator = CreateCoordinator(prepared, httpClient, logger: logger);

        var outcome = await coordinator.SearchAsync(keyword, prepared.Conversation.Id);

        Assert.Equal(ClientSearchStatus.TransientFailure, outcome.Status);
        Assert.NotEmpty(logger.Messages);
        Assert.All(logger.Messages, message =>
        {
            Assert.DoesNotContain(keyword, message, StringComparison.OrdinalIgnoreCase);
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
                Content = new ByteArrayContent(new byte[(512 * 1024) + 1]),
            })));
        await using var coordinator = CreateCoordinator(prepared, httpClient);

        var outcome = await coordinator.SearchAsync("needle", conversationId: null);

        Assert.Equal(ClientSearchStatus.ProtocolError, outcome.Status);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(rootDirectory))
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    private async Task<PreparedSearch> CreatePreparedAsync()
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
            CreatedAt: DateTimeOffset.Parse("2026-08-04T01:00:00Z"),
            UpdatedAt: DateTimeOffset.Parse("2026-08-04T02:00:00Z"),
            LastMessageId: 0,
            LastReadMessageId: 0,
            UnreadCount: 0);
        Assert.Equal(
            LocalCacheOperationStatus.Ready,
            await cache.ApplyAuthoritativeConversationSnapshotAsync(
                new ConversationListResponse([conversation], Complete: true)));
        return new PreparedSearch(identity, cache, conversation);
    }

    private static ClientSearchCoordinator CreateCoordinator(
        PreparedSearch prepared,
        HttpClient httpClient,
        IClientAuthenticationSession? authenticationSession = null,
        Func<Guid, CancellationToken, Task>? conversationRevokedAsync = null,
        ILogger<ClientSearchCoordinator>? logger = null) =>
        new(
            prepared.Identity,
            httpClient,
            authenticationSession ?? new FakeAuthenticationSession("access-token"),
            prepared.Cache,
            logger ?? NullLogger<ClientSearchCoordinator>.Instance,
            conversationRevokedAsync);

    private static SearchResultDto CreateResult(long messageId, Guid conversationId) =>
        new(
            messageId,
            conversationId,
            "Conversation",
            "Sender",
            "needle",
            DateTimeOffset.Parse("2026-08-04T03:00:00Z"),
            MatchedAttachmentFileName: null);

    private static string RepeatRune(string value, int count) =>
        string.Concat(Enumerable.Repeat(value, count));

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

    private sealed record PreparedSearch(
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

        public ValueTask<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(currentAccessToken);

        public Task<bool> TryRefreshAccessTokenAsync(
            string rejectedAccessToken,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref refreshCount);
            currentAccessToken = refreshedToken;
            return Task.FromResult(refreshedToken is not null);
        }
    }

    private sealed class DelegateHttpHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync) :
        HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => sendAsync(request, cancellationToken);
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
