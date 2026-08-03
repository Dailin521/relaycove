using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using RelayCove.Client.Storage;
using RelayCove.Client.Sync;
using RelayCove.Shared.Conversations;
using RelayCove.Shared.Errors;
using RelayCove.Shared.Messages;

namespace RelayCove.Client.Tests.Sync;

public sealed class ClientReadThroughCoordinatorTests : IDisposable
{
    private static readonly Guid UserId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid OtherUserId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Uri ServerBaseUri = new("https://relaycove.example/team/");
    private readonly string rootDirectory = Path.Combine(
        Path.GetTempPath(),
        "RelayCove.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task TriggerAsync_WhenCursorCatchesPending_SendsExactConversationTargetsOnce()
    {
        var prepared = await CreatePendingAsync(rawPendingMessageId: 63);
        var requestedMessageIds = new ConcurrentQueue<long>();
        using var httpClient = new HttpClient(new DelegateHttpHandler(async (request, cancellationToken) =>
        {
            Assert.EndsWith(
                $"/api/conversations/{prepared.Conversation.Id:D}/read",
                request.RequestUri!.AbsolutePath,
                StringComparison.Ordinal);
            Assert.Equal(
                new AuthenticationHeaderValue("Bearer", "access-token"),
                request.Headers.Authorization);
            var payload = await request.Content!
                .ReadFromJsonAsync<MarkConversationReadRequest>(cancellationToken);
            requestedMessageIds.Enqueue(payload!.MessageId);
            return Ok(new ConversationReadReceipt(prepared.Conversation.Id, payload.MessageId));
        }));
        await using var coordinator = CreateCoordinator(
            prepared,
            httpClient,
            new FakeAuthenticationSession("access-token"));

        var first = await coordinator.TriggerAsync();
        var gapMessage = CreateMessage(61, prepared.Conversation.Id);
        Assert.Equal(
            LocalCacheOperationStatus.Ready,
            (await prepared.Cache.ApplySyncPageAsync(
                new SyncResponse(
                    [gapMessage, prepared.PendingMessage],
                    NextCursor: 63,
                    SnapshotUpperBound: 63,
                    HasMore: false),
                expectedCursor: 60,
                expectedSnapshotUpperBound: null,
                new LocalMessageIngestionContext(
                    IncomingMessageSource.Sync,
                    prepared.Conversation.Id))).Status);
        var caughtUp = await coordinator.TriggerAsync();
        var afterClear = await coordinator.TriggerAsync();

        Assert.Equal(ClientReadThroughRunStatus.Completed, first.Status);
        Assert.Equal((1, 1), (first.RequestCount, first.ReceiptCount));
        Assert.Equal((1, 1), (caughtUp.RequestCount, caughtUp.ReceiptCount));
        Assert.Equal((0, 0), (afterClear.RequestCount, afterClear.ReceiptCount));
        Assert.Equal([50L, 63L], requestedMessageIds.ToArray());
        Assert.Equal(
            new ConversationAttention(LastReadMessageId: 63, PendingReadThroughMessageId: null),
            ReadConversationAttention(prepared.Identity, prepared.Conversation.Id));
    }

    [Fact]
    public async Task ReadPendingBatch_WhenFirstPageHasNoSafeTargets_ContinuesByRawConversationRows()
    {
        var identity = AccountScopeIdentity.Create(ServerBaseUri, UserId, rootDirectory);
        await using var cache = await AccountScopedLocalCache.CreateAsync(
            identity,
            NullLogger<AccountScopedLocalCache>.Instance);
        var conversations = Enumerable.Range(1, 102)
            .Select(index => CreateConversationWithId(
                Guid.Parse($"00000000-0000-0000-0000-{index:x12}")))
            .ToArray();
        conversations[^1] = conversations[^1] with
        {
            LastMessageId = 1,
            UnreadCount = 1,
        };
        Assert.Equal(
            LocalCacheOperationStatus.Ready,
            await cache.ApplyAuthoritativeConversationSnapshotAsync(
                new ConversationListResponse(conversations, Complete: true)));
        var message = CreateMessage(1, conversations[^1].Id);
        Assert.Equal(
            LocalCacheOperationStatus.Ready,
            (await cache.ApplySyncPageAsync(
                new SyncResponse([message], 1, 1, HasMore: false),
                0,
                null)).Status);
        for (var index = 0; index < conversations.Length - 1; index++)
        {
            var conversation = conversations[index];
            Assert.Equal(
                LocalCacheOperationStatus.Ready,
                (await cache.MergeIncomingMessageAsync(
                    CreateMessage(index + 2, conversation.Id),
                    new LocalMessageIngestionContext(
                        IncomingMessageSource.Realtime,
                        conversation.Id))).Status);
        }

        await cache.MergeIncomingMessageAsync(
            message,
            new LocalMessageIngestionContext(
                IncomingMessageSource.Realtime,
                conversations[^1].Id));

        var first = await cache.ReadPendingReadThroughBatchAsync(null, 100);
        var second = await cache.ReadPendingReadThroughBatchAsync(
            first.ContinuationConversationId,
            100);

        Assert.Equal(LocalCacheOperationStatus.Ready, first.Status);
        Assert.Empty(first.Targets);
        Assert.NotNull(first.ContinuationConversationId);
        var target = Assert.Single(second.Targets);
        Assert.Equal(conversations[^1].Id, target.ConversationId);
        Assert.Equal(1, target.SafeMessageId);
        Assert.Null(second.ContinuationConversationId);
    }

    [Fact]
    public async Task TriggerAsync_WhenMoreThanOneBatchIsPending_UploadsEveryConversation()
    {
        var identity = AccountScopeIdentity.Create(ServerBaseUri, UserId, rootDirectory);
        await using var cache = await AccountScopedLocalCache.CreateAsync(
            identity,
            NullLogger<AccountScopedLocalCache>.Instance);
        var conversations = Enumerable.Range(1, 102)
            .Select(index => CreateConversationWithId(
                Guid.Parse($"00000000-0000-0000-0000-{index:x12}")) with
            {
                LastMessageId = index,
                UnreadCount = 1,
            })
            .ToArray();
        var messages = conversations
            .Select((conversation, index) => CreateMessage(index + 1, conversation.Id))
            .ToArray();
        Assert.Equal(
            LocalCacheOperationStatus.Ready,
            await cache.ApplyAuthoritativeConversationSnapshotAsync(
                new ConversationListResponse(conversations, Complete: true)));
        Assert.Equal(
            LocalCacheOperationStatus.Ready,
            (await cache.ApplySyncPageAsync(
                new SyncResponse(
                    messages[..100],
                    NextCursor: 100,
                    SnapshotUpperBound: 102,
                    HasMore: true),
                expectedCursor: 0,
                expectedSnapshotUpperBound: null)).Status);
        Assert.Equal(
            LocalCacheOperationStatus.Ready,
            (await cache.ApplySyncPageAsync(
                new SyncResponse(
                    messages[100..],
                    NextCursor: 102,
                    SnapshotUpperBound: 102,
                    HasMore: false),
                expectedCursor: 100,
                expectedSnapshotUpperBound: 102)).Status);
        foreach (var message in messages)
        {
            Assert.Equal(
                LocalCacheOperationStatus.Ready,
                (await cache.MergeIncomingMessageAsync(
                    message,
                    new LocalMessageIngestionContext(
                        IncomingMessageSource.Realtime,
                        message.ConversationId))).Status);
        }

        var requestedConversations = new ConcurrentDictionary<Guid, byte>();
        using var httpClient = new HttpClient(new DelegateHttpHandler(
            async (request, cancellationToken) =>
            {
                var pathSegments = request.RequestUri!.AbsolutePath.Split('/');
                var conversationId = Guid.Parse(pathSegments[^2]);
                var payload = await request.Content!
                    .ReadFromJsonAsync<MarkConversationReadRequest>(cancellationToken);
                requestedConversations.TryAdd(conversationId, 0);
                return Ok(new ConversationReadReceipt(conversationId, payload!.MessageId));
            }));
        await using var coordinator = new ClientReadThroughCoordinator(
            identity,
            httpClient,
            new FakeAuthenticationSession("access-token"),
            cache,
            NullLogger<ClientReadThroughCoordinator>.Instance);

        var outcome = await coordinator.TriggerAsync();
        var afterClear = await coordinator.TriggerAsync();

        Assert.Equal(ClientReadThroughRunStatus.Completed, outcome.Status);
        Assert.Equal((102, 102), (outcome.RequestCount, outcome.ReceiptCount));
        Assert.Equal(102, requestedConversations.Count);
        Assert.Equal(ClientReadThroughRunStatus.Completed, afterClear.Status);
        Assert.Equal((0, 0), (afterClear.RequestCount, afterClear.ReceiptCount));
    }

    [Fact]
    public async Task TriggerAsync_WhenUnreadGapRemains_DoesNotAdvancePastGap()
    {
        var prepared = await CreatePendingAsync(rawPendingMessageId: 63);
        var requestedMessageIds = new ConcurrentQueue<long>();
        using var httpClient = new HttpClient(new DelegateHttpHandler(async (request, cancellationToken) =>
        {
            var payload = await request.Content!
                .ReadFromJsonAsync<MarkConversationReadRequest>(cancellationToken);
            requestedMessageIds.Enqueue(payload!.MessageId);
            return Ok(new ConversationReadReceipt(prepared.Conversation.Id, payload.MessageId));
        }));
        await using var coordinator = CreateCoordinator(
            prepared,
            httpClient,
            new FakeAuthenticationSession("access-token"));
        await coordinator.TriggerAsync();
        Assert.Equal(
            LocalCacheOperationStatus.Ready,
            (await prepared.Cache.ApplySyncPageAsync(
                new SyncResponse(
                    [CreateMessage(61, prepared.Conversation.Id), prepared.PendingMessage],
                    NextCursor: 63,
                    SnapshotUpperBound: 63,
                    HasMore: false),
                expectedCursor: 60,
                expectedSnapshotUpperBound: null)).Status);

        var outcome = await coordinator.TriggerAsync();

        Assert.Equal(ClientReadThroughRunStatus.Completed, outcome.Status);
        Assert.Equal((0, 0), (outcome.RequestCount, outcome.ReceiptCount));
        Assert.Equal([50L], requestedMessageIds.ToArray());
        Assert.Equal(
            new ConversationAttention(LastReadMessageId: 50, PendingReadThroughMessageId: 63),
            ReadConversationAttention(prepared.Identity, prepared.Conversation.Id));
        Assert.Equal(1, ReadConversationUnreadCount(
            prepared.Identity,
            prepared.Conversation.Id));
        Assert.False(ReadMessageIsRead(prepared.Identity, messageId: 61));
    }

    [Fact]
    public async Task TriggerAsync_WhenUnauthorizedOnce_RefreshesAndAppliesReceipt()
    {
        var prepared = await CreatePendingAsync(rawPendingMessageId: 50);
        var authorization = new ConcurrentQueue<string?>();
        var responseNumber = 0;
        using var httpClient = new HttpClient(new DelegateHttpHandler((request, _) =>
        {
            authorization.Enqueue(request.Headers.Authorization?.ToString());
            responseNumber++;
            return Task.FromResult(responseNumber == 1
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : Ok(new ConversationReadReceipt(prepared.Conversation.Id, 50)));
        }));
        var authentication = new FakeAuthenticationSession(
            "old-token",
            refreshedToken: "new-token");
        await using var coordinator = CreateCoordinator(prepared, httpClient, authentication);

        var outcome = await coordinator.TriggerAsync();

        Assert.Equal(ClientReadThroughRunStatus.Completed, outcome.Status);
        Assert.Equal((1, 1), (outcome.RequestCount, outcome.ReceiptCount));
        Assert.Equal(1, authentication.RefreshCount);
        Assert.Equal(
            new string?[] { "Bearer old-token", "Bearer new-token" },
            authorization.ToArray());
        Assert.Null(ReadConversationAttention(
            prepared.Identity,
            prepared.Conversation.Id).PendingReadThroughMessageId);
    }

    [Fact]
    public async Task TriggerAsync_WhenRefreshFails_StopsAfterFirstUnauthorized()
    {
        var prepared = await CreatePendingAsync(rawPendingMessageId: 50);
        var requestCount = 0;
        using var httpClient = new HttpClient(new DelegateHttpHandler((_, _) =>
        {
            requestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        }));
        var authentication = new FakeAuthenticationSession("expired-token");
        await using var coordinator = CreateCoordinator(prepared, httpClient, authentication);

        var outcome = await coordinator.TriggerAsync();

        Assert.Equal(ClientReadThroughRunStatus.AuthenticationRequired, outcome.Status);
        Assert.Equal((1, 0), (outcome.RequestCount, outcome.ReceiptCount));
        Assert.Equal(1, requestCount);
        Assert.Equal(1, authentication.RefreshCount);
        Assert.Equal(
            50,
            ReadConversationAttention(
                prepared.Identity,
                prepared.Conversation.Id).PendingReadThroughMessageId);
    }

    [Fact]
    public async Task TriggerAsync_WhenNetworkFails_RetainsPendingWithoutInternalRetry()
    {
        var prepared = await CreatePendingAsync(rawPendingMessageId: 50);
        var requestCount = 0;
        using var httpClient = new HttpClient(new DelegateHttpHandler((_, _) =>
        {
            requestCount++;
            throw new HttpRequestException("sensitive transport detail");
        }));
        await using var coordinator = CreateCoordinator(
            prepared,
            httpClient,
            new FakeAuthenticationSession("access-token"));

        var outcome = await coordinator.TriggerAsync();
        var deferred = await coordinator.TriggerAsync();
        Assert.Equal(
            LocalCacheOperationStatus.Ready,
            await prepared.Cache.ApplyAuthoritativeConversationSnapshotAsync(
                new ConversationListResponse([prepared.Conversation], Complete: true)));
        var afterSnapshot = await coordinator.TriggerAsync();

        Assert.Equal(ClientReadThroughRunStatus.TransientFailure, outcome.Status);
        Assert.Equal((1, 0), (outcome.RequestCount, outcome.ReceiptCount));
        Assert.Equal(ClientReadThroughRunStatus.Completed, deferred.Status);
        Assert.Equal((0, 0), (deferred.RequestCount, deferred.ReceiptCount));
        Assert.Equal(ClientReadThroughRunStatus.TransientFailure, afterSnapshot.Status);
        Assert.Equal((1, 0), (afterSnapshot.RequestCount, afterSnapshot.ReceiptCount));
        Assert.Equal(2, requestCount);
        Assert.Equal(
            50,
            ReadConversationAttention(
                prepared.Identity,
                prepared.Conversation.Id).PendingReadThroughMessageId);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, 4)]
    [InlineData(HttpStatusCode.Forbidden, 5)]
    [InlineData(HttpStatusCode.TooManyRequests, 3)]
    [InlineData(HttpStatusCode.InternalServerError, 3)]
    [InlineData(HttpStatusCode.NotFound, 7)]
    public async Task TriggerAsync_WhenServerRejectsOrFails_RetainsPendingWithoutRetry(
        HttpStatusCode statusCode,
        int expectedStatusValue)
    {
        var prepared = await CreatePendingAsync(rawPendingMessageId: 50);
        var requestCount = 0;
        using var httpClient = new HttpClient(new DelegateHttpHandler((_, _) =>
        {
            requestCount++;
            return Task.FromResult(new HttpResponseMessage(statusCode));
        }));
        await using var coordinator = CreateCoordinator(
            prepared,
            httpClient,
            new FakeAuthenticationSession("access-token"));

        var outcome = await coordinator.TriggerAsync();

        Assert.Equal((ClientReadThroughRunStatus)expectedStatusValue, outcome.Status);
        Assert.Equal((1, 0), (outcome.RequestCount, outcome.ReceiptCount));
        Assert.Equal(1, requestCount);
        Assert.Equal(
            50,
            ReadConversationAttention(
                prepared.Identity,
                prepared.Conversation.Id).PendingReadThroughMessageId);
    }

