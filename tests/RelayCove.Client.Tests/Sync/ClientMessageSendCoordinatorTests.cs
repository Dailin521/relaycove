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

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task SendTextAsync_WhenReplyTargetIsInvalid_DoesNotPersistOrPost(
        long replyToMessageId)
    {
        await using var prepared = await CreatePreparedAsync();
        var requests = 0;
        using var httpClient = new HttpClient(new DelegateHttpHandler((_, _) =>
        {
            Interlocked.Increment(ref requests);
            throw new InvalidOperationException("HTTP must not run for an invalid reply target.");
        }));
        await using var coordinator = CreateCoordinator(prepared, httpClient);

        var outcome = await coordinator.SendTextAsync(
            prepared.Conversation.Id,
            "invalid reply",
            replyToMessageId);

        Assert.Equal(ClientMessageSendStatus.ValidationFailed, outcome.Status);
        Assert.False(outcome.PendingCommitted);
        Assert.Equal(0, Volatile.Read(ref requests));
        Assert.Equal(0, Scalar(prepared.Identity, "SELECT COUNT(*) FROM LocalMessages;"));
    }

    [Fact]
    public async Task SendTextAsync_WhenReplyTargetIsProvided_PersistsAndPostsExactTarget()
    {
        await using var prepared = await CreatePreparedAsync();
        const long replyToMessageId = 73;
        await SeedReplyTargetAsync(prepared, replyToMessageId);
        SendMessageRequest? captured = null;
        using var httpClient = new HttpClient(new DelegateHttpHandler(async (request, token) =>
        {
            captured = await request.Content!.ReadFromJsonAsync<SendMessageRequest>(
                JsonOptions,
                token);
            var pendingPage = await prepared.Cache.ReadMessagePageAsync(
                prepared.Conversation.Id,
                beforeMessageId: null,
                limit: 50,
                token);
            Assert.Equal(replyToMessageId, Assert.Single(pendingPage.PendingMessages)
                .ReplyToMessageId);
            return Created(CreateResponse(captured!));
        }));
        await using var coordinator = CreateCoordinator(prepared, httpClient);

        var outcome = await coordinator.SendTextAsync(
            prepared.Conversation.Id,
            "reply body",
            replyToMessageId);

        Assert.Equal(ClientMessageSendStatus.Completed, outcome.Status);
        Assert.True(outcome.PendingCommitted);
        Assert.Equal(replyToMessageId, captured!.ReplyToMessageId);
        var page = await prepared.Cache.ReadMessagePageAsync(
            prepared.Conversation.Id,
            beforeMessageId: null,
            limit: 50);
        Assert.Empty(page.PendingMessages);
        Assert.Equal(
            replyToMessageId,
            Assert.Single(page.Messages, message =>
                message.ClientMessageId == captured.ClientMessageId).ReplyToMessageId);
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
    public async Task SendTextAsync_WhenMentionsProvided_PersistsCanonicalSetBeforePost()
    {
        await using var prepared = await CreatePreparedAsync();
        var first = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var second = Guid.Parse("22222222-2222-2222-2222-222222222222");
        SendMessageRequest? captured = null;
        using var httpClient = new HttpClient(new DelegateHttpHandler(async (request, token) =>
        {
            captured = await request.Content!.ReadFromJsonAsync<SendMessageRequest>(
                JsonOptions,
                token);
            var pendingPage = await prepared.Cache.ReadMessagePageAsync(
                prepared.Conversation.Id,
                beforeMessageId: null,
                limit: 50,
                token);
            Assert.Equal(
                [first, second],
                Assert.Single(pendingPage.PendingMessages).MentionUserIds);
            return Created(CreateResponse(captured!));
        }));
        await using var coordinator = CreateCoordinator(prepared, httpClient);

        var outcome = await coordinator.SendTextAsync(
            prepared.Conversation.Id,
            "hello @first and @second",
            mentionUserIds: [second, first]);

        Assert.Equal(ClientMessageSendStatus.Completed, outcome.Status);
        Assert.True(outcome.PendingCommitted);
        Assert.Equal([first, second], captured!.MentionUserIds);
        var page = await prepared.Cache.ReadMessagePageAsync(
            prepared.Conversation.Id,
            beforeMessageId: null,
            limit: 50);
        Assert.Equal([first, second], Assert.Single(page.Messages).MentionUserIds);
    }

    [Fact]
    public async Task SendTextAsync_WhenMentionSetIsInvalid_DoesNotPersistOrPost()
    {
        await using var prepared = await CreatePreparedAsync();
        var duplicate = Guid.NewGuid();
        IReadOnlyList<Guid>[] invalidSets =
        [
            [Guid.Empty],
            [duplicate, duplicate],
            Enumerable.Range(0, 21).Select(_ => Guid.NewGuid()).ToArray(),
        ];
        var requestCount = 0;
        using var httpClient = new HttpClient(new DelegateHttpHandler((_, _) =>
        {
            Interlocked.Increment(ref requestCount);
            throw new InvalidOperationException("Invalid mentions must not reach HTTP.");
        }));
        await using var coordinator = CreateCoordinator(prepared, httpClient);

        foreach (var invalidSet in invalidSets)
        {
            var outcome = await coordinator.SendTextAsync(
                prepared.Conversation.Id,
                "invalid mentions",
                mentionUserIds: invalidSet);
            Assert.Equal(ClientMessageSendStatus.ValidationFailed, outcome.Status);
            Assert.False(outcome.PendingCommitted);
        }

        Assert.Equal(0, Volatile.Read(ref requestCount));
        Assert.Equal(0, Scalar(prepared.Identity, "SELECT COUNT(*) FROM LocalMessages;"));
    }

    [Fact]
    public async Task SendTextAsync_WhenResponseMentionOrderDiffers_MarksPendingFailed()
    {
        await using var prepared = await CreatePreparedAsync();
        var first = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var second = Guid.Parse("22222222-2222-2222-2222-222222222222");
        using var httpClient = new HttpClient(new DelegateHttpHandler(async (request, token) =>
        {
            var sent = (await request.Content!.ReadFromJsonAsync<SendMessageRequest>(
                JsonOptions,
                token))!;
            return Created(CreateResponse(sent) with
            {
                MentionUserIds = sent.MentionUserIds.Reverse().ToArray(),
            });
        }));
        await using var coordinator = CreateCoordinator(prepared, httpClient);

        var outcome = await coordinator.SendTextAsync(
            prepared.Conversation.Id,
            "protocol order",
            mentionUserIds: [second, first]);

        Assert.Equal(ClientMessageSendStatus.ProtocolError, outcome.Status);
        Assert.True(outcome.PendingCommitted);
        var page = await prepared.Cache.ReadMessagePageAsync(
            prepared.Conversation.Id,
            beforeMessageId: null,
            limit: 50);
        var failed = Assert.Single(page.PendingMessages);
        Assert.Equal(MessageSendStatus.Failed, failed.SendStatus);
        Assert.Equal([first, second], failed.MentionUserIds);
    }

    [Fact]
    public async Task RetryAsync_WhenFirstPostIsAmbiguous_ReusesExactKeyAndPayload()
    {
        await using var prepared = await CreatePreparedAsync();
        await SeedReplyTargetAsync(prepared, messageId: 88);
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

        var firstMention = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var secondMention = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var first = await coordinator.SendTextAsync(
            prepared.Conversation.Id,
            "retry me",
            replyToMessageId: 88,
            mentionUserIds: [secondMention, firstMention]);
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
        Assert.Equal(88, failed.ReplyToMessageId);
        Assert.Equal([firstMention, secondMention], failed.MentionUserIds);
        Assert.Equal(ClientMessageSendStatus.Completed, retry.Status);
        Assert.Equal(2, requests.Count);
        AssertRequestEqual(requests[0], requests[1]);
        Assert.Equal([firstMention, secondMention], requests[0].MentionUserIds);
        Assert.Equal(2, Scalar(prepared.Identity, "SELECT COUNT(*) FROM LocalMessages;"));
        Assert.Equal(2, Scalar(
            prepared.Identity,
            "SELECT COUNT(*) FROM LocalMessages WHERE ServerMessageId IS NOT NULL;"));
    }

    [Fact]
    public async Task RetryAsync_AfterCacheRestart_ReusesDurableMentionSet()
    {
        var prepared = await CreatePreparedAsync();
        var firstMention = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var secondMention = Guid.Parse("22222222-2222-2222-2222-222222222222");
        using (var failingHttpClient = new HttpClient(new DelegateHttpHandler((_, _) =>
                   Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)))))
        {
            await using var firstCoordinator = CreateCoordinator(prepared, failingHttpClient);
            var first = await firstCoordinator.SendTextAsync(
                prepared.Conversation.Id,
                "restart retry",
                mentionUserIds: [secondMention, firstMention]);
            Assert.Equal(ClientMessageSendStatus.TransientFailure, first.Status);
        }

        var beforeRestart = await prepared.Cache.ReadMessagePageAsync(
            prepared.Conversation.Id,
            beforeMessageId: null,
            limit: 50);
        var clientMessageId = Assert.Single(beforeRestart.PendingMessages).ClientMessageId;
        await prepared.Cache.DisposeAsync();

        await using var reopenedCache = await AccountScopedLocalCache.CreateAsync(
            prepared.Identity,
            NullLogger<AccountScopedLocalCache>.Instance);
        Assert.Equal(
            LocalCacheOperationStatus.Ready,
            await reopenedCache.ApplyAuthoritativeConversationSnapshotAsync(
                new ConversationListResponse([prepared.Conversation], Complete: true)));
        var reopened = new PreparedSend(
            prepared.Identity,
            reopenedCache,
            prepared.Conversation);
        SendMessageRequest? retriedRequest = null;
        using var successHttpClient = new HttpClient(new DelegateHttpHandler(async (request, token) =>
        {
            retriedRequest = await request.Content!.ReadFromJsonAsync<SendMessageRequest>(
                JsonOptions,
                token);
            return Ok(CreateResponse(retriedRequest!));
        }));
        await using var secondCoordinator = CreateCoordinator(reopened, successHttpClient);

        var retry = await secondCoordinator.RetryAsync(
            prepared.Conversation.Id,
            clientMessageId);

        Assert.Equal(ClientMessageSendStatus.Completed, retry.Status);
        Assert.Equal([firstMention, secondMention], retriedRequest!.MentionUserIds);
        var afterRestart = await reopenedCache.ReadMessagePageAsync(
            prepared.Conversation.Id,
            beforeMessageId: null,
            limit: 50);
        Assert.Empty(afterRestart.PendingMessages);
        Assert.Equal(
            [firstMention, secondMention],
            Assert.Single(afterRestart.Messages).MentionUserIds);
    }

    [Fact]
    public async Task SendTextAsync_WhenRealtimeWins_ResponseBecomesDuplicate()
    {
        await using var prepared = await CreatePreparedAsync();
        await SeedReplyTargetAsync(prepared, messageId: 77);
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

        var send = coordinator.SendTextAsync(
            prepared.Conversation.Id,
            "race",
            replyToMessageId: 77);
        var sentRequest = await requestSeen.Task;
        Assert.Equal(77, sentRequest.ReplyToMessageId);
        var realtime = CreateResponse(sentRequest);
        var realtimeMerge = await prepared.Cache.MergeIncomingMessageAsync(
            realtime,
            LocalMessageIngestionContext.Background(IncomingMessageSource.Realtime));
        releaseResponse.SetResult();
        var outcome = await send;

        Assert.Equal(IncomingMessageMergeResult.PendingPromoted, realtimeMerge.Result);
        Assert.Equal(ClientMessageSendStatus.Completed, outcome.Status);
        Assert.Equal(2, Scalar(prepared.Identity, "SELECT COUNT(*) FROM LocalMessages;"));
        Assert.Equal((long)MessageSendStatus.Sent, Scalar(
            prepared.Identity,
            "SELECT LocalSendStatus FROM LocalMessages WHERE ClientMessageId = '" +
            sentRequest.ClientMessageId.ToString("D") + "';"));
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

    [Fact]
    public async Task SendAttachmentsAsync_WhenLaterUploadFails_CleansOnlyFlightReservations()
    {
        await using var prepared = await CreatePreparedAsync();
        var firstAttachment = CreateAttachment(Guid.NewGuid(), "first.txt", "text/plain", 1);
        var uploadCount = 0;
        var messageCount = 0;
        var progress = new List<ClientAttachmentSendProgress>();
        using var httpClient = new HttpClient(new DelegateHttpHandler(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/api/attachments", StringComparison.Ordinal))
            {
                var currentUpload = Interlocked.Increment(ref uploadCount);
                if (currentUpload == 1)
                {
                    var multipart = Assert.IsType<MultipartFormDataContent>(request.Content);
                    _ = await Assert.Single(multipart).ReadAsByteArrayAsync(token);
                }

                return currentUpload == 1
                    ? Created(firstAttachment)
                    : new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }

            Interlocked.Increment(ref messageCount);
            throw new InvalidOperationException("Message POST must not run after partial upload failure.");
        }));
        await using var coordinator = CreateCoordinator(prepared, httpClient);

        var outcome = await coordinator.SendAttachmentsAsync(
            prepared.Conversation.Id,
            MessageType.File,
            [CreateSource("first.txt", "text/plain"), CreateSource("second.txt", "text/plain")],
            progress: new CollectingProgress(progress));

        Assert.Equal(ClientMessageSendStatus.TransientFailure, outcome.Status);
        Assert.False(outcome.PendingCommitted);
        Assert.Equal(2, Volatile.Read(ref uploadCount));
        Assert.Equal(0, Volatile.Read(ref messageCount));
        Assert.Equal(0, Scalar(prepared.Identity, "SELECT COUNT(*) FROM LocalAttachments;"));
        Assert.Equal(0, Scalar(prepared.Identity, "SELECT COUNT(*) FROM LocalMessages;"));
        Assert.Equal(1, progress.Max(value => value.BytesCopied));
        Assert.DoesNotContain(progress, value =>
            value.Stage == ClientAttachmentSendProgressStage.Finalizing);
        Assert.True(progress.Zip(progress.Skip(1)).All(pair =>
            pair.First.BytesCopied <= pair.Second.BytesCopied &&
            pair.First.Percent <= pair.Second.Percent));
    }

    [Fact]
    public async Task SendAttachmentsAsync_WhenPendingFails_RetryReusesBoundAttachmentWithoutUploading()
    {
        await using var prepared = await CreatePreparedAsync();
        var attachment = CreateAttachment(Guid.NewGuid(), "one.txt", "text/plain", 1);
        var uploadCount = 0;
        var messageCount = 0;
        SendMessageRequest? firstRequest = null;
        using var httpClient = new HttpClient(new DelegateHttpHandler(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/api/attachments", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref uploadCount);
                return Created(attachment);
            }

            var sent = (await request.Content!.ReadFromJsonAsync<SendMessageRequest>(
                JsonOptions,
                token))!;
            if (Interlocked.Increment(ref messageCount) == 1)
            {
                firstRequest = sent;
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            return Created(CreateResponse(sent) with { Attachments = [attachment] });
        }));
        Guid clientMessageId;
        await using (var firstCoordinator = CreateCoordinator(prepared, httpClient))
        {
            var first = await firstCoordinator.SendAttachmentsAsync(
                prepared.Conversation.Id,
                MessageType.File,
                [CreateSource("one.txt", "text/plain")]);
            Assert.Equal(ClientMessageSendStatus.TransientFailure, first.Status);
            Assert.True(first.PendingCommitted);
            clientMessageId = firstRequest!.ClientMessageId;
        }

        await using var retryCoordinator = CreateCoordinator(prepared, httpClient);
        var retry = await retryCoordinator.RetryAsync(prepared.Conversation.Id, clientMessageId);

        Assert.Equal(ClientMessageSendStatus.Completed, retry.Status);
        Assert.True(retry.PendingCommitted);
        Assert.Equal(1, Volatile.Read(ref uploadCount));
        Assert.Equal(2, Volatile.Read(ref messageCount));
        var page = await prepared.Cache.ReadMessagePageAsync(
            prepared.Conversation.Id,
            beforeMessageId: null,
            limit: 50);
        Assert.Empty(page.PendingMessages);
        Assert.Equal(attachment.Id, Assert.Single(page.Messages).Attachments.Single().Id);
    }

    [Fact]
    public async Task SendAttachmentsAsync_WhenCompleted_ReportsMonotonicProgressThenFinalizing()
    {
        await using var prepared = await CreatePreparedAsync();
        var attachments = new[]
        {
            CreateAttachment(Guid.Parse("11111111-1111-1111-1111-111111111111"), "one.txt", "text/plain", 1),
            CreateAttachment(Guid.Parse("22222222-2222-2222-2222-222222222222"), "two.txt", "text/plain", 1),
        };
        var uploadIndex = 0;
        var progress = new List<ClientAttachmentSendProgress>();
        using var httpClient = new HttpClient(new DelegateHttpHandler(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/api/attachments", StringComparison.Ordinal))
            {
                var multipart = Assert.IsType<MultipartFormDataContent>(request.Content);
                _ = await Assert.Single(multipart).ReadAsByteArrayAsync(token);
                return Created(attachments[uploadIndex++]);
            }

            var sent = (await request.Content!.ReadFromJsonAsync<SendMessageRequest>(JsonOptions, token))!;
            return Created(CreateResponse(sent) with { Attachments = attachments });
        }));
        await using var coordinator = CreateCoordinator(prepared, httpClient);

        var result = await coordinator.SendAttachmentsAsync(
            prepared.Conversation.Id,
            MessageType.File,
            [CreateSource("one.txt", "text/plain"), CreateSource("two.txt", "text/plain")],
            progress: new CollectingProgress(progress));

        Assert.Equal(ClientMessageSendStatus.Completed, result.Status);
        Assert.Equal(ClientAttachmentSendProgressStage.Finalizing, progress[^1].Stage);
        Assert.Equal(100, progress[^1].Percent);
        Assert.Equal(2, progress[^1].BytesCopied);
        Assert.True(progress.Zip(progress.Skip(1)).All(pair =>
            pair.First.BytesCopied <= pair.Second.BytesCopied &&
            pair.First.Percent <= pair.Second.Percent));
    }

    [Fact]
    public async Task SendAttachmentsAsync_WhenStable401ReopensSource_KeepsAggregateProgressMonotonic()
    {
        await using var prepared = await CreatePreparedAsync();
        var authentication = new FakeAuthenticationSession("old-token", "new-token");
        var attachment = CreateAttachment(Guid.NewGuid(), "retry.bin", "application/octet-stream", 3);
        var source = new ClientAttachmentUploadSource(
            "retry.bin",
            "application/octet-stream",
            size: 3,
            _ => ValueTask.FromResult<Stream>(new MemoryStream([1, 2, 3], writable: false)));
        var progress = new List<ClientAttachmentSendProgress>();
        var uploadRequests = 0;
        using var httpClient = new HttpClient(new DelegateHttpHandler(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/api/attachments", StringComparison.Ordinal))
            {
                var multipart = Assert.IsType<MultipartFormDataContent>(request.Content);
                _ = await Assert.Single(multipart).ReadAsByteArrayAsync(token);
                if (Interlocked.Increment(ref uploadRequests) == 1)
                {
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized)
                    {
                        Content = JsonContent.Create(new ApiErrorResponse(
                            ApiErrorCodes.AuthenticationRequired,
                            "Authentication is required.")),
                    };
                }

                return Created(attachment);
            }

            var sent = (await request.Content!.ReadFromJsonAsync<SendMessageRequest>(
                JsonOptions,
                token))!;
            return Created(CreateResponse(sent) with { Attachments = [attachment] });
        }));
        await using var coordinator = CreateCoordinator(
            prepared,
            httpClient,
            authenticationSession: authentication);

        var result = await coordinator.SendAttachmentsAsync(
            prepared.Conversation.Id,
            MessageType.File,
            [source],
            progress: new CollectingProgress(progress));

        Assert.Equal(ClientMessageSendStatus.Completed, result.Status);
        Assert.Equal(1, authentication.RefreshCount);
        Assert.Equal(2, Volatile.Read(ref uploadRequests));
        Assert.Equal(ClientAttachmentSendProgressStage.Finalizing, progress[^1].Stage);
        Assert.True(progress.Zip(progress.Skip(1)).All(pair =>
            pair.First.BytesCopied <= pair.Second.BytesCopied &&
            pair.First.Percent <= pair.Second.Percent));
    }

    [Fact]
    public async Task SendAttachmentsAsync_WhenUploadFails_DoesNotReportFinalizing()
    {
        await using var prepared = await CreatePreparedAsync();
        var progress = new List<ClientAttachmentSendProgress>();
        using var httpClient = new HttpClient(new DelegateHttpHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))));
        await using var coordinator = CreateCoordinator(prepared, httpClient);

        var result = await coordinator.SendAttachmentsAsync(
            prepared.Conversation.Id,
            MessageType.File,
            [CreateSource("one.txt", "text/plain")],
            progress: new ThrowingProgress(progress));

        Assert.Equal(ClientMessageSendStatus.TransientFailure, result.Status);
        Assert.DoesNotContain(progress, value =>
            value.Stage == ClientAttachmentSendProgressStage.Finalizing);
    }

    [Fact]
    public async Task SendAttachmentsAsync_WhenCanceledAfterContentCopy_DoesNotReportFinalizing()
    {
        await using var prepared = await CreatePreparedAsync();
        using var cancellation = new CancellationTokenSource();
        var progress = new List<ClientAttachmentSendProgress>();
        using var httpClient = new HttpClient(new DelegateHttpHandler(async (request, token) =>
        {
            var multipart = Assert.IsType<MultipartFormDataContent>(request.Content);
            _ = await Assert.Single(multipart).ReadAsByteArrayAsync(token);
            cancellation.Cancel();
            throw new OperationCanceledException(token);
        }));
        await using var coordinator = CreateCoordinator(prepared, httpClient);

        var result = await coordinator.SendAttachmentsAsync(
            prepared.Conversation.Id,
            MessageType.File,
            [CreateSource("one.txt", "text/plain")],
            cancellationToken: cancellation.Token,
            progress: new CollectingProgress(progress));

        Assert.Equal(ClientMessageSendStatus.Canceled, result.Status);
        Assert.Equal(1, progress.Max(value => value.BytesCopied));
        Assert.DoesNotContain(progress, value =>
            value.Stage == ClientAttachmentSendProgressStage.Finalizing);
        Assert.Equal(0, Scalar(prepared.Identity, "SELECT COUNT(*) FROM LocalMessages;"));
    }

    [Fact]
    public async Task SendAttachmentsAsync_WhenTenImagesWithReplyAndMentions_SendsEachUploadOnceAndPostsCanonicalWirePayload()
    {
        await using var prepared = await CreatePreparedAsync();
        const long replyToMessageId = 73;
        await SeedReplyTargetAsync(prepared, replyToMessageId);
        var firstMention = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var secondMention = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var attachments = Enumerable.Range(1, 10)
            .Select(index => CreateAttachment(
                Guid.Parse($"00000000-0000-0000-0000-{index:D12}"),
                $"图片-{index}.png",
                "image/png",
                1))
            .ToArray();
        var uploads = 0;
        SendMessageRequest? messageRequest = null;
        using var httpClient = new HttpClient(new DelegateHttpHandler(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/api/attachments", StringComparison.Ordinal))
            {
                var uploadIndex = Interlocked.Increment(ref uploads) - 1;
                Assert.Equal(HttpMethod.Post, request.Method);
                var multipart = Assert.IsType<MultipartFormDataContent>(request.Content);
                var part = Assert.Single(multipart);
                Assert.Equal("image/png", part.Headers.ContentType!.MediaType);
                Assert.Equal("file", part.Headers.ContentDisposition!.Name!.Trim('\"'));
                Assert.Equal($"图片-{uploadIndex + 1}.png", part.Headers.ContentDisposition.FileName!.Trim('\"'));
                return Created(attachments[uploadIndex]);
            }

            messageRequest = (await request.Content!.ReadFromJsonAsync<SendMessageRequest>(
                JsonOptions,
                token))!;
            var responseAttachments = messageRequest.AttachmentIds
                .Select(id => attachments.Single(attachment => attachment.Id == id))
                .ToArray();
            return Created(CreateResponse(messageRequest) with { Attachments = responseAttachments });
        }));
        await using var coordinator = CreateCoordinator(prepared, httpClient);

        var outcome = await coordinator.SendAttachmentsAsync(
            prepared.Conversation.Id,
            MessageType.Image,
            attachments.Select(attachment => CreateSource(attachment.OriginalFileName, attachment.ContentType)).ToArray(),
            replyToMessageId,
            [secondMention, firstMention]);

        Assert.Equal(ClientMessageSendStatus.Completed, outcome.Status);
        Assert.True(outcome.PendingCommitted);
        Assert.Equal(10, Volatile.Read(ref uploads));
        Assert.NotNull(messageRequest);
        Assert.Equal(MessageType.Image, messageRequest!.Type);
        Assert.Null(messageRequest.Content);
        Assert.Equal(replyToMessageId, messageRequest.ReplyToMessageId);
        Assert.Equal(attachments.Select(attachment => attachment.Id).Order().ToArray(), messageRequest.AttachmentIds);
        Assert.Equal([firstMention, secondMention], messageRequest.MentionUserIds);
        var page = await prepared.Cache.ReadMessagePageAsync(
            prepared.Conversation.Id,
            beforeMessageId: null,
            limit: 50);
        var message = Assert.Single(page.Messages, message =>
            message.ClientMessageId == messageRequest.ClientMessageId);
        Assert.Equal(messageRequest.AttachmentIds, message.Attachments.Select(attachment => attachment.Id));
    }

    [Fact]
    public async Task RetryAsync_AfterProcessRestart_ReusesAttachmentPendingIdentityWithoutReupload()
    {
        var identity = AccountScopeIdentity.Create(ServerBaseUri, UserId, rootDirectory);
        var conversation = new ConversationDto(
            Guid.NewGuid(),
            ConversationType.PrivateChannel,
            "Conversation",
            AvatarUrl: null,
            DateTimeOffset.Parse("2026-08-03T01:00:00Z"),
            DateTimeOffset.Parse("2026-08-03T02:00:00Z"),
            LastMessageId: 0,
            LastReadMessageId: 0,
            UnreadCount: 0);
        var attachment = CreateAttachment(Guid.NewGuid(), "restart.bin", "application/octet-stream", 1);
        var uploadCount = 0;
        var messageRequests = new List<SendMessageRequest>();
        var messageAttempt = 0;
        using var httpClient = new HttpClient(new DelegateHttpHandler(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/api/attachments", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref uploadCount);
                return Created(attachment);
            }

            var sent = (await request.Content!.ReadFromJsonAsync<SendMessageRequest>(JsonOptions, token))!;
            messageRequests.Add(sent);
            return Interlocked.Increment(ref messageAttempt) == 1
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : Created(CreateResponse(sent) with { Attachments = [attachment] });
        }));

        Guid clientMessageId;
        await using (var firstCache = await AccountScopedLocalCache.CreateAsync(
            identity,
            NullLogger<AccountScopedLocalCache>.Instance))
        {
            Assert.Equal(LocalCacheOperationStatus.Ready, await firstCache.ApplyAuthoritativeConversationSnapshotAsync(
                new ConversationListResponse([conversation], Complete: true)));
            await using var firstCoordinator = CreateCoordinator(
                new PreparedSend(identity, firstCache, conversation),
                httpClient);
            var first = await firstCoordinator.SendAttachmentsAsync(
                conversation.Id,
                MessageType.File,
                [CreateSource(attachment.OriginalFileName, attachment.ContentType)]);
            Assert.Equal(ClientMessageSendStatus.TransientFailure, first.Status);
            Assert.True(first.PendingCommitted);
            clientMessageId = Assert.Single(messageRequests).ClientMessageId;
        }

        AccountScopedLocalCache.ResetProcessStateForTest(identity);
        await using var reopenedCache = await AccountScopedLocalCache.CreateAsync(
            identity,
            NullLogger<AccountScopedLocalCache>.Instance);
        Assert.Equal(LocalCacheOperationStatus.Ready, await reopenedCache.ApplyAuthoritativeConversationSnapshotAsync(
            new ConversationListResponse([conversation], Complete: true)));
        await using var reopenedCoordinator = CreateCoordinator(
            new PreparedSend(identity, reopenedCache, conversation),
            httpClient);

        var retry = await reopenedCoordinator.RetryAsync(conversation.Id, clientMessageId);

        Assert.Equal(ClientMessageSendStatus.Completed, retry.Status);
        Assert.Equal(1, Volatile.Read(ref uploadCount));
        Assert.Equal(2, messageRequests.Count);
        Assert.Equal(messageRequests[0].ClientMessageId, messageRequests[1].ClientMessageId);
        Assert.Equal(messageRequests[0].AttachmentIds, messageRequests[1].AttachmentIds);
        Assert.Equal(messageRequests[0].MentionUserIds, messageRequests[1].MentionUserIds);
    }

    [Fact]
    public async Task SendAttachmentsAsync_WhenRealtimePromotesBeforeResponse_ResponseIsDuplicate()
    {
        await using var prepared = await CreatePreparedAsync();
        var attachment = CreateAttachment(Guid.NewGuid(), "race.txt", "text/plain", 1);
        var messageObserved = NewSignal();
        var releaseResponse = NewSignal();
        var uploadCount = 0;
        SendMessageRequest? sentRequest = null;
        using var httpClient = new HttpClient(new DelegateHttpHandler(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/api/attachments", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref uploadCount);
                return Created(attachment);
            }

            sentRequest = (await request.Content!.ReadFromJsonAsync<SendMessageRequest>(JsonOptions, token))!;
            messageObserved.SetResult();
            await releaseResponse.Task.WaitAsync(token);
            return Created(CreateResponse(sentRequest) with { Attachments = [attachment] });
        }));
        await using var coordinator = CreateCoordinator(prepared, httpClient);

        var send = coordinator.SendAttachmentsAsync(
            prepared.Conversation.Id,
            MessageType.File,
            [CreateSource(attachment.OriginalFileName, attachment.ContentType)]);
        await messageObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var realtime = await prepared.Cache.MergeIncomingMessageAsync(
            CreateResponse(sentRequest!) with { Attachments = [attachment] },
            LocalMessageIngestionContext.Background(IncomingMessageSource.Realtime));
        releaseResponse.SetResult();
        var outcome = await send;

        Assert.Equal(IncomingMessageMergeResult.PendingPromoted, realtime.Result);
        Assert.Equal(ClientMessageSendStatus.Completed, outcome.Status);
        Assert.Equal(1, Volatile.Read(ref uploadCount));
        var page = await prepared.Cache.ReadMessagePageAsync(
            prepared.Conversation.Id,
            beforeMessageId: null,
            limit: 50);
        Assert.Empty(page.PendingMessages);
        Assert.Equal(attachment.Id, Assert.Single(page.Messages).Attachments.Single().Id);
    }

    [Fact]
    public async Task SendAttachmentsAsync_WhenResponseMergeIsRejected_ReportsCommittedAndKeepsBoundAttachment()
    {
        await using var prepared = await CreatePreparedAsync();
        var attachment = CreateAttachment(Guid.NewGuid(), "bound.txt", "text/plain", 1);
        using var httpClient = new HttpClient(new DelegateHttpHandler(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/api/attachments", StringComparison.Ordinal))
            {
                return Created(attachment);
            }

            var sent = (await request.Content!.ReadFromJsonAsync<SendMessageRequest>(JsonOptions, token))!;
            return Created(CreateResponse(sent) with
            {
                SenderId = Guid.NewGuid(),
                Attachments = [attachment],
            });
        }));
        await using var coordinator = CreateCoordinator(prepared, httpClient);

        var outcome = await coordinator.SendAttachmentsAsync(
            prepared.Conversation.Id,
            MessageType.File,
            [CreateSource(attachment.OriginalFileName, attachment.ContentType)]);

        Assert.Equal(ClientMessageSendStatus.ProtocolError, outcome.Status);
        Assert.True(outcome.PendingCommitted);
        Assert.Equal(1, Scalar(prepared.Identity, "SELECT COUNT(*) FROM LocalAttachments;"));
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

    private static async Task SeedReplyTargetAsync(
        PreparedSend prepared,
        long messageId)
    {
        var outcome = await prepared.Cache.MergeIncomingMessageAsync(
            new MessageDto(
                messageId,
                Guid.NewGuid(),
                prepared.Conversation.Id,
                Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                "Reply Target",
                MessageType.Text,
                "target",
                ReplyToMessageId: null,
                Array.Empty<AttachmentDto>(),
                Array.Empty<Guid>(),
                DateTimeOffset.Parse("2026-08-03T02:00:00Z")),
            LocalMessageIngestionContext.Background(IncomingMessageSource.Sync));
        Assert.Equal(IncomingMessageMergeResult.Inserted, outcome.Result);
    }

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

    private static ClientAttachmentUploadSource CreateSource(
        string fileName,
        string contentType) =>
        new(
            fileName,
            contentType,
            size: 1,
            _ => ValueTask.FromResult<Stream>(new MemoryStream([0x42], writable: false)));

    private static AttachmentDto CreateAttachment(
        Guid id,
        string fileName,
        string contentType,
        long size) =>
        new(
            id,
            fileName,
            contentType,
            size,
            $"/api/attachments/{id:D}/download",
            ThumbnailUrl: null);

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

    private static HttpResponseMessage Created(AttachmentDto value)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = JsonContent.Create(value),
        };
        response.Headers.Location = new Uri($"/api/attachments/{value.Id:D}", UriKind.Relative);
        return response;
    }

    private static HttpResponseMessage Ok(MessageDto value) =>
        new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

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

    private sealed class ThrowingProgress(List<ClientAttachmentSendProgress> values) :
        IProgress<ClientAttachmentSendProgress>
    {
        public void Report(ClientAttachmentSendProgress value)
        {
            values.Add(value);
            throw new InvalidOperationException("receiver failure");
        }
    }

    private sealed class CollectingProgress(List<ClientAttachmentSendProgress> values) :
        IProgress<ClientAttachmentSendProgress>
    {
        public void Report(ClientAttachmentSendProgress value) => values.Add(value);
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
