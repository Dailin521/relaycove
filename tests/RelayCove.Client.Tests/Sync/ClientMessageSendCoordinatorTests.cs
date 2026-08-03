using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using RelayCove.Client.Storage;
using RelayCove.Client.Sync;
using RelayCove.Shared.Conversations;
using RelayCove.Shared.Errors;
using RelayCove.Shared.Messages;

namespace RelayCove.Client.Tests.Sync;

[Collection(SqliteTestCollection.Name)]
public sealed class ClientMessageSendCoordinatorTests : IDisposable
{
    private static readonly Guid UserId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Uri ServerBaseUri = new("https://relaycove.example/team/");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string rootDirectory = Path.Combine(
        Path.GetTempPath(),
        "RelayCove.MessageSend.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SendTextAsync_WhenContentIsInvalid_DoesNotPersistOrPost()
    {
        await using var prepared = await CreatePreparedAsync();
        var requests = 0;
        using var httpClient = new HttpClient(new DelegateHttpHandler((_, _) =>
        {
            Interlocked.Increment(ref requests);
            throw new InvalidOperationException("HTTP must not run for invalid content.");
        }));
        await using var coordinator = CreateCoordinator(prepared, httpClient);

        var outcome = await coordinator.SendTextAsync(prepared.Conversation.Id, " \t\r\n");

        Assert.Equal(ClientMessageSendStatus.ValidationFailed, outcome.Status);
        Assert.False(outcome.PendingCommitted);
        Assert.Equal(0, Volatile.Read(ref requests));
        Assert.Equal(0, Scalar(prepared.Identity, "SELECT COUNT(*) FROM LocalMessages;"));
    }

    [Fact]
    public async Task SendTextAsync_WhenCreated_PersistsBeforePostAndPromotesSameRow()
    {
        await using var prepared = await CreatePreparedAsync();
        SendMessageRequest? captured = null;
        using var httpClient = new HttpClient(new DelegateHttpHandler(async (request, token) =>
        {
            Assert.Equal("/team/api/messages", request.RequestUri!.AbsolutePath);
            Assert.Equal(1, Scalar(
                prepared.Identity,
                "SELECT COUNT(*) FROM LocalMessages WHERE ServerMessageId IS NULL;"));
            captured = await request.Content!.ReadFromJsonAsync<SendMessageRequest>(
                JsonOptions,
                token);
            return Created(CreateResponse(captured!));
        }));
        await using var coordinator = CreateCoordinator(prepared, httpClient);
        const string content = "  first line\r\nsecond line  ";

        var outcome = await coordinator.SendTextAsync(prepared.Conversation.Id, content);

        Assert.Equal(ClientMessageSendStatus.Completed, outcome.Status);
        Assert.True(outcome.PendingCommitted);
        Assert.NotNull(captured);
        Assert.Equal(content, captured.Content);
        Assert.Empty(captured.AttachmentIds);
        Assert.Empty(captured.MentionUserIds);
        var page = await prepared.Cache.ReadMessagePageAsync(
            prepared.Conversation.Id,
            beforeMessageId: null,
            limit: 50);
        Assert.Empty(page.PendingMessages);
        var message = Assert.Single(page.Messages);
        Assert.Equal(captured.ClientMessageId, message.ClientMessageId);
        Assert.Equal(content, message.Content);
        Assert.Equal(1, Scalar(prepared.Identity, "SELECT COUNT(*) FROM LocalMessages;"));
    }

    [Fact]
    public async Task RetryAsync_WhenFirstPostIsAmbiguous_ReusesExactKeyAndPayload()
    {
        await using var prepared = await CreatePreparedAsync();
        var requests = new List<SendMessageRequest>();
        using var httpClient = new HttpClient(new DelegateHttpHandler(async (request, token) =>
        {
            var sent = (await request.Content!.ReadFromJsonAsync<SendMessageRequest>(
                JsonOptions,
                token))!;
            requests.Add(sent);
            return requests.Count == 1
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : Ok(CreateResponse(sent));
        }));
        await using var coordinator = CreateCoordinator(prepared, httpClient);

        var first = await coordinator.SendTextAsync(prepared.Conversation.Id, "retry me");
        var failedPage = await prepared.Cache.ReadMessagePageAsync(
            prepared.Conversation.Id,
            beforeMessageId: null,
            limit: 50);
        var failed = Assert.Single(failedPage.PendingMessages);
        var retry = await coordinator.RetryAsync(
            prepared.Conversation.Id,
            failed.ClientMessageId);

        Assert.Equal(ClientMessageSendStatus.TransientFailure, first.Status);
        Assert.True(first.PendingCommitted);
        Assert.Equal(MessageSendStatus.Failed, failed.SendStatus);
        Assert.Equal(ClientMessageSendStatus.Completed, retry.Status);
        Assert.Equal(2, requests.Count);
        AssertRequestEqual(requests[0], requests[1]);
        Assert.Equal(1, Scalar(prepared.Identity, "SELECT COUNT(*) FROM LocalMessages;"));
        Assert.Equal(1, Scalar(
            prepared.Identity,
            "SELECT COUNT(*) FROM LocalMessages WHERE ServerMessageId IS NOT NULL;"));
    }

    [Fact]
    public async Task SendTextAsync_WhenRealtimeWins_ResponseBecomesDuplicate()
    {
        await using var prepared = await CreatePreparedAsync();
        var requestSeen = new TaskCompletionSource<SendMessageRequest>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseResponse = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var httpClient = new HttpClient(new DelegateHttpHandler(async (request, token) =>
        {
            var sent = (await request.Content!.ReadFromJsonAsync<SendMessageRequest>(
                JsonOptions,
                token))!;
            requestSeen.SetResult(sent);
            await releaseResponse.Task.WaitAsync(token);
            return Created(CreateResponse(sent));
        }));
        await using var coordinator = CreateCoordinator(prepared, httpClient);

        var send = coordinator.SendTextAsync(prepared.Conversation.Id, "race");
        var sentRequest = await requestSeen.Task;
        var realtime = CreateResponse(sentRequest);
        var realtimeMerge = await prepared.Cache.MergeIncomingMessageAsync(
            realtime,
            LocalMessageIngestionContext.Background(IncomingMessageSource.Realtime));
        releaseResponse.SetResult();
        var outcome = await send;

        Assert.Equal(IncomingMessageMergeResult.PendingPromoted, realtimeMerge.Result);
        Assert.Equal(ClientMessageSendStatus.Completed, outcome.Status);
        Assert.Equal(1, Scalar(prepared.Identity, "SELECT COUNT(*) FROM LocalMessages;"));
        Assert.Equal((long)MessageSendStatus.Sent, Scalar(
            prepared.Identity,
            "SELECT LocalSendStatus FROM LocalMessages;"));
    }

    [Fact]
    public async Task RetryAsync_WhenClickedTwice_CoalescesOnePost()
    {
        await using var prepared = await CreatePreparedAsync();
        var callCount = 0;
        var retrySeen = new TaskCompletionSource<SendMessageRequest>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRetry = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var httpClient = new HttpClient(new DelegateHttpHandler(async (request, token) =>
        {
            var sent = (await request.Content!.ReadFromJsonAsync<SendMessageRequest>(
                JsonOptions,
                token))!;
            if (Interlocked.Increment(ref callCount) == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            retrySeen.SetResult(sent);
            await releaseRetry.Task.WaitAsync(token);
            return Ok(CreateResponse(sent));
        }));
        await using var coordinator = CreateCoordinator(prepared, httpClient);
        _ = await coordinator.SendTextAsync(prepared.Conversation.Id, "coalesce");
        var page = await prepared.Cache.ReadMessagePageAsync(
            prepared.Conversation.Id,
            beforeMessageId: null,
            limit: 50);
        var clientMessageId = Assert.Single(page.PendingMessages).ClientMessageId;

        var first = coordinator.RetryAsync(prepared.Conversation.Id, clientMessageId);
        _ = await retrySeen.Task;
        var second = coordinator.RetryAsync(prepared.Conversation.Id, clientMessageId);
        Assert.Same(first, second);
        releaseRetry.SetResult();

        Assert.Equal(ClientMessageSendStatus.Completed, (await first).Status);
        Assert.Equal(2, Volatile.Read(ref callCount));
    }

    [Fact]
    public async Task SendTextAsync_WhenUnauthorized_RefreshesOnceAndReplaysSameRequest()
    {
        await using var prepared = await CreatePreparedAsync();
        var authentication = new FakeAuthenticationSession("old-token", "new-token");
        var requests = new List<SendMessageRequest>();
        using var httpClient = new HttpClient(new DelegateHttpHandler(async (request, token) =>
        {
            var sent = (await request.Content!.ReadFromJsonAsync<SendMessageRequest>(
                JsonOptions,
                token))!;
            requests.Add(sent);
            if (requests.Count == 1)
            {
                Assert.Equal("old-token", request.Headers.Authorization!.Parameter);
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            Assert.Equal("new-token", request.Headers.Authorization!.Parameter);
            return Ok(CreateResponse(sent));
        }));
        await using var coordinator = CreateCoordinator(
            prepared,
            httpClient,
            authenticationSession: authentication);

        var outcome = await coordinator.SendTextAsync(prepared.Conversation.Id, "refresh");

        Assert.Equal(ClientMessageSendStatus.Completed, outcome.Status);
        Assert.Equal(1, authentication.RefreshCount);
        Assert.Equal(2, requests.Count);
        AssertRequestEqual(requests[0], requests[1]);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, ApiErrorCodes.ValidationFailed,
        (int)ClientMessageSendStatus.ValidationFailed)]
    [InlineData(HttpStatusCode.Forbidden, ApiErrorCodes.AccessDenied,
        (int)ClientMessageSendStatus.AccessDenied)]
    [InlineData(HttpStatusCode.Conflict, ApiErrorCodes.IdempotencyKeyReuse,
        (int)ClientMessageSendStatus.IdempotencyConflict)]
    [InlineData(HttpStatusCode.TooManyRequests, ApiErrorCodes.RateLimitExceeded,
        (int)ClientMessageSendStatus.TransientFailure)]
    [InlineData(HttpStatusCode.InternalServerError, ApiErrorCodes.InternalServerError,
        (int)ClientMessageSendStatus.TransientFailure)]
    [InlineData(HttpStatusCode.Gone, ApiErrorCodes.ServiceUnavailable,
        (int)ClientMessageSendStatus.RemoteFailure)]
    public async Task SendTextAsync_WhenHttpFailureReturned_ClassifiesAndDoesNotAutoRetry(
        HttpStatusCode statusCode,
        string errorCode,
        int expectedStatusValue)
    {
        await using var prepared = await CreatePreparedAsync();
        var requestCount = 0;
        using var httpClient = new HttpClient(new DelegateHttpHandler((_, _) =>
        {
            Interlocked.Increment(ref requestCount);
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = JsonContent.Create(new ApiErrorResponse(errorCode, "failure")),
            });
        }));
        await using var coordinator = CreateCoordinator(prepared, httpClient);

        var outcome = await coordinator.SendTextAsync(prepared.Conversation.Id, "classify");
        var page = await prepared.Cache.ReadMessagePageAsync(
            prepared.Conversation.Id,
            beforeMessageId: null,
            limit: 50);

        Assert.Equal((ClientMessageSendStatus)expectedStatusValue, outcome.Status);
        Assert.True(outcome.PendingCommitted);
        Assert.Equal(1, Volatile.Read(ref requestCount));
        Assert.Equal(MessageSendStatus.Failed, Assert.Single(page.PendingMessages).SendStatus);
    }