    [Fact]
    public async Task TriggerAsync_WhenPermanentFailureRepeats_SuppressesUntilNextSnapshot()
    {
        var prepared = await CreatePendingAsync(rawPendingMessageId: 50);
        var requestCount = 0;
        using var httpClient = new HttpClient(new DelegateHttpHandler((_, _) =>
        {
            requestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
        }));
        await using var coordinator = CreateCoordinator(
            prepared,
            httpClient,
            new FakeAuthenticationSession("access-token"));

        var first = await coordinator.TriggerAsync();
        Assert.Equal(
            LocalCacheOperationStatus.Ready,
            (await prepared.Cache.MergeIncomingMessageAsync(
                CreateMessage(63, prepared.Conversation.Id),
                new LocalMessageIngestionContext(
                    IncomingMessageSource.Realtime,
                    prepared.Conversation.Id))).Status);
        var suppressed = await coordinator.TriggerAsync();
        Assert.Equal(
            LocalCacheOperationStatus.Ready,
            await prepared.Cache.ApplyAuthoritativeConversationSnapshotAsync(
                new ConversationListResponse([prepared.Conversation], Complete: true)));
        var afterSnapshot = await coordinator.TriggerAsync();

        Assert.Equal(ClientReadThroughRunStatus.ProtocolError, first.Status);
        Assert.Equal(ClientReadThroughRunStatus.Completed, suppressed.Status);
        Assert.Equal((0, 0), (suppressed.RequestCount, suppressed.ReceiptCount));
        Assert.Equal(ClientReadThroughRunStatus.ProtocolError, afterSnapshot.Status);
        Assert.Equal(2, requestCount);
        Assert.Equal(
            63,
            ReadConversationAttention(
                prepared.Identity,
                prepared.Conversation.Id).PendingReadThroughMessageId);
    }

