using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RelayCove.Client.Notifications;
using RelayCove.Client.Storage;
using RelayCove.Client.Sync;
using RelayCove.Shared.Conversations;
using RelayCove.Shared.Errors;
using RelayCove.Shared.Messages;

namespace RelayCove.Client.Tests.Sync;

public sealed class ClientSyncCoordinatorTests : IDisposable
{
    private static readonly Guid UserId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private readonly string rootDirectory = Path.Combine(
        Path.GetTempPath(),
        "RelayCove.Client.SyncCoordinatorTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ClientSync_WhenPageCompletes_PairsRoundAndForwardsCommittedCandidates()
    {
        var identity = CreateIdentity();
        var conversation = CreateConversation("Notification round");
        var handler = new ScriptedHttpHandler();
        handler.EnqueueResponse(Ok(new ConversationListResponse([conversation], Complete: true)));
        handler.EnqueueResponse(Ok(new SyncResponse(
            [CreateMessage(1, conversation.Id)],
            1,
            1,
            HasMore: false)));
        var notificationRounds = new RecordingNotificationRoundCoordinator();
        await using var cache = await CreateCacheAsync(identity);
        var coordinator = CreateCoordinator(
            identity,
            handler,
            new RecordingAuthenticationSession("current-token"),
            cache,
            notificationRoundCoordinator: notificationRounds);

        var outcome = await coordinator.TriggerAsync(SyncReason.Startup);
        await coordinator.DisposeAsync();

        Assert.Equal(ClientSyncRunStatus.Completed, outcome.Status);
        Assert.Equal(
            ["open:Startup", "snapshot:1", "candidates:1", "close:1:Completed", "dispose"],
            notificationRounds.Events);
    }

    [Fact]
    public async Task ClientSync_WhenSnapshotRequestFails_StillClosesNotificationRound()
    {
        var identity = CreateIdentity();
        var handler = new ScriptedHttpHandler();
        handler.EnqueueResponse(new HttpResponseMessage(HttpStatusCode.BadRequest));
        var notificationRounds = new RecordingNotificationRoundCoordinator();
        await using var cache = await CreateCacheAsync(identity);
        var coordinator = CreateCoordinator(
            identity,
            handler,
            new RecordingAuthenticationSession("current-token"),
            cache,
            notificationRoundCoordinator: notificationRounds);

        var outcome = await coordinator.TriggerAsync(SyncReason.Periodic);
        await coordinator.DisposeAsync();

        Assert.Equal(ClientSyncRunStatus.ProtocolError, outcome.Status);
        Assert.Equal(
            ["open:Periodic", "close:1:ProtocolError", "dispose"],
            notificationRounds.Events);
    }

    [Fact]
    public async Task ClientSync_WhenTwoPagesComplete_AppliesSnapshotAndKeepsUpperBound()
    {
        var identity = CreateIdentity();
        var conversation = CreateConversation("Team");
        var first = CreateMessage(1, conversation.Id);
        var third = CreateMessage(3, conversation.Id);
        var handler = new ScriptedHttpHandler();
        handler.EnqueueResponse(Ok(new ConversationListResponse([conversation], Complete: true)));
        handler.EnqueueResponse(Ok(new SyncResponse([first], 1, 3, HasMore: true)));
        handler.EnqueueResponse(Ok(new SyncResponse([third], 3, 3, HasMore: false)));
        var authentication = new RecordingAuthenticationSession("current-token");
        await using var cache = await CreateCacheAsync(identity);
        await using var coordinator = CreateCoordinator(identity, handler, authentication, cache);

        var outcome = await coordinator.TriggerAsync(SyncReason.Startup);

        Assert.Equal(ClientSyncRunStatus.Completed, outcome.Status);
        Assert.Equal(SyncReason.Startup, outcome.Reason);
        Assert.Equal(1, outcome.RoundsExecuted);
        Assert.Equal(3, (await cache.ReadLastSyncCursorAsync()).Cursor);
        var messages = await cache.ReadMessagesAsync(conversation.Id);
        Assert.Equal([1L, 3L], messages.Messages.Select(message => message.Id));
        Assert.Equal(
            [
                "/relay/api/conversations",
                "/relay/api/sync?cursor=0&limit=100",
                "/relay/api/sync?cursor=1&snapshotUpperBound=3&limit=100",
            ],
            handler.Requests.Select(request => request.PathAndQuery));
        Assert.All(handler.Requests, request => Assert.Equal("current-token", request.BearerToken));
    }

    [Fact]
    public async Task ClientSync_WhenPagesCommit_RequestsReadThroughAfterEveryCursorAdvance()
    {
        var identity = CreateIdentity();
        var conversation = CreateConversation("Read-through");
        var handler = new ScriptedHttpHandler();
        handler.EnqueueResponse(Ok(new ConversationListResponse([conversation], Complete: true)));
        handler.EnqueueResponse(Ok(new SyncResponse(
            [CreateMessage(1, conversation.Id)],
            1,
            2,
            HasMore: true)));
        handler.EnqueueResponse(Ok(new SyncResponse(
            [CreateMessage(2, conversation.Id)],
            2,
            2,
            HasMore: false)));
        var requests = 0;
        await using var cache = await CreateCacheAsync(identity);
        await using var coordinator = CreateCoordinator(
            identity,
            handler,
            new RecordingAuthenticationSession("current-token"),
            cache,
            requestReadThroughUpload: () => Interlocked.Increment(ref requests));

        var outcome = await coordinator.TriggerAsync(SyncReason.Startup);

        Assert.Equal(ClientSyncRunStatus.Completed, outcome.Status);
        Assert.Equal(2, requests);
    }

    [Fact]
    public async Task ClientSync_WhenPageIsUnauthorized_RefreshesOnceAndRetriesExactPage()
    {
        var identity = CreateIdentity();
        var conversation = CreateConversation("Auth");
        var handler = new ScriptedHttpHandler();
        handler.EnqueueResponse(Ok(new ConversationListResponse([conversation], Complete: true)));
        handler.EnqueueResponse(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        handler.EnqueueResponse(Ok(new SyncResponse([], 0, 0, HasMore: false)));
        var authentication = new RecordingAuthenticationSession("old-token");
        authentication.EnqueueRefresh(success: true, "new-token");
        await using var cache = await CreateCacheAsync(identity);
        await using var coordinator = CreateCoordinator(identity, handler, authentication, cache);

        var outcome = await coordinator.TriggerAsync(SyncReason.Reconnect);

        Assert.Equal(ClientSyncRunStatus.Completed, outcome.Status);
        Assert.Equal(1, authentication.RefreshCount);
        Assert.Equal("old-token", authentication.RejectedTokens.Single());
        Assert.Equal(
            ["old-token", "old-token", "new-token"],
            handler.Requests.Select(request => request.BearerToken));
        Assert.Equal(
            handler.Requests[1].PathAndQuery,
            handler.Requests[2].PathAndQuery);
    }

    [Fact]
    public async Task ClientSync_WhenSecondUnauthorizedArrives_StopsWithoutSecondRefresh()
    {
        var identity = CreateIdentity();
        var conversation = CreateConversation("Auth failure");
        var handler = new ScriptedHttpHandler();
        handler.EnqueueResponse(Ok(new ConversationListResponse([conversation], Complete: true)));
        handler.EnqueueResponse(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        handler.EnqueueResponse(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var authentication = new RecordingAuthenticationSession("old-token");
        authentication.EnqueueRefresh(success: true, "new-token");
        await using var cache = await CreateCacheAsync(identity);
        await using var coordinator = CreateCoordinator(identity, handler, authentication, cache);

        var outcome = await coordinator.TriggerAsync(SyncReason.Startup);

        Assert.Equal(ClientSyncRunStatus.AuthenticationRequired, outcome.Status);
        Assert.Equal(1, authentication.RefreshCount);
        Assert.Equal(0, (await cache.ReadLastSyncCursorAsync()).Cursor);
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task ClientSync_WhenHttpStatusIsTransient_RetriesOnce(
        HttpStatusCode transientStatus)
    {
        var identity = CreateIdentity();
        var conversation = CreateConversation("Retry");
        var handler = new ScriptedHttpHandler();
        handler.EnqueueResponse(Ok(new ConversationListResponse([conversation], Complete: true)));
        handler.EnqueueResponse(new HttpResponseMessage(transientStatus));
        handler.EnqueueResponse(Ok(new SyncResponse([], 0, 0, HasMore: false)));
        var delays = new List<TimeSpan>();
        await using var cache = await CreateCacheAsync(identity);
        await using var coordinator = CreateCoordinator(
            identity,
            handler,
            new RecordingAuthenticationSession("token"),
            cache,
            delays);

        var outcome = await coordinator.TriggerAsync(SyncReason.Periodic);

        Assert.Equal(ClientSyncRunStatus.Completed, outcome.Status);
        Assert.Equal([TimeSpan.FromMilliseconds(250)], delays);
        Assert.Equal(handler.Requests[1].PathAndQuery, handler.Requests[2].PathAndQuery);
    }

    [Fact]
    public async Task ClientSync_WhenTransientFailuresRecover_HonorsRetryAfterAndExactTuple()
    {
        var identity = CreateIdentity();
        var conversation = CreateConversation("Retry sequence");
        var handler = new ScriptedHttpHandler();
        handler.EnqueueResponse(Ok(new ConversationListResponse([conversation], Complete: true)));
        handler.Enqueue((_, _) => throw new HttpRequestException("Injected network failure."));
        var rateLimited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        rateLimited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(2));
        handler.EnqueueResponse(rateLimited);
        handler.EnqueueResponse(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        handler.EnqueueResponse(Ok(new SyncResponse([], 0, 0, HasMore: false)));
        var delays = new List<TimeSpan>();
        await using var cache = await CreateCacheAsync(identity);
        await using var coordinator = CreateCoordinator(
            identity,
            handler,
            new RecordingAuthenticationSession("token"),
            cache,
            delays);

        var outcome = await coordinator.TriggerAsync(SyncReason.Reconnect);

        Assert.Equal(ClientSyncRunStatus.Completed, outcome.Status);
        Assert.Equal(
            [
                TimeSpan.FromMilliseconds(250),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(1),
            ],
            delays);
        Assert.Single(handler.Requests.Select(request => request.PathAndQuery).Skip(1).Distinct());
    }

    [Fact]
    public async Task ClientSync_WhenTransientRetriesAreExhausted_PreservesCursor()
    {
        var identity = CreateIdentity();
        var conversation = CreateConversation("Unavailable");
        var handler = new ScriptedHttpHandler();
        handler.EnqueueResponse(Ok(new ConversationListResponse([conversation], Complete: true)));
        for (var attempt = 0; attempt < 4; attempt++)
        {
            handler.EnqueueResponse(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }

        var delays = new List<TimeSpan>();
        await using var cache = await CreateCacheAsync(identity);
        await using var coordinator = CreateCoordinator(
            identity,
            handler,
            new RecordingAuthenticationSession("token"),
            cache,
            delays);

        var outcome = await coordinator.TriggerAsync(SyncReason.Periodic);

        Assert.Equal(ClientSyncRunStatus.TransientFailure, outcome.Status);
        Assert.Equal(3, delays.Count);
        Assert.Equal(0, (await cache.ReadLastSyncCursorAsync()).Cursor);
    }

    [Fact]
    public async Task ClientSync_WhenRetryAfterExceedsBound_ClampsDelayToThirtySeconds()
    {
        var identity = CreateIdentity();
        var conversation = CreateConversation("Retry cap");
        var handler = new ScriptedHttpHandler();
        handler.EnqueueResponse(Ok(new ConversationListResponse([conversation], Complete: true)));
        var rateLimited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        rateLimited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromHours(1));
        handler.EnqueueResponse(rateLimited);
        handler.EnqueueResponse(Ok(new SyncResponse([], 0, 0, HasMore: false)));
        var delays = new List<TimeSpan>();
        await using var cache = await CreateCacheAsync(identity);
        await using var coordinator = CreateCoordinator(
            identity,
            handler,
            new RecordingAuthenticationSession("token"),
            cache,
            delays);

        var outcome = await coordinator.TriggerAsync(SyncReason.Periodic);

        Assert.Equal(ClientSyncRunStatus.Completed, outcome.Status);
        Assert.Equal([TimeSpan.FromSeconds(30)], delays);
    }

    [Fact]
    public async Task ClientSync_WhenResponseIsBadRequest_StopsWithoutRetry()
    {
        var identity = CreateIdentity();
        var conversation = CreateConversation("Protocol");
        var handler = new ScriptedHttpHandler();
        handler.EnqueueResponse(Ok(new ConversationListResponse([conversation], Complete: true)));
        handler.EnqueueResponse(new HttpResponseMessage(HttpStatusCode.BadRequest));
        await using var cache = await CreateCacheAsync(identity);
        await using var coordinator = CreateCoordinator(
            identity,
            handler,
            new RecordingAuthenticationSession("token"),
            cache);

        var outcome = await coordinator.TriggerAsync(SyncReason.Startup);

        Assert.Equal(ClientSyncRunStatus.ProtocolError, outcome.Status);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(0, (await cache.ReadLastSyncCursorAsync()).Cursor);
    }

    [Fact]
    public async Task ClientSync_WhenJsonOrPageInvariantIsInvalid_StopsAsProtocolError()
    {
        var identity = CreateIdentity();
        var malformedHandler = new ScriptedHttpHandler();
        malformedHandler.EnqueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{not-json"),
        });
        await using (var malformedCache = await CreateCacheAsync(identity))
        await using (var malformedCoordinator = CreateCoordinator(
            identity,
            malformedHandler,
            new RecordingAuthenticationSession("token"),
            malformedCache))
        {
            var malformed = await malformedCoordinator.TriggerAsync(SyncReason.Startup);
            Assert.Equal(ClientSyncRunStatus.ProtocolError, malformed.Status);
        }

        var secondIdentity = CreateIdentity(Guid.NewGuid());
        var conversation = CreateConversation("Invariant");
        var invariantHandler = new ScriptedHttpHandler();
        invariantHandler.EnqueueResponse(Ok(
            new ConversationListResponse([conversation], Complete: true)));
        invariantHandler.EnqueueResponse(Ok(
            new SyncResponse([], NextCursor: 0, SnapshotUpperBound: 1, HasMore: true)));
        await using var invariantCache = await CreateCacheAsync(secondIdentity);
        await using var invariantCoordinator = CreateCoordinator(
            secondIdentity,
            invariantHandler,
            new RecordingAuthenticationSession("token"),
            invariantCache);

        var invariant = await invariantCoordinator.TriggerAsync(SyncReason.Startup);

        Assert.Equal(ClientSyncRunStatus.ProtocolError, invariant.Status);
        Assert.Equal(0, (await invariantCache.ReadLastSyncCursorAsync()).Cursor);
    }

    [Fact]
    public async Task ClientSync_WhenCursorIsInvalid_BlocksLaterTriggersAndKeepsPending()
    {
        var identity = CreateIdentity();
        var conversation = CreateConversation("Cursor invalid");
        await using var cache = await CreateCacheAsync(identity);
        Assert.Equal(
            LocalCacheOperationStatus.Ready,
            await cache.ApplyAuthoritativeConversationSnapshotAsync(
                new ConversationListResponse([conversation], Complete: true)));
        Assert.Equal(
            LocalCacheOperationStatus.Ready,
            await cache.AddPendingMessageAsync(CreatePending(conversation.Id)));
        var handler = new ScriptedHttpHandler();
        handler.EnqueueResponse(Ok(new ConversationListResponse([conversation], Complete: true)));
        handler.EnqueueResponse(Error(
            HttpStatusCode.Conflict,
            ApiErrorCodes.SyncCursorInvalid));
        await using var coordinator = CreateCoordinator(
            identity,
            handler,
            new RecordingAuthenticationSession("token"),
            cache);

        var first = await coordinator.TriggerAsync(SyncReason.Startup);
        var second = await coordinator.TriggerAsync(SyncReason.WindowActivated);

        Assert.Equal(ClientSyncRunStatus.CursorInvalid, first.Status);
        Assert.Equal(ClientSyncRunStatus.CursorInvalid, second.Status);
        Assert.Equal(0, second.RoundsExecuted);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(0, (await cache.ReadLastSyncCursorAsync()).Cursor);
        Assert.Equal(1, Scalar(identity, "SELECT COUNT(*) FROM LocalMessages WHERE ServerMessageId IS NULL;"));
    }

    [Fact]
    public async Task ClientSync_WhenConflictCodeIsUnexpected_DoesNotSetCursorBlock()
    {
        var identity = CreateIdentity();
        var conversation = CreateConversation("Unexpected conflict");
        var handler = new ScriptedHttpHandler();
        handler.EnqueueResponse(Ok(new ConversationListResponse([conversation], Complete: true)));
        handler.EnqueueResponse(Error(HttpStatusCode.Conflict, ApiErrorCodes.IdempotencyKeyReuse));
        handler.EnqueueResponse(Ok(new ConversationListResponse([conversation], Complete: true)));
        handler.EnqueueResponse(Ok(new SyncResponse([], 0, 0, HasMore: false)));
        await using var cache = await CreateCacheAsync(identity);
        await using var coordinator = CreateCoordinator(
            identity,
            handler,
            new RecordingAuthenticationSession("token"),
            cache);

        var first = await coordinator.TriggerAsync(SyncReason.Startup);
        var second = await coordinator.TriggerAsync(SyncReason.Periodic);

        Assert.Equal(ClientSyncRunStatus.ProtocolError, first.Status);
        Assert.Equal(ClientSyncRunStatus.Completed, second.Status);
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task ClientSync_WhenTriggersOverlap_RunsOneFlightAndAtMostOnePriorityRerun()
    {
        var identity = CreateIdentity();
        var conversation = CreateConversation("Single flight");
        var firstEntered = NewSignal();
        var firstRelease = NewSignal();
        var rerunEntered = NewSignal();
        var rerunRelease = NewSignal();
        var handler = new ScriptedHttpHandler();
        handler.Enqueue(async (_, cancellationToken) =>
        {
            firstEntered.TrySetResult();
            await firstRelease.Task.WaitAsync(cancellationToken);
            return Ok(new ConversationListResponse([conversation], Complete: true));
        });
        handler.EnqueueResponse(Ok(new SyncResponse([], 0, 0, HasMore: false)));
        handler.Enqueue(async (_, cancellationToken) =>
        {
            rerunEntered.TrySetResult();
            await rerunRelease.Task.WaitAsync(cancellationToken);
            return Ok(new ConversationListResponse([conversation], Complete: true));
        });
        handler.EnqueueResponse(Ok(new SyncResponse([], 0, 0, HasMore: false)));
        await using var cache = await CreateCacheAsync(identity);
        await using var coordinator = CreateCoordinator(
            identity,
            handler,
            new RecordingAuthenticationSession("token"),
            cache);

        var startup = coordinator.TriggerAsync(SyncReason.Startup);
        await firstEntered.Task;
        var periodic = coordinator.TriggerAsync(SyncReason.Periodic);
        var reconnect = coordinator.TriggerAsync(SyncReason.Reconnect);
        var windowActivated = coordinator.TriggerAsync(SyncReason.WindowActivated);
        Assert.Same(startup, periodic);
        Assert.Same(startup, reconnect);
        Assert.Same(startup, windowActivated);
        firstRelease.TrySetResult();
        await rerunEntered.Task;
        var duringRerun = coordinator.TriggerAsync(SyncReason.Periodic);
        Assert.Same(startup, duringRerun);
        rerunRelease.TrySetResult();

        var outcomes = await Task.WhenAll(
            startup,
            periodic,
            reconnect,
            windowActivated,
            duringRerun);

        Assert.All(outcomes, outcome =>
        {
            Assert.Equal(ClientSyncRunStatus.Completed, outcome.Status);
            Assert.Equal(SyncReason.WindowActivated, outcome.Reason);
            Assert.Equal(2, outcome.RoundsExecuted);
        });
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task ClientSync_WhenStartupRecoveryFails_PendingRerunKeepsStartupReason()
    {
        var identity = CreateIdentity();
        var conversation = CreateConversation("Startup recovery");
        var entered = NewSignal();
        var release = NewSignal();
        var handler = new ScriptedHttpHandler();
        handler.Enqueue(async (_, cancellationToken) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });
        for (var attempt = 0; attempt < 3; attempt++)
        {
            handler.EnqueueResponse(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }

        handler.EnqueueResponse(Ok(new ConversationListResponse([conversation], Complete: true)));
        handler.EnqueueResponse(Ok(new SyncResponse([], 0, 0, HasMore: false)));
        await using var cache = await CreateCacheAsync(identity);
        await using var coordinator = CreateCoordinator(
            identity,
            handler,
            new RecordingAuthenticationSession("token"),
            cache);

        var startup = coordinator.TriggerAsync(SyncReason.Startup);
        await entered.Task;
        var reconnect = coordinator.TriggerAsync(SyncReason.Reconnect);
        release.TrySetResult();
        var outcomes = await Task.WhenAll(startup, reconnect);

        Assert.All(outcomes, outcome =>
        {
            Assert.Equal(ClientSyncRunStatus.Completed, outcome.Status);
            Assert.Equal(SyncReason.Startup, outcome.Reason);
            Assert.Equal(2, outcome.RoundsExecuted);
        });
        Assert.Equal(6, handler.Requests.Count);
    }

    [Fact]
    public async Task ClientSync_WhenPreviousFlightCompleted_NextTriggerStartsNewRound()
    {
        var identity = CreateIdentity();
        var conversation = CreateConversation("Repeated flight");
        var handler = new ScriptedHttpHandler();
        for (var round = 0; round < 2; round++)
        {
            handler.EnqueueResponse(Ok(
                new ConversationListResponse([conversation], Complete: true)));
            handler.EnqueueResponse(Ok(new SyncResponse([], 0, 0, HasMore: false)));
        }

        await using var cache = await CreateCacheAsync(identity);
        await using var coordinator = CreateCoordinator(
            identity,
            handler,
            new RecordingAuthenticationSession("token"),
            cache);

        var first = await coordinator.TriggerAsync(SyncReason.Startup);
        var second = await coordinator.TriggerAsync(SyncReason.Periodic);

        Assert.Equal(ClientSyncRunStatus.Completed, first.Status);
        Assert.Equal(ClientSyncRunStatus.Completed, second.Status);
        Assert.Equal(SyncReason.Periodic, second.Reason);
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task ClientSync_WhenCallerCancelsWait_SharedFlightContinues()
    {
        var identity = CreateIdentity();
        var conversation = CreateConversation("Caller cancellation");
        var entered = NewSignal();
        var release = NewSignal();
        var handler = new ScriptedHttpHandler();
        handler.Enqueue(async (_, cancellationToken) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return Ok(new ConversationListResponse([conversation], Complete: true));
        });
        handler.EnqueueResponse(Ok(new SyncResponse([], 1, 1, HasMore: false)));
        await using var cache = await CreateCacheAsync(identity);
        await using var coordinator = CreateCoordinator(
            identity,
            handler,
            new RecordingAuthenticationSession("token"),
            cache);
        using var callerCancellation = new CancellationTokenSource();

        var caller = coordinator.TriggerAsync(SyncReason.Startup, callerCancellation.Token);
        await entered.Task;
        callerCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => caller);
        release.TrySetResult();
        await WaitUntilAsync(async () => (await cache.ReadLastSyncCursorAsync()).Cursor == 1);

        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task ClientSync_WhenDisposed_CancelsAccountFlight()
    {
        var identity = CreateIdentity();
        var entered = NewSignal();
        var handler = new ScriptedHttpHandler();
        handler.Enqueue(async (_, cancellationToken) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        });
        await using var cache = await CreateCacheAsync(identity);
        var coordinator = CreateCoordinator(
            identity,
            handler,
            new RecordingAuthenticationSession("token"),
            cache);

        var flight = coordinator.TriggerAsync(SyncReason.Startup);
        await entered.Task;
        await coordinator.DisposeAsync();
        var outcome = await flight;

        Assert.Equal(ClientSyncRunStatus.Canceled, outcome.Status);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => coordinator.TriggerAsync(SyncReason.Periodic));
    }

    [Fact]
    public async Task ClientSync_WhenTokenIsMissing_DoesNotSendRequest()
    {
        var identity = CreateIdentity();
        var handler = new ScriptedHttpHandler();
        await using var cache = await CreateCacheAsync(identity);
        await using var coordinator = CreateCoordinator(
            identity,
            handler,
            new RecordingAuthenticationSession(null),
            cache);

        var outcome = await coordinator.TriggerAsync(SyncReason.Startup);

        Assert.Equal(ClientSyncRunStatus.AuthenticationRequired, outcome.Status);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ClientSync_WhenTokenCannotFormBearerHeader_DoesNotSendRequest()
    {
        var identity = CreateIdentity();
        var handler = new ScriptedHttpHandler();
        await using var cache = await CreateCacheAsync(identity);
        await using var coordinator = CreateCoordinator(
            identity,
            handler,
            new RecordingAuthenticationSession("invalid\r\ntoken"),
            cache);

        var outcome = await coordinator.TriggerAsync(SyncReason.Startup);

        Assert.Equal(ClientSyncRunStatus.AuthenticationRequired, outcome.Status);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void ClientSyncOutcome_WhenFormatted_DoesNotContainTransportOrCursorData()
    {
        var outcome = new ClientSyncRunOutcome(
            ClientSyncRunStatus.TransientFailure,
            SyncReason.Reconnect,
            2);

        Assert.Equal(
            "ClientSyncRunOutcome { Status = TransientFailure, Reason = Reconnect, RoundsExecuted = 2 }",
            outcome.ToString());

        var sensitiveSnapshot = new ConversationListResponse(
            [CreateConversation("classified-conversation-name")],
            Complete: true);
        var httpResult = ClientSyncHttpResult<ConversationListResponse>.Success(sensitiveSnapshot);
        Assert.DoesNotContain("classified-conversation-name", httpResult.ToString());
        Assert.Contains("Value = [REDACTED]", httpResult.ToString());
    }

    [Fact]
    public async Task ClientSync_WhenTransientExceptionIsLogged_RedactsTransportState()
    {
        const string secretToken = "secret-access-token-987654";
        const string secretError = "classified body and cursor 7654321";
        var identity = CreateIdentity();
        var handler = new ScriptedHttpHandler();
        for (var attempt = 0; attempt < 4; attempt++)
        {
            handler.Enqueue((_, _) => throw new HttpRequestException(secretError));
        }

        var logger = new RecordingLogger<ClientSyncCoordinator>();
        await using var cache = await CreateCacheAsync(identity);
        await using var coordinator = CreateCoordinator(
            identity,
            handler,
            new RecordingAuthenticationSession(secretToken),
            cache,
            logger: logger);

        var outcome = await coordinator.TriggerAsync(SyncReason.Startup);
        var combined = string.Join(Environment.NewLine, logger.Entries);

        Assert.Equal(ClientSyncRunStatus.TransientFailure, outcome.Status);
        Assert.DoesNotContain(secretToken, combined, StringComparison.Ordinal);
        Assert.DoesNotContain(secretError, combined, StringComparison.Ordinal);
        Assert.DoesNotContain("relaycove.example", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/api/", combined, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(rootDirectory))
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    private AccountScopeIdentity CreateIdentity() => CreateIdentity(UserId);

    private AccountScopeIdentity CreateIdentity(Guid userId) => AccountScopeIdentity.Create(
        new Uri("https://relaycove.example/relay/"),
        userId,
        rootDirectory);

    private static Task<AccountScopedLocalCache> CreateCacheAsync(AccountScopeIdentity identity) =>
        AccountScopedLocalCache.CreateAsync(
            identity,
            NullLogger<AccountScopedLocalCache>.Instance);

    private static ClientSyncCoordinator CreateCoordinator(
        AccountScopeIdentity identity,
        ScriptedHttpHandler handler,
        IClientAuthenticationSession authenticationSession,
        AccountScopedLocalCache cache,
        List<TimeSpan>? delays = null,
        ILogger<ClientSyncCoordinator>? logger = null,
        Action? requestReadThroughUpload = null,
        IClientNotificationRoundCoordinator? notificationRoundCoordinator = null)
    {
        var httpClient = new HttpClient(handler, disposeHandler: false);
        return new ClientSyncCoordinator(
            identity,
            httpClient,
            authenticationSession,
            cache,
            logger ?? NullLogger<ClientSyncCoordinator>.Instance,
            delayAsync: (delay, _) =>
            {
                delays?.Add(delay);
                return Task.CompletedTask;
            },
            nextJitter: () => 0,
            timeProvider: TimeProvider.System,
            requestReadThroughUpload: requestReadThroughUpload,
            notificationRoundCoordinator: notificationRoundCoordinator);
    }

    private static ConversationDto CreateConversation(string name) => new(
        Guid.NewGuid(),
        ConversationType.PrivateChannel,
        name,
        null,
        DateTimeOffset.Parse("2026-08-03T01:00:00Z"),
        DateTimeOffset.Parse("2026-08-03T02:00:00Z"),
        0,
        0,
        0);

    private static MessageDto CreateMessage(long id, Guid conversationId) => new(
        id,
        Guid.NewGuid(),
        conversationId,
        Guid.NewGuid(),
        "Sender",
        MessageType.Text,
        $"message {id}",
        null,
        Array.Empty<AttachmentDto>(),
        [Guid.NewGuid()],
        new DateTimeOffset(2026, 8, 3, 3, 0, 0, TimeSpan.Zero).AddSeconds(id));

    private static PendingMessage CreatePending(Guid conversationId) => new(
        Guid.NewGuid(),
        conversationId,
        UserId,
        "Current user",
        MessageType.Text,
        "pending body",
        null,
        Array.Empty<Guid>(),
        DateTimeOffset.Parse("2026-08-03T04:00:00Z"));

    private static HttpResponseMessage Ok<T>(T value) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(value),
    };

    private static HttpResponseMessage Error(HttpStatusCode status, string code) => new(status)
    {
        Content = JsonContent.Create(new ApiErrorResponse(code, "Safe test error.")),
    };

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task WaitUntilAsync(Func<Task<bool>> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!await predicate())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("The expected sync state was not observed.");
            }

            await Task.Delay(10);
        }
    }

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

    private sealed class RecordingAuthenticationSession(string? accessToken) :
        IClientAuthenticationSession
    {
        private readonly Queue<(bool Success, string? AccessToken)> refreshResults = new();
        private string? accessToken = accessToken;

        public int RefreshCount { get; private set; }

        public List<string> RejectedTokens { get; } = [];

        public ValueTask<string?> GetAccessTokenAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(accessToken);
        }

        public Task<bool> TryRefreshAccessTokenAsync(
            string rejectedAccessToken,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RefreshCount++;
            RejectedTokens.Add(rejectedAccessToken);
            if (refreshResults.Count == 0)
            {
                return Task.FromResult(false);
            }

            var result = refreshResults.Dequeue();
            if (result.Success)
            {
                accessToken = result.AccessToken;
            }

            return Task.FromResult(result.Success);
        }

        public void EnqueueRefresh(bool success, string? replacementToken) =>
            refreshResults.Enqueue((success, replacementToken));
    }

    private sealed class ScriptedHttpHandler : HttpMessageHandler
    {
        private readonly ConcurrentQueue<
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> steps = new();

        public List<RequestRecord> Requests { get; } = [];

        public void Enqueue(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> step) =>
            steps.Enqueue(step);

        public void EnqueueResponse(HttpResponseMessage response) =>
            Enqueue((_, _) => Task.FromResult(response));

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Requests.Add(new RequestRecord(
                    request.RequestUri!.PathAndQuery,
                    request.Headers.Authorization?.Parameter));
            }

            if (!steps.TryDequeue(out var step))
            {
                throw new InvalidOperationException("No scripted HTTP response remains.");
            }

            return step(request, cancellationToken);
        }
    }

    private sealed record RequestRecord(string PathAndQuery, string? BearerToken);

    private sealed class RecordingNotificationRoundCoordinator :
        IClientNotificationRoundCoordinator
    {
        private long generation;

        public List<string> Events { get; } = [];

        public ClientNotificationRoundToken OpenRound(SyncReason reason)
        {
            var token = new ClientNotificationRoundToken(++generation, reason);
            Events.Add($"open:{reason}");
            return token;
        }

        public Task SnapshotCommittedAsync(
            ClientNotificationRoundToken token,
            CancellationToken cancellationToken)
        {
            Events.Add($"snapshot:{token.Generation}");
            return Task.CompletedTask;
        }

        public void SubmitSyncCandidates(
            ClientNotificationRoundToken token,
            IReadOnlyCollection<long> messageIds) =>
            Events.Add($"candidates:{string.Join(',', messageIds)}");

        public Task SubmitRealtimeCandidateAsync(
            long messageId,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task CloseRoundAsync(
            ClientNotificationRoundToken token,
            ClientSyncRunStatus status)
        {
            Events.Add($"close:{token.Generation}:{status}");
            return Task.CompletedTask;
        }

        public Task ConversationRevokedAsync(
            Guid conversationId,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            Events.Add("dispose");
            return ValueTask.CompletedTask;
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
}