    [Fact]
    public async Task SendTextAsync_WhenSuccessJsonIsInvalid_MarksSamePendingRowFailed()
    {
        await using var prepared = await CreatePreparedAsync();
        using var httpClient = new HttpClient(new DelegateHttpHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("{ invalid-json"),
            })));
        await using var coordinator = CreateCoordinator(prepared, httpClient);

        var outcome = await coordinator.SendTextAsync(prepared.Conversation.Id, "bad json");
        var page = await prepared.Cache.ReadMessagePageAsync(
            prepared.Conversation.Id,
            beforeMessageId: null,
            limit: 50);

        Assert.Equal(ClientMessageSendStatus.ProtocolError, outcome.Status);
        Assert.Empty(page.Messages);
        Assert.Equal(MessageSendStatus.Failed, Assert.Single(page.PendingMessages).SendStatus);
        Assert.Equal(1, Scalar(prepared.Identity, "SELECT COUNT(*) FROM LocalMessages;"));
    }

    [Fact]
    public async Task SendTextAsync_WhenRefreshFails_EndsAsAuthenticationRequiredAfterOnePost()
    {
        await using var prepared = await CreatePreparedAsync();
        var authentication = new FakeAuthenticationSession("expired-token");
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

        var outcome = await coordinator.SendTextAsync(prepared.Conversation.Id, "expired");

        Assert.Equal(ClientMessageSendStatus.AuthenticationRequired, outcome.Status);
        Assert.Equal(1, authentication.RefreshCount);
        Assert.Equal(1, Volatile.Read(ref requestCount));
    }

    [Fact]
    public async Task DisposeAsync_WhenPostIsInFlight_CancelsAndLeavesRetryableRow()
    {
        await using var prepared = await CreatePreparedAsync();
        var requestSeen = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var httpClient = new HttpClient(new DelegateHttpHandler(async (_, token) =>
        {
            requestSeen.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("The canceled POST must not complete.");
        }));
        var coordinator = CreateCoordinator(prepared, httpClient);
        var send = coordinator.SendTextAsync(prepared.Conversation.Id, "cancel safely");
        await requestSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await coordinator.DisposeAsync();
        var outcome = await send;
        var page = await prepared.Cache.ReadMessagePageAsync(
            prepared.Conversation.Id,
            beforeMessageId: null,
            limit: 50);

        Assert.Equal(ClientMessageSendStatus.Canceled, outcome.Status);
        Assert.True(outcome.PendingCommitted);
        Assert.Empty(page.Messages);
        Assert.Equal(MessageSendStatus.Failed, Assert.Single(page.PendingMessages).SendStatus);
    }

    [Fact]
    public async Task SendTextAsync_WhenStableRevocationReturned_PurgesAndNotifies()
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
            (conversationId, _) =>
            {
                revoked.Add(conversationId);
                return Task.CompletedTask;
            });

        var outcome = await coordinator.SendTextAsync(prepared.Conversation.Id, "revoked");

        Assert.Equal(ClientMessageSendStatus.AccessRevoked, outcome.Status);
        Assert.True(outcome.PendingCommitted);
        Assert.Equal([prepared.Conversation.Id], revoked);
        Assert.Equal(0, Scalar(prepared.Identity, "SELECT COUNT(*) FROM LocalMessages;"));
        Assert.Equal(0, Scalar(prepared.Identity, "SELECT COUNT(*) FROM LocalConversations;"));
        Assert.Equal(1, Scalar(prepared.Identity, "SELECT COUNT(*) FROM RevokedConversations;"));
    }

    [Fact]
    public async Task SendTextAsync_WhenSuccessSenderDoesNotMatch_RejectsBeforeMerge()
    {
        await using var prepared = await CreatePreparedAsync();
        using var httpClient = new HttpClient(new DelegateHttpHandler(async (request, token) =>
        {
            var sent = (await request.Content!.ReadFromJsonAsync<SendMessageRequest>(
                JsonOptions,
                token))!;
            return Created(CreateResponse(sent) with { SenderId = Guid.NewGuid() });
        }));
        await using var coordinator = CreateCoordinator(prepared, httpClient);

        var outcome = await coordinator.SendTextAsync(prepared.Conversation.Id, "sender mismatch");
        var page = await prepared.Cache.ReadMessagePageAsync(
            prepared.Conversation.Id,
            beforeMessageId: null,
            limit: 50);

        Assert.Equal(ClientMessageSendStatus.ProtocolError, outcome.Status);
        Assert.Empty(page.Messages);
        Assert.Equal(MessageSendStatus.Failed, Assert.Single(page.PendingMessages).SendStatus);
        Assert.Equal(1, Scalar(prepared.Identity, "SELECT COUNT(*) FROM LocalMessages;"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(rootDirectory))
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    private async Task<PreparedSend> CreatePreparedAsync()
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
        return new PreparedSend(identity, cache, conversation);
    }

    private static ClientMessageSendCoordinator CreateCoordinator(
        PreparedSend prepared,
        HttpClient httpClient,
        Func<Guid, CancellationToken, Task>? conversationRevokedAsync = null,
        IClientAuthenticationSession? authenticationSession = null) =>
        new(
            prepared.Identity,
            "Sender",
            httpClient,
            authenticationSession ?? new FakeAuthenticationSession("access-token"),
            prepared.Cache,
            NullLogger<ClientMessageSendCoordinator>.Instance,
            conversationRevokedAsync);

    private static MessageDto CreateResponse(SendMessageRequest request) => new(
        Id: 101,
        request.ClientMessageId,
        request.ConversationId,
        UserId,
        "Sender",
        request.Type,
        request.Content,
        request.ReplyToMessageId,
        Array.Empty<AttachmentDto>(),
        request.MentionUserIds,
        DateTimeOffset.Parse("2026-08-03T03:00:00Z"));

    private static void AssertRequestEqual(
        SendMessageRequest expected,
        SendMessageRequest actual)
    {
        Assert.Equal(expected.ClientMessageId, actual.ClientMessageId);
        Assert.Equal(expected.ConversationId, actual.ConversationId);
        Assert.Equal(expected.Type, actual.Type);
        Assert.Equal(expected.Content, actual.Content);
        Assert.Equal(expected.ReplyToMessageId, actual.ReplyToMessageId);
        Assert.Equal(expected.AttachmentIds, actual.AttachmentIds);
        Assert.Equal(expected.MentionUserIds, actual.MentionUserIds);
    }

    private static HttpResponseMessage Created(MessageDto value) =>
        new(HttpStatusCode.Created) { Content = JsonContent.Create(value) };

    private static HttpResponseMessage Ok(MessageDto value) =>
        new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };

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

    private sealed record PreparedSend(
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
}