    [Fact]
    public async Task TriggerAsync_WhenForbiddenCodeConfirmsRevocation_PurgesConversation()
    {
        var prepared = await CreatePendingAsync(rawPendingMessageId: 50);
        var clearedConversations = new List<Guid>();
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
            new FakeAuthenticationSession("access-token"),
            (conversationId, _) =>
            {
                clearedConversations.Add(conversationId);
                return Task.CompletedTask;
            });

        var outcome = await coordinator.TriggerAsync();

        Assert.Equal(ClientReadThroughRunStatus.Completed, outcome.Status);
        Assert.Equal((1, 0), (outcome.RequestCount, outcome.ReceiptCount));
        Assert.Equal(
            LocalCacheOperationStatus.RevokedConversation,
            (await prepared.Cache.ReadMessagesAsync(prepared.Conversation.Id)).Status);
        Assert.Equal([prepared.Conversation.Id], clearedConversations);
    }

    [Fact]
    public async Task TriggerAsync_WhenReceiptTargetsAnotherConversation_RejectsProtocolWithoutClearing()
    {
        var prepared = await CreatePendingAsync(rawPendingMessageId: 50);
        using var httpClient = new HttpClient(new DelegateHttpHandler((_, _) =>
            Task.FromResult(Ok(new ConversationReadReceipt(Guid.NewGuid(), 50)))));
        await using var coordinator = CreateCoordinator(
            prepared,
            httpClient,
            new FakeAuthenticationSession("access-token"));

        var outcome = await coordinator.TriggerAsync();

        Assert.Equal(ClientReadThroughRunStatus.ProtocolError, outcome.Status);
        Assert.Equal((1, 0), (outcome.RequestCount, outcome.ReceiptCount));
        Assert.Equal(
            50,
            ReadConversationAttention(
                prepared.Identity,
                prepared.Conversation.Id).PendingReadThroughMessageId);
    }

    [Fact]
    public async Task TriggerAsync_WhenReceiptRegressesRequestedTarget_RejectsProtocolWithoutClearing()
    {
        var prepared = await CreatePendingAsync(rawPendingMessageId: 50);
        using var httpClient = new HttpClient(new DelegateHttpHandler((_, _) =>
            Task.FromResult(Ok(new ConversationReadReceipt(
                prepared.Conversation.Id,
                49)))));
        await using var coordinator = CreateCoordinator(
            prepared,
            httpClient,
            new FakeAuthenticationSession("access-token"));

        var outcome = await coordinator.TriggerAsync();

        Assert.Equal(ClientReadThroughRunStatus.ProtocolError, outcome.Status);
        Assert.Equal((1, 0), (outcome.RequestCount, outcome.ReceiptCount));
        Assert.Equal(
            50,
            ReadConversationAttention(
                prepared.Identity,
                prepared.Conversation.Id).PendingReadThroughMessageId);
    }

    [Fact]
    public async Task TriggerAsync_WhenAccessIsRevokedInFlight_DoesNotApplyLateReceipt()
    {
        var prepared = await CreatePendingAsync(rawPendingMessageId: 50);
        var requestStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseResponse = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var httpClient = new HttpClient(new DelegateHttpHandler(async (_, cancellationToken) =>
        {
            requestStarted.TrySetResult(true);
            await releaseResponse.Task.WaitAsync(cancellationToken);
            return Ok(new ConversationReadReceipt(prepared.Conversation.Id, 50));
        }));
        await using var coordinator = CreateCoordinator(
            prepared,
            httpClient,
            new FakeAuthenticationSession("access-token"));

        var flight = coordinator.TriggerAsync();
        await requestStarted.Task;
        Assert.Equal(
            LocalCacheOperationStatus.RevokedConversation,
            await prepared.Cache.RevokeConversationAccessAsync(prepared.Conversation.Id));
        releaseResponse.TrySetResult(true);
        var outcome = await flight;

        Assert.Equal(ClientReadThroughRunStatus.Completed, outcome.Status);
        Assert.Equal((1, 0), (outcome.RequestCount, outcome.ReceiptCount));
        Assert.Equal(
            LocalCacheOperationStatus.RevokedConversation,
            (await prepared.Cache.ReadMessagesAsync(prepared.Conversation.Id)).Status);
    }

    [Fact]
    public async Task TriggerAsync_WhenRevocationStartsDuringBatchRead_DoesNotReturnRevokedTarget()
    {
        using var faultInjector = new BlockingReadFaultInjector();
        var prepared = await CreatePendingAsync(
            rawPendingMessageId: 50,
            faultInjector);
        var requestCount = 0;
        using var httpClient = new HttpClient(new DelegateHttpHandler((_, _) =>
        {
            Interlocked.Increment(ref requestCount);
            return Task.FromResult(Ok(new ConversationReadReceipt(
                prepared.Conversation.Id,
                50)));
        }));
        await using var coordinator = CreateCoordinator(
            prepared,
            httpClient,
            new FakeAuthenticationSession("access-token"));

        var flight = coordinator.TriggerAsync();
        await faultInjector.Entered;
        var revoke = prepared.Cache.RevokeConversationAccessAsync(prepared.Conversation.Id);
        faultInjector.Release();
        var outcome = await flight;

        Assert.Equal(
            LocalCacheOperationStatus.RevokedConversation,
            await revoke);
        Assert.Equal(ClientReadThroughRunStatus.Completed, outcome.Status);
        Assert.Equal((0, 0), (outcome.RequestCount, outcome.ReceiptCount));
        Assert.Equal(0, requestCount);
        Assert.Equal(
            LocalCacheOperationStatus.RevokedConversation,
            (await prepared.Cache.ReadMessagesAsync(prepared.Conversation.Id)).Status);
    }

    [Fact]
    public async Task TriggerAsync_WhenPendingAdvancesInFlight_ReceiptDoesNotClearNewerTarget()
    {
        var prepared = await CreatePendingAsync(rawPendingMessageId: 50);
        var requestStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseResponse = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var httpClient = new HttpClient(new DelegateHttpHandler(async (_, cancellationToken) =>
        {
            requestStarted.TrySetResult(true);
            await releaseResponse.Task.WaitAsync(cancellationToken);
            return Ok(new ConversationReadReceipt(prepared.Conversation.Id, 50));
        }));
        await using var coordinator = CreateCoordinator(
            prepared,
            httpClient,
            new FakeAuthenticationSession("access-token"));

        var flight = coordinator.TriggerAsync();
        await requestStarted.Task;
        Assert.Equal(
            LocalCacheOperationStatus.Ready,
            (await prepared.Cache.MergeIncomingMessageAsync(
                CreateMessage(63, prepared.Conversation.Id),
                new LocalMessageIngestionContext(
                    IncomingMessageSource.Realtime,
                    prepared.Conversation.Id))).Status);
        releaseResponse.TrySetResult(true);
        var outcome = await flight;

        Assert.Equal(ClientReadThroughRunStatus.Completed, outcome.Status);
        Assert.Equal(
            new ConversationAttention(LastReadMessageId: 50, PendingReadThroughMessageId: 63),
            ReadConversationAttention(prepared.Identity, prepared.Conversation.Id));
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(59L)]
    [InlineData(60L)]
    public async Task TriggerAsync_WhenPendingStateIsCorrupt_IsolatesTargetWithoutSending(
        long corruptPendingMessageId)
    {
        var prepared = await CreatePendingAsync(rawPendingMessageId: 50);
        ExecuteNonQuery(
            prepared.Identity,
            "UPDATE LocalConversations " +
            $"SET PendingReadThroughMessageId = {corruptPendingMessageId} " +
            $"WHERE Id = '{prepared.Conversation.Id:D}';");
        var requestCount = 0;
        using var httpClient = new HttpClient(new DelegateHttpHandler((_, _) =>
        {
            requestCount++;
            return Task.FromResult(Ok(new ConversationReadReceipt(
                prepared.Conversation.Id,
                50)));
        }));
        await using var coordinator = CreateCoordinator(
            prepared,
            httpClient,
            new FakeAuthenticationSession("access-token"));

        var outcome = await coordinator.TriggerAsync();

        Assert.Equal(ClientReadThroughRunStatus.Completed, outcome.Status);
        Assert.Equal((0, 0), (outcome.RequestCount, outcome.ReceiptCount));
        Assert.Equal(0, requestCount);
        Assert.False(prepared.Cache.IsFatal);
        Assert.Equal(
            LocalCacheOperationStatus.Ready,
            (await prepared.Cache.ReadMessagesAsync(prepared.Conversation.Id)).Status);
    }

    [Fact]
    public async Task TriggerAsync_WhenOnePendingStateIsCorrupt_StillUploadsOtherConversation()
    {
        var prepared = await CreatePendingAsync(rawPendingMessageId: 50);
        ExecuteNonQuery(
            prepared.Identity,
            "UPDATE LocalConversations SET PendingReadThroughMessageId = 59 " +
            $"WHERE Id = '{prepared.Conversation.Id:D}';");
        Assert.Equal(
            LocalCacheOperationStatus.Ready,
            (await prepared.Cache.MergeIncomingMessageAsync(
                CreateMessage(61, prepared.CursorOwner.Id),
                new LocalMessageIngestionContext(
                    IncomingMessageSource.Realtime,
                    prepared.CursorOwner.Id))).Status);
        var requestedConversations = new ConcurrentQueue<Guid>();
        using var httpClient = new HttpClient(new DelegateHttpHandler(
            async (request, cancellationToken) =>
            {
                var payload = await request.Content!
                    .ReadFromJsonAsync<MarkConversationReadRequest>(cancellationToken);
                requestedConversations.Enqueue(prepared.CursorOwner.Id);
                return Ok(new ConversationReadReceipt(
                    prepared.CursorOwner.Id,
                    payload!.MessageId));
            }));
        await using var coordinator = CreateCoordinator(
            prepared,
            httpClient,
            new FakeAuthenticationSession("access-token"));

        var outcome = await coordinator.TriggerAsync();

        Assert.Equal(ClientReadThroughRunStatus.Completed, outcome.Status);
        Assert.Equal((1, 1), (outcome.RequestCount, outcome.ReceiptCount));
        Assert.Equal([prepared.CursorOwner.Id], requestedConversations.ToArray());
        Assert.False(prepared.Cache.IsFatal);
    }

    [Fact]
    public async Task TriggerAsync_WhenPendingReadStaysBusy_ReturnsTransientWithoutFailingScope()
    {
        var faultInjector = new BusyReadFaultInjector();
        var prepared = await CreatePendingAsync(
            rawPendingMessageId: 50,
            faultInjector);
        var requestCount = 0;
        using var httpClient = new HttpClient(new DelegateHttpHandler((_, _) =>
        {
            requestCount++;
            return Task.FromResult(Ok(new ConversationReadReceipt(
                prepared.Conversation.Id,
                50)));
        }));
        await using var coordinator = CreateCoordinator(
            prepared,
            httpClient,
            new FakeAuthenticationSession("access-token"));

        var outcome = await coordinator.TriggerAsync();

        Assert.Equal(ClientReadThroughRunStatus.TransientFailure, outcome.Status);
        Assert.Equal((0, 0), (outcome.RequestCount, outcome.ReceiptCount));
        Assert.Equal(4, faultInjector.AttemptCount);
        Assert.Equal(0, requestCount);
        Assert.False(prepared.Cache.IsFatal);
        Assert.Equal(
            LocalCacheOperationStatus.Ready,
            (await prepared.Cache.ReadMessagesAsync(prepared.Conversation.Id)).Status);
    }

    [Fact]
    public async Task TriggerAsync_WhenManyCallersOverlap_UsesOneRequestAndOneBoundedRerun()
    {
        var prepared = await CreatePendingAsync(rawPendingMessageId: 50);
        var requestStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseResponse = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var requestCount = 0;
        using var httpClient = new HttpClient(new DelegateHttpHandler(async (_, cancellationToken) =>
        {
            Interlocked.Increment(ref requestCount);
            requestStarted.TrySetResult(true);
            await releaseResponse.Task.WaitAsync(cancellationToken);
            return Ok(new ConversationReadReceipt(prepared.Conversation.Id, 50));
        }));
        await using var coordinator = CreateCoordinator(
            prepared,
            httpClient,
            new FakeAuthenticationSession("access-token"));

        var first = coordinator.TriggerAsync();
        await requestStarted.Task;
        var overlapping = Enumerable.Range(0, 20)
            .Select(_ => coordinator.TriggerAsync())
            .ToArray();
        releaseResponse.TrySetResult(true);
        var outcomes = await Task.WhenAll([first, .. overlapping]);

        Assert.Equal(1, requestCount);
        Assert.All(outcomes, outcome =>
        {
            Assert.Equal(ClientReadThroughRunStatus.Completed, outcome.Status);
            Assert.Equal((1, 1), (outcome.RequestCount, outcome.ReceiptCount));
        });
    }

    [Fact]
    public async Task TriggerAsync_WhenPermanentErrorHasPendingOverlap_RunsOneBoundedRerun()
    {
        var prepared = await CreatePendingAsync(rawPendingMessageId: 50);
        var requestStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseResponse = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var requestCount = 0;
        using var httpClient = new HttpClient(new DelegateHttpHandler(
            async (_, cancellationToken) =>
            {
                var currentRequest = Interlocked.Increment(ref requestCount);
                if (currentRequest == 1)
                {
                    requestStarted.TrySetResult(true);
                    await releaseResponse.Task.WaitAsync(cancellationToken);
                    return new HttpResponseMessage(HttpStatusCode.BadRequest);
                }

                return Ok(new ConversationReadReceipt(prepared.CursorOwner.Id, 60));
            }));
        await using var coordinator = CreateCoordinator(
            prepared,
            httpClient,
            new FakeAuthenticationSession("access-token"));

        var first = coordinator.TriggerAsync();
        await requestStarted.Task;
        Assert.Equal(
            LocalCacheOperationStatus.Ready,
            (await prepared.Cache.MergeIncomingMessageAsync(
                CreateMessage(61, prepared.CursorOwner.Id),
                new LocalMessageIngestionContext(
                    IncomingMessageSource.Realtime,
                    prepared.CursorOwner.Id))).Status);
        var overlapping = coordinator.TriggerAsync();
        releaseResponse.TrySetResult(true);
        var outcomes = await Task.WhenAll(first, overlapping);

        Assert.Equal(2, requestCount);
        Assert.All(outcomes, outcome =>
        {
            Assert.Equal(ClientReadThroughRunStatus.ProtocolError, outcome.Status);
            Assert.Equal((2, 1), (outcome.RequestCount, outcome.ReceiptCount));
        });
        Assert.Equal(
            new ConversationAttention(LastReadMessageId: 60, PendingReadThroughMessageId: 61),
            ReadConversationAttention(prepared.Identity, prepared.CursorOwner.Id));
    }

    [Fact]
    public async Task TriggerAsync_WhenConversationRejoins_DoesNotReusePriorMembershipAcknowledgement()
    {
        var prepared = await CreatePendingAsync(rawPendingMessageId: 50);
        var requestCount = 0;
        using var httpClient = new HttpClient(new DelegateHttpHandler((_, _) =>
        {
            requestCount++;
            return Task.FromResult(Ok(new ConversationReadReceipt(
                prepared.Conversation.Id,
                50)));
        }));
        await using var coordinator = CreateCoordinator(
            prepared,
            httpClient,
            new FakeAuthenticationSession("access-token"));
        Assert.Equal(ClientReadThroughRunStatus.Completed, (await coordinator.TriggerAsync()).Status);
        Assert.Equal(
            LocalCacheOperationStatus.RevokedConversation,
            await prepared.Cache.RevokeConversationAccessAsync(prepared.Conversation.Id));
        Assert.Equal(
            LocalCacheOperationStatus.Ready,
            await prepared.Cache.ApplyAuthoritativeConversationSnapshotAsync(
                new ConversationListResponse([prepared.Conversation], Complete: true)));
        Assert.Equal(
            LocalCacheOperationStatus.Ready,
            (await prepared.Cache.MergeIncomingMessageAsync(
                prepared.PendingMessage,
                new LocalMessageIngestionContext(
                    IncomingMessageSource.Realtime,
                    prepared.Conversation.Id))).Status);

        var rejoined = await coordinator.TriggerAsync();

        Assert.Equal(ClientReadThroughRunStatus.Completed, rejoined.Status);
        Assert.Equal((1, 1), (rejoined.RequestCount, rejoined.ReceiptCount));
        Assert.Equal(2, requestCount);
    }

    [Fact]
    public async Task TriggerAsync_AfterCacheRestart_RecoversDurablePendingTarget()
    {
        var prepared = await CreatePendingAsync(rawPendingMessageId: 50);
        await prepared.Cache.DisposeAsync();
        var restartedCache = await AccountScopedLocalCache.CreateAsync(
            prepared.Identity,
            NullLogger<AccountScopedLocalCache>.Instance);
        Assert.Equal(
            LocalCacheOperationStatus.Ready,
            await restartedCache.ApplyAuthoritativeConversationSnapshotAsync(
                new ConversationListResponse([prepared.Conversation], Complete: true)));
        var restarted = prepared with { Cache = restartedCache };
        var requestCount = 0;
        using var httpClient = new HttpClient(new DelegateHttpHandler((_, _) =>
        {
            requestCount++;
            return Task.FromResult(Ok(new ConversationReadReceipt(
                prepared.Conversation.Id,
                50)));
        }));
        await using var coordinator = CreateCoordinator(
            restarted,
            httpClient,
            new FakeAuthenticationSession("access-token"));

        var outcome = await coordinator.TriggerAsync();

        Assert.Equal(ClientReadThroughRunStatus.Completed, outcome.Status);
        Assert.Equal((1, 1), (outcome.RequestCount, outcome.ReceiptCount));
        Assert.Equal(1, requestCount);
        Assert.Null(ReadConversationAttention(
            prepared.Identity,
            prepared.Conversation.Id).PendingReadThroughMessageId);
        await restartedCache.DisposeAsync();
    }

    [Fact]
    public async Task TriggerAsync_WhenCallerCancelsWaiting_SharedUploadStillCommits()
    {
        var prepared = await CreatePendingAsync(rawPendingMessageId: 50);
        var requestStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseResponse = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var httpClient = new HttpClient(new DelegateHttpHandler(async (_, cancellationToken) =>
        {
            requestStarted.TrySetResult(true);
            await releaseResponse.Task.WaitAsync(cancellationToken);
            return Ok(new ConversationReadReceipt(prepared.Conversation.Id, 50));
        }));
        await using var coordinator = CreateCoordinator(
            prepared,
            httpClient,
            new FakeAuthenticationSession("access-token"));

        var shared = coordinator.TriggerAsync();
        await requestStarted.Task;
        using var callerCancellation = new CancellationTokenSource();
        var canceledWait = coordinator.TriggerAsync(callerCancellation.Token);
        callerCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledWait);
        releaseResponse.TrySetResult(true);

        Assert.Equal(ClientReadThroughRunStatus.Completed, (await shared).Status);
        Assert.Null(ReadConversationAttention(
            prepared.Identity,
            prepared.Conversation.Id).PendingReadThroughMessageId);
    }

    [Fact]
    public async Task DisposeAsync_WhenRequestIsInFlight_CancelsFlightAndRetainsPending()
    {
        var prepared = await CreatePendingAsync(rawPendingMessageId: 50);
        var requestStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var httpClient = new HttpClient(new DelegateHttpHandler(async (_, cancellationToken) =>
        {
            requestStarted.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }));
        var coordinator = CreateCoordinator(
            prepared,
            httpClient,
            new FakeAuthenticationSession("access-token"));

        var flight = coordinator.TriggerAsync();
        await requestStarted.Task;
        await coordinator.DisposeAsync();

        Assert.Equal(ClientReadThroughRunStatus.Canceled, (await flight).Status);
        Assert.Equal(
            50,
            ReadConversationAttention(
                prepared.Identity,
                prepared.Conversation.Id).PendingReadThroughMessageId);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => coordinator.TriggerAsync());
    }

    [Fact]
    public void ReadThroughDiagnostics_WhenFormatted_RedactIdentifiersAndTargets()
    {
        var conversationId = Guid.Parse("12345678-1234-1234-1234-123456789abc");
        const long targetId = 987654321;
        var target = new LocalReadThroughUploadTarget(conversationId, targetId, targetId);
        var batch = new LocalReadThroughBatchOutcome(
            LocalCacheOperationStatus.Ready,
            [target],
            conversationId,
            SnapshotRevision: 42);
        var httpResult = ClientReadThroughHttpResult.Success(
            new ConversationReadReceipt(conversationId, targetId));

        foreach (var diagnostic in new[]
                 {
                     target.ToString(),
                     batch.ToString(),
                     httpResult.ToString(),
                 })
        {
            Assert.DoesNotContain(conversationId.ToString("D"), diagnostic, StringComparison.Ordinal);
            Assert.DoesNotContain(targetId.ToString(), diagnostic, StringComparison.Ordinal);
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(rootDirectory))
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    private async Task<PreparedReadThrough> CreatePendingAsync(
        long rawPendingMessageId,
        ILocalCacheFaultInjector? faultInjector = null)
    {
        var identity = AccountScopeIdentity.Create(ServerBaseUri, UserId, rootDirectory);
        var cache = await AccountScopedLocalCache.CreateAsync(
            identity,
            NullLogger<AccountScopedLocalCache>.Instance,
            faultInjector);
        var conversation = CreateConversation(lastMessageId: 50, lastReadMessageId: 0, unreadCount: 1);
        var cursorOwner = CreateConversation(lastMessageId: 60, lastReadMessageId: 60, unreadCount: 0);
        Assert.Equal(
            LocalCacheOperationStatus.Ready,
            await cache.ApplyAuthoritativeConversationSnapshotAsync(
                new ConversationListResponse([conversation, cursorOwner], Complete: true)));
        var message50 = CreateMessage(50, conversation.Id);
        Assert.Equal(
            LocalCacheOperationStatus.Ready,
            (await cache.ApplySyncPageAsync(
                new SyncResponse(
                    [message50, CreateMessage(60, cursorOwner.Id)],
                    NextCursor: 60,
                    SnapshotUpperBound: 60,
                    HasMore: false),
                0,
                null)).Status);
        var pendingMessage = rawPendingMessageId == 50
            ? message50
            : CreateMessage(rawPendingMessageId, conversation.Id);
        var merge = await cache.MergeIncomingMessageAsync(
            pendingMessage,
            new LocalMessageIngestionContext(IncomingMessageSource.Realtime, conversation.Id));
        Assert.Equal(LocalCacheOperationStatus.Ready, merge.Status);
        return new PreparedReadThrough(
            identity,
            cache,
            conversation,
            cursorOwner,
            pendingMessage);
    }

    private static ClientReadThroughCoordinator CreateCoordinator(
        PreparedReadThrough prepared,
        HttpClient httpClient,
        IClientAuthenticationSession authenticationSession,
        Func<Guid, CancellationToken, Task>? conversationRevokedAsync = null) =>
        new(
            prepared.Identity,
            httpClient,
            authenticationSession,
            prepared.Cache,
            NullLogger<ClientReadThroughCoordinator>.Instance,
            conversationRevokedAsync);

    private static ConversationDto CreateConversation(
        long lastMessageId,
        long lastReadMessageId,
        int unreadCount) => new(
        Guid.NewGuid(),
        ConversationType.PrivateChannel,
        "Conversation",
        null,
        DateTimeOffset.Parse("2026-08-03T01:00:00Z"),
        DateTimeOffset.Parse("2026-08-03T02:00:00Z"),
        lastMessageId,
        lastReadMessageId,
        unreadCount);

    private static ConversationDto CreateConversationWithId(Guid conversationId) => new(
        conversationId,
        ConversationType.PrivateChannel,
        "Conversation",
        null,
        DateTimeOffset.Parse("2026-08-03T01:00:00Z"),
        DateTimeOffset.Parse("2026-08-03T02:00:00Z"),
        LastMessageId: 0,
        LastReadMessageId: 0,
        UnreadCount: 0);

    private static MessageDto CreateMessage(long id, Guid conversationId) => new(
        id,
        Guid.NewGuid(),
        conversationId,
        OtherUserId,
        "Sender",
        MessageType.Text,
        $"message {id}",
        null,
        Array.Empty<AttachmentDto>(),
        Array.Empty<Guid>(),
        DateTimeOffset.Parse("2026-08-03T03:00:00Z").AddSeconds(id));

    private static ConversationAttention ReadConversationAttention(
        AccountScopeIdentity identity,
        Guid conversationId)
    {
        using var connection = OpenConnection(identity);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT LastReadMessageId, PendingReadThroughMessageId
            FROM LocalConversations
            WHERE Id = $conversationId;
            """;
        command.Parameters.AddWithValue("$conversationId", conversationId.ToString("D"));
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        return new ConversationAttention(
            reader.GetInt64(0),
            reader.IsDBNull(1) ? null : reader.GetInt64(1));
    }

    private static int ReadConversationUnreadCount(
        AccountScopeIdentity identity,
        Guid conversationId)
    {
        using var connection = OpenConnection(identity);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT UnreadCount FROM LocalConversations WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", conversationId.ToString("D"));
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static bool ReadMessageIsRead(AccountScopeIdentity identity, long messageId)
    {
        using var connection = OpenConnection(identity);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT IsRead FROM LocalMessages WHERE ServerMessageId = $id;";
        command.Parameters.AddWithValue("$id", messageId);
        return Convert.ToInt32(command.ExecuteScalar()) != 0;
    }

    private static SqliteConnection OpenConnection(AccountScopeIdentity identity)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = identity.DatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            ForeignKeys = true,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static void ExecuteNonQuery(AccountScopeIdentity identity, string sql)
    {
        using var connection = OpenConnection(identity);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static HttpResponseMessage Ok<T>(T value) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(value),
    };

    private sealed record PreparedReadThrough(
        AccountScopeIdentity Identity,
        AccountScopedLocalCache Cache,
        ConversationDto Conversation,
        ConversationDto CursorOwner,
        MessageDto PendingMessage);

    private sealed record ConversationAttention(
        long LastReadMessageId,
        long? PendingReadThroughMessageId);

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

    private sealed class BusyReadFaultInjector : ILocalCacheFaultInjector
    {
        private int attemptCount;

        public int AttemptCount => Volatile.Read(ref attemptCount);

        public void BeforeRevocationTombstone(Guid conversationId)
        {
        }

        public void BeforeReadPendingReadThroughBatch()
        {
            Interlocked.Increment(ref attemptCount);
            throw new SqliteException("busy", 5, 5);
        }
    }

    private sealed class BlockingReadFaultInjector : ILocalCacheFaultInjector, IDisposable
    {
        private readonly ManualResetEventSlim release = new(initialState: false);
        private readonly TaskCompletionSource<bool> entered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => entered.Task;

        public void BeforeRevocationTombstone(Guid conversationId)
        {
        }

        public void BeforeReadPendingReadThroughBatch()
        {
            entered.TrySetResult(true);
            release.Wait();
        }

        public void Release() => release.Set();

        public void Dispose() => release.Dispose();
    }
}
