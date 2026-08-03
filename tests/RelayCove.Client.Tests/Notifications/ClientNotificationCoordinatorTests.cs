using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RelayCove.Client.Notifications;
using RelayCove.Client.Storage;
using RelayCove.Shared.Conversations;
using RelayCove.Shared.Messages;

namespace RelayCove.Client.Tests.Notifications;

[Collection(SqliteTestCollection.Name)]
public sealed class ClientNotificationCoordinatorTests : IDisposable
{
    private static readonly Guid UserId =
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid OtherUserId =
        Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Uri ServerBaseUri = new("https://relaycove.example/team/");
    private readonly string rootDirectory = Path.Combine(
        Path.GetTempPath(),
        "RelayCove.Client.NotificationTests",
        Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(1, true, 1)]
    [InlineData(2, false, 2)]
    [InlineData(3, true, 1)]
    public async Task DispatchPerMessage_WhenPlatformReturnsStatus_PersistsOnlyTerminalHandling(
        int platformStatus,
        bool expectedHandled,
        int expectedDispatchStatus)
    {
        var prepared = await PrepareAsync(messageCount: 1);
        await using var cache = prepared.Cache;
        var platform = new FakeNotificationPlatform
        {
            SubmitResult = new ClientNotificationPlatformResult(
                (ClientNotificationPlatformStatus)platformStatus),
        };
        await using var coordinator = CreateCoordinator(prepared, platform);

        var outcome = await coordinator.DispatchAsync(
            prepared.MessageIds,
            ClientNotificationDispatchMode.PerMessage);

        Assert.Equal((ClientNotificationDispatchStatus)expectedDispatchStatus, outcome.Status);
        Assert.Equal(expectedHandled, ReadNotificationHandled(prepared.Identity, 1));
        Assert.Single(platform.Requests);
        var request = platform.Requests.Single();
        Assert.Equal(prepared.Identity.Id, request.AccountScopeId);
        Assert.Equal(NotificationPolicy.PerMessage, request.Policy);
    }

    [Fact]
    public async Task Dispatch_WhenPlatformIsUnavailable_DoesNotClaimEligibleCandidates()
    {
        var prepared = await PrepareAsync(messageCount: 1);
        await using var cache = prepared.Cache;
        var platform = new FakeNotificationPlatform();
        await using var coordinator = CreateCoordinator(
            prepared,
            platform,
            static () => ClientNotificationSettingsSnapshot.Unavailable);

        var outcome = await coordinator.DispatchAsync(
            prepared.MessageIds,
            ClientNotificationDispatchMode.PerMessage);

        Assert.Equal(ClientNotificationDispatchStatus.TransientFailure, outcome.Status);
        Assert.Empty(platform.Requests);
        Assert.False(ReadNotificationHandled(prepared.Identity, 1));
    }

    [Fact]
    public async Task Dispatch_WhenPlatformIsUnavailable_CommitsForegroundSuppression()
    {
        var prepared = await PrepareAsync(messageCount: 1);
        await using var cache = prepared.Cache;
        var platform = new FakeNotificationPlatform();
        await using var coordinator = CreateCoordinator(
            prepared,
            platform,
            static () => ClientNotificationSettingsSnapshot.Unavailable,
            () => prepared.Conversation.Id);

        var outcome = await coordinator.DispatchAsync(
            prepared.MessageIds,
            ClientNotificationDispatchMode.PerMessage);

        Assert.Equal(ClientNotificationDispatchStatus.Completed, outcome.Status);
        Assert.Empty(platform.Requests);
        Assert.True(ReadNotificationHandled(prepared.Identity, 1));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Dispatch_WhenPlatformBecomesUnavailable_StillCommitsForegroundSuppression(
        int dispatchMode)
    {
        var prepared = await PrepareAsync(messageCount: 1);
        await using var cache = prepared.Cache;
        var platform = new FakeNotificationPlatform();
        var settingsReadCount = 0;
        var foregroundReadCount = 0;
        await using var coordinator = CreateCoordinator(
            prepared,
            platform,
            () => Interlocked.Increment(ref settingsReadCount) == 1
                ? ClientNotificationSettingsSnapshot.Enabled
                : ClientNotificationSettingsSnapshot.Unavailable,
            () => Interlocked.Increment(ref foregroundReadCount) == 1
                ? null
                : prepared.Conversation.Id);

        var outcome = await coordinator.DispatchAsync(
            prepared.MessageIds,
            (ClientNotificationDispatchMode)dispatchMode);

        Assert.Equal(ClientNotificationDispatchStatus.Completed, outcome.Status);
        Assert.Empty(platform.Requests);
        Assert.True(ReadNotificationHandled(prepared.Identity, 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Dispatch_WhenPolicySuppresses_MarksHandledWithoutCallingPlatform(int caseId)
    {
        var prepared = await PrepareAsync(messageCount: 1);
        await using var cache = prepared.Cache;
        var platform = new FakeNotificationPlatform();
        var mode = caseId == 0
            ? ClientNotificationDispatchMode.None
            : ClientNotificationDispatchMode.PerMessage;
        var settings = caseId switch
        {
            0 => ClientNotificationSettingsSnapshot.Unavailable,
            1 => new ClientNotificationSettingsSnapshot(
                ClientNotificationPlatformAvailability.Disabled,
                IsDoNotDisturbEnabled: false),
            _ => new ClientNotificationSettingsSnapshot(
                ClientNotificationPlatformAvailability.Available,
                IsDoNotDisturbEnabled: true),
        };
        await using var coordinator = CreateCoordinator(prepared, platform, () => settings);

        var outcome = await coordinator.DispatchAsync(prepared.MessageIds, mode);

        Assert.Equal(ClientNotificationDispatchStatus.Completed, outcome.Status);
        Assert.Empty(platform.Requests);
        Assert.True(ReadNotificationHandled(prepared.Identity, 1));
    }

    [Fact]
    public async Task MergeLive_WhenConversationIsMuted_SuppressesInIngestionTransaction()
    {
        var prepared = await PrepareAsync(messageCount: 0, isMuted: true);
        await using var cache = prepared.Cache;

        var outcome = await cache.MergeIncomingMessageAsync(
            CreateMessage(1, prepared.Conversation.Id));

        Assert.Null(outcome.NotificationCandidateMessageId);
        Assert.True(ReadNotificationHandled(prepared.Identity, 1));
    }

    [Fact]
    public async Task DispatchSummary_WhenAccepted_SubmitsOneSummaryAndHandlesAllCandidates()
    {
        var prepared = await PrepareAsync(messageCount: 12);
        await using var cache = prepared.Cache;
        var platform = new FakeNotificationPlatform();
        await using var coordinator = CreateCoordinator(prepared, platform);

        var outcome = await coordinator.DispatchAsync(
            prepared.MessageIds,
            ClientNotificationDispatchMode.Automatic);

        var request = Assert.Single(platform.Requests);
        Assert.Equal(NotificationPolicy.Summary, request.Policy);
        Assert.Equal(12, request.Messages.Count);
        Assert.Equal(12, outcome.AcceptedCount);
        Assert.All(prepared.MessageIds, id =>
            Assert.True(ReadNotificationHandled(prepared.Identity, id)));
    }

    [Theory]
    [InlineData(10, 10, 1)]
    [InlineData(11, 1, 2)]
    public async Task DispatchAutomatic_WhenCandidateCountCrossesBoundary_SelectsExpectedPolicy(
        int messageCount,
        int expectedRequestCount,
        int expectedPolicy)
    {
        var prepared = await PrepareAsync(messageCount);
        await using var cache = prepared.Cache;
        var platform = new FakeNotificationPlatform();
        await using var coordinator = CreateCoordinator(prepared, platform);

        var outcome = await coordinator.DispatchAsync(
            prepared.MessageIds,
            ClientNotificationDispatchMode.Automatic);

        Assert.Equal(ClientNotificationDispatchStatus.Completed, outcome.Status);
        Assert.Equal(expectedRequestCount, platform.Requests.Count);
        Assert.All(
            platform.Requests,
            request => Assert.Equal((NotificationPolicy)expectedPolicy, request.Policy));
    }

    [Fact]
    public async Task DispatchPerMessage_WhenOneSubmissionIsTransient_CommitsAcceptedPeers()
    {
        var prepared = await PrepareAsync(messageCount: 3);
        await using var cache = prepared.Cache;
        var submissions = 0;
        var platform = new FakeNotificationPlatform
        {
            SubmitAction = (_, _) => Task.FromResult(
                Interlocked.Increment(ref submissions) == 2
                    ? ClientNotificationPlatformResult.TransientFailure
                    : ClientNotificationPlatformResult.Accepted),
        };
        await using var coordinator = CreateCoordinator(prepared, platform);

        var outcome = await coordinator.DispatchAsync(
            prepared.MessageIds,
            ClientNotificationDispatchMode.PerMessage);

        Assert.Equal(ClientNotificationDispatchStatus.TransientFailure, outcome.Status);
        Assert.True(ReadNotificationHandled(prepared.Identity, 1));
        Assert.False(ReadNotificationHandled(prepared.Identity, 2));
        Assert.True(ReadNotificationHandled(prepared.Identity, 3));
        Assert.Equal(3, platform.Requests.Count);
    }

    [Fact]
    public async Task DispatchPerMessage_WhenManyToastsAreAccepted_SignalsAttentionOnce()
    {
        var prepared = await PrepareAsync(messageCount: 3);
        await using var cache = prepared.Cache;
        var attention = new RecordingNotificationAttention();
        await using var coordinator = CreateCoordinator(
            prepared,
            new FakeNotificationPlatform(),
            notificationAttention: attention);

        var outcome = await coordinator.DispatchAsync(
            prepared.MessageIds,
            ClientNotificationDispatchMode.PerMessage);

        Assert.Equal(ClientNotificationDispatchStatus.Completed, outcome.Status);
        Assert.Equal(3, outcome.AcceptedCount);
        Assert.Equal(1, attention.SignalCount);
    }

    [Fact]
    public async Task Dispatch_WhenCallsShareRoundGate_SignalsAttentionOnceAcrossCalls()
    {
        var prepared = await PrepareAsync(messageCount: 2);
        await using var cache = prepared.Cache;
        var attention = new RecordingNotificationAttention();
        await using var coordinator = CreateCoordinator(
            prepared,
            new FakeNotificationPlatform(),
            notificationAttention: attention);
        var attentionGate = new ClientNotificationAttentionGate();

        var first = await coordinator.DispatchAsync(
            [1],
            ClientNotificationDispatchMode.PerMessage,
            attentionGate: attentionGate);
        var second = await coordinator.DispatchAsync(
            [2],
            ClientNotificationDispatchMode.PerMessage,
            attentionGate: attentionGate);

        Assert.Equal(1, first.AcceptedCount);
        Assert.Equal(1, second.AcceptedCount);
        Assert.Equal(1, attention.SignalCount);
    }

    [Fact]
    public async Task Dispatch_WhenCallsUseIndependentGates_SignalsAttentionForEachCall()
    {
        var prepared = await PrepareAsync(messageCount: 2);
        await using var cache = prepared.Cache;
        var attention = new RecordingNotificationAttention();
        await using var coordinator = CreateCoordinator(
            prepared,
            new FakeNotificationPlatform(),
            notificationAttention: attention);

        var first = await coordinator.DispatchAsync(
            [1],
            ClientNotificationDispatchMode.PerMessage);
        var second = await coordinator.DispatchAsync(
            [2],
            ClientNotificationDispatchMode.PerMessage);

        Assert.Equal(1, first.AcceptedCount);
        Assert.Equal(1, second.AcceptedCount);
        Assert.Equal(2, attention.SignalCount);
    }

    [Fact]
    public async Task DispatchSummary_WhenToastIsAccepted_SignalsAttentionOnce()
    {
        var prepared = await PrepareAsync(messageCount: 12);
        await using var cache = prepared.Cache;
        var attention = new RecordingNotificationAttention();
        await using var coordinator = CreateCoordinator(
            prepared,
            new FakeNotificationPlatform(),
            notificationAttention: attention);

        var outcome = await coordinator.DispatchAsync(
            prepared.MessageIds,
            ClientNotificationDispatchMode.Summary);

        Assert.Equal(ClientNotificationDispatchStatus.Completed, outcome.Status);
        Assert.Equal(12, outcome.AcceptedCount);
        Assert.Equal(1, attention.SignalCount);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Dispatch_WhenToastIsNotAccepted_DoesNotSignalAttention(int platformStatus)
    {
        var prepared = await PrepareAsync(messageCount: 1);
        await using var cache = prepared.Cache;
        var attention = new RecordingNotificationAttention();
        var platform = new FakeNotificationPlatform
        {
            SubmitResult = new ClientNotificationPlatformResult(
                (ClientNotificationPlatformStatus)platformStatus),
        };
        await using var coordinator = CreateCoordinator(
            prepared,
            platform,
            notificationAttention: attention);

        _ = await coordinator.DispatchAsync(
            prepared.MessageIds,
            ClientNotificationDispatchMode.PerMessage);

        Assert.Equal(0, attention.SignalCount);
    }

    [Fact]
    public async Task Dispatch_WhenAttentionThrows_StillCommitsAcceptedToastAndLogsTypeOnly()
    {
        var prepared = await PrepareAsync(messageCount: 1);
        await using var cache = prepared.Cache;
        var attention = new RecordingNotificationAttention
        {
            SignalException = new InvalidOperationException("sensitive attention"),
        };
        var logger = new RecordingLogger<ClientNotificationCoordinator>();
        await using var coordinator = CreateCoordinator(
            prepared,
            new FakeNotificationPlatform(),
            logger: logger,
            notificationAttention: attention);

        var outcome = await coordinator.DispatchAsync(
            prepared.MessageIds,
            ClientNotificationDispatchMode.PerMessage);

        Assert.Equal(ClientNotificationDispatchStatus.Completed, outcome.Status);
        Assert.Equal(1, outcome.AcceptedCount);
        Assert.True(ReadNotificationHandled(prepared.Identity, 1));
        Assert.Equal(1, attention.SignalCount);
        Assert.Contains(
            logger.Entries,
            entry => entry.Contains(nameof(InvalidOperationException), StringComparison.Ordinal));
        Assert.DoesNotContain(
            logger.Entries,
            entry => entry.Contains("sensitive attention", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Dispatch_WhenCallerCancelsWait_DoesNotCancelAcceptedPlatformFlight()
    {
        var prepared = await PrepareAsync(messageCount: 1);
        await using var cache = prepared.Cache;
        var entered = NewSignal();
        var release = NewSignal();
        var platformTokenCanceled = false;
        var platform = new FakeNotificationPlatform
        {
            SubmitAction = async (_, cancellationToken) =>
            {
                entered.TrySetResult();
                using var registration = cancellationToken.Register(
                    () => platformTokenCanceled = true);
                await release.Task;
                return ClientNotificationPlatformResult.Accepted;
            },
        };
        await using var coordinator = CreateCoordinator(prepared, platform);
        using var callerCancellation = new CancellationTokenSource();
        var dispatch = coordinator.DispatchAsync(
            prepared.MessageIds,
            ClientNotificationDispatchMode.PerMessage,
            callerCancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        callerCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatch);
        Assert.False(platformTokenCanceled);
        release.TrySetResult();
        await WaitUntilAsync(() => ReadNotificationHandled(prepared.Identity, 1));
    }

    [Fact]
    public async Task DisposeAsync_WhenPlatformFlightIsActive_CancelsFlightAndLeavesCandidate()
    {
        var prepared = await PrepareAsync(messageCount: 1);
        await using var cache = prepared.Cache;
        var entered = NewSignal();
        var platformCanceled = false;
        var platform = new FakeNotificationPlatform
        {
            SubmitAction = async (_, cancellationToken) =>
            {
                entered.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    platformCanceled = true;
                    throw;
                }

                throw new InvalidOperationException("Unreachable.");
            },
        };
        var coordinator = CreateCoordinator(prepared, platform);
        var dispatch = coordinator.DispatchAsync(
            prepared.MessageIds,
            ClientNotificationDispatchMode.PerMessage);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await coordinator.DisposeAsync();
        var outcome = await dispatch;

        Assert.True(platformCanceled);
        Assert.Equal(ClientNotificationDispatchStatus.Canceled, outcome.Status);
        Assert.False(ReadNotificationHandled(prepared.Identity, 1));
    }

    [Fact]
    public async Task Revocation_DuringAcceptedSubmission_ClearsAfterAccessIsDenied()
    {
        var prepared = await PrepareAsync(messageCount: 1);
        await using var cache = prepared.Cache;
        var entered = NewSignal();
        var release = NewSignal();
        var platform = new FakeNotificationPlatform
        {
            SubmitAction = async (_, _) =>
            {
                entered.TrySetResult();
                await release.Task;
                return ClientNotificationPlatformResult.Accepted;
            },
        };
        await using var coordinator = CreateCoordinator(prepared, platform);
        var dispatch = coordinator.DispatchAsync(
            prepared.MessageIds,
            ClientNotificationDispatchMode.PerMessage);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(
            LocalCacheOperationStatus.RevokedConversation,
            await cache.RevokeConversationAccessAsync(prepared.Conversation.Id));
        var clear = coordinator.ConversationRevokedAsync(prepared.Conversation.Id);
        release.TrySetResult();
        await Task.WhenAll(dispatch, clear);

        Assert.NotEmpty(platform.ClearedConversations);
        Assert.True(platform.ClearedSummaries > 0);
        Assert.All(
            platform.ClearedConversations,
            conversationId => Assert.Equal(prepared.Conversation.Id, conversationId));
        Assert.Equal(
            LocalCacheOperationStatus.RevokedConversation,
            cache.GetNotificationConversationAccessStatus(prepared.Conversation.Id));
        var afterClear = await cache
            .ApplyAuthoritativeConversationSnapshotWithRevocationsAsync(
                new ConversationListResponse([], Complete: true));
        Assert.Empty(afterClear.RevokedConversationIds);
    }

    [Fact]
    public async Task Revocation_WhenPlatformClearIsTransient_RemainsPendingForNextSnapshot()
    {
        var prepared = await PrepareAsync(messageCount: 0);
        await using var cache = prepared.Cache;
        var platform = new FakeNotificationPlatform
        {
            ClearResult = ClientNotificationPlatformResult.TransientFailure,
        };
        await using var coordinator = CreateCoordinator(prepared, platform);

        Assert.Equal(
            LocalCacheOperationStatus.RevokedConversation,
            await cache.RevokeConversationAccessAsync(prepared.Conversation.Id));
        await coordinator.ConversationRevokedAsync(prepared.Conversation.Id);

        var retry = await cache
            .ApplyAuthoritativeConversationSnapshotWithRevocationsAsync(
                new ConversationListResponse([], Complete: true));
        Assert.Equal([prepared.Conversation.Id], retry.RevokedConversationIds);
    }

    [Fact]
    public async Task Revocation_WhenSummaryClearIsTransient_RemainsPendingForNextSnapshot()
    {
        var prepared = await PrepareAsync(messageCount: 0);
        await using var cache = prepared.Cache;
        var platform = new FakeNotificationPlatform
        {
            SummaryClearResult = ClientNotificationPlatformResult.TransientFailure,
        };
        await using var coordinator = CreateCoordinator(prepared, platform);

        Assert.Equal(
            LocalCacheOperationStatus.RevokedConversation,
            await cache.RevokeConversationAccessAsync(prepared.Conversation.Id));
        await coordinator.ConversationRevokedAsync(prepared.Conversation.Id);

        var snapshot = await cache.ApplyAuthoritativeConversationSnapshotWithRevocationsAsync(
            new ConversationListResponse([], Complete: true));
        Assert.Equal([prepared.Conversation.Id], snapshot.RevokedConversationIds);
        Assert.Single(platform.ClearedConversations);
        Assert.Equal(1, platform.ClearedSummaries);
    }

    [Fact]
    public async Task ConcurrentDispatches_AreSerializedPerAccount()
    {
        var prepared = await PrepareAsync(messageCount: 2);
        await using var cache = prepared.Cache;
        var platform = new FakeNotificationPlatform
        {
            SubmitAction = async (_, cancellationToken) =>
            {
                await Task.Delay(25, cancellationToken);
                return ClientNotificationPlatformResult.Accepted;
            },
        };
        await using var coordinator = CreateCoordinator(prepared, platform);

        await Task.WhenAll(
            coordinator.DispatchAsync([1], ClientNotificationDispatchMode.PerMessage),
            coordinator.DispatchAsync([2], ClientNotificationDispatchMode.PerMessage));

        Assert.Equal(1, platform.MaxConcurrentSubmissions);
        Assert.Equal(2, platform.Requests.Count);
    }

    [Fact]
    public async Task Dispatch_WhenPlatformThrows_DoesNotLogPayloadOrIdentity()
    {
        const string secret = "platform-secret-payload";
        var prepared = await PrepareAsync(messageCount: 1);
        await using var cache = prepared.Cache;
        var logger = new RecordingLogger<ClientNotificationCoordinator>();
        var platform = new FakeNotificationPlatform
        {
            SubmitAction = (_, _) => throw new InvalidOperationException(secret),
        };
        await using var coordinator = CreateCoordinator(
            prepared,
            platform,
            logger: logger);

        var outcome = await coordinator.DispatchAsync(
            prepared.MessageIds,
            ClientNotificationDispatchMode.PerMessage);

        Assert.Equal(ClientNotificationDispatchStatus.TransientFailure, outcome.Status);
        var logs = string.Join(Environment.NewLine, logger.Entries);
        Assert.DoesNotContain(secret, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(
            prepared.Conversation.Id.ToString(),
            logs,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("message 1", logs, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(rootDirectory))
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    private async Task<PreparedCache> PrepareAsync(int messageCount, bool isMuted = false)
    {
        var identity = AccountScopeIdentity.Create(ServerBaseUri, UserId, rootDirectory);
        var cache = await AccountScopedLocalCache.CreateAsync(
            identity,
            NullLogger<AccountScopedLocalCache>.Instance);
        await cache.AdoptNotificationStateAsync();
        var conversation = new ConversationDto(
            Guid.NewGuid(),
            ConversationType.PrivateChannel,
            "Conversation",
            null,
            DateTimeOffset.Parse("2026-08-03T01:00:00Z"),
            DateTimeOffset.Parse("2026-08-03T02:00:00Z"),
            LastMessageId: 0,
            LastReadMessageId: 0,
            UnreadCount: 0,
            IsMuted: isMuted);
        Assert.Equal(
            LocalCacheOperationStatus.Ready,
            await cache.ApplyAuthoritativeConversationSnapshotAsync(
                new ConversationListResponse([conversation], Complete: true)));
        var messageIds = new List<long>(messageCount);
        for (var id = 1; id <= messageCount; id++)
        {
            var outcome = await cache.MergeIncomingMessageAsync(CreateMessage(id, conversation.Id));
            Assert.Equal(id, outcome.NotificationCandidateMessageId);
            messageIds.Add(id);
        }

        return new PreparedCache(identity, cache, conversation, messageIds);
    }

    private static ClientNotificationCoordinator CreateCoordinator(
        PreparedCache prepared,
        IClientNotificationPlatform platform,
        Func<ClientNotificationSettingsSnapshot>? settingsProvider = null,
        Func<Guid?>? foregroundConversationIdProvider = null,
        ILogger<ClientNotificationCoordinator>? logger = null,
        IClientNotificationAttention? notificationAttention = null) =>
        new(
            prepared.Identity,
            prepared.Cache,
            platform,
            settingsProvider ?? (static () => ClientNotificationSettingsSnapshot.Enabled),
            foregroundConversationIdProvider ?? (static () => null),
            logger ?? NullLogger<ClientNotificationCoordinator>.Instance,
            notificationAttention);

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

    private static bool ReadNotificationHandled(
        AccountScopeIdentity identity,
        long messageId)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = identity.DatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            ForeignKeys = true,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT IsNotificationHandled
            FROM LocalMessages
            WHERE ServerMessageId = $messageId;
            """;
        command.Parameters.AddWithValue("$messageId", messageId);
        return Convert.ToInt32(command.ExecuteScalar()) != 0;
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("The expected notification state was not observed.");
            }

            await Task.Delay(10);
        }
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed record PreparedCache(
        AccountScopeIdentity Identity,
        AccountScopedLocalCache Cache,
        ConversationDto Conversation,
        IReadOnlyList<long> MessageIds);

    private sealed class FakeNotificationPlatform : IClientNotificationPlatform
    {
        private readonly ConcurrentQueue<ClientNotificationRequest> requests = new();
        private readonly ConcurrentQueue<Guid> clearedConversations = new();
        private int clearedSummaries;
        private int activeSubmissions;
        private int maxConcurrentSubmissions;

        public ClientNotificationPlatformResult SubmitResult { get; init; } =
            ClientNotificationPlatformResult.Accepted;

        public ClientNotificationPlatformResult ClearResult { get; init; } =
            ClientNotificationPlatformResult.Accepted;

        public ClientNotificationPlatformResult? SummaryClearResult { get; init; }

        public Func<
            ClientNotificationRequest,
            CancellationToken,
            Task<ClientNotificationPlatformResult>>? SubmitAction
        {
            get;
            init;
        }

        public IReadOnlyCollection<ClientNotificationRequest> Requests => requests.ToArray();

        public IReadOnlyCollection<Guid> ClearedConversations => clearedConversations.ToArray();

        public int ClearedSummaries => Volatile.Read(ref clearedSummaries);

        public int MaxConcurrentSubmissions => Volatile.Read(ref maxConcurrentSubmissions);

        public async Task<ClientNotificationPlatformResult> SubmitAsync(
            ClientNotificationRequest request,
            CancellationToken cancellationToken)
        {
            requests.Enqueue(request);
            var active = Interlocked.Increment(ref activeSubmissions);
            UpdateMaximum(active);
            try
            {
                return SubmitAction is null
                    ? SubmitResult
                    : await SubmitAction(request, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref activeSubmissions);
            }
        }

        public Task<ClientNotificationPlatformResult> ClearConversationAsync(
            string accountScopeId,
            Guid conversationId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            clearedConversations.Enqueue(conversationId);
            return Task.FromResult(ClearResult);
        }

        public Task<ClientNotificationPlatformResult> ClearSummaryAsync(
            string accountScopeId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref clearedSummaries);
            return Task.FromResult(SummaryClearResult ?? ClearResult);
        }

        private void UpdateMaximum(int candidate)
        {
            while (true)
            {
                var current = Volatile.Read(ref maxConcurrentSubmissions);
                if (candidate <= current ||
                    Interlocked.CompareExchange(
                        ref maxConcurrentSubmissions,
                        candidate,
                        current) == current)
                {
                    return;
                }
            }
        }
    }

    private sealed class RecordingNotificationAttention : IClientNotificationAttention
    {
        private int signalCount;
        private int stopCount;

        public Exception? SignalException { get; init; }

        public int SignalCount => Volatile.Read(ref signalCount);

        public int StopCount => Volatile.Read(ref stopCount);

        public void SignalAcceptedToast()
        {
            Interlocked.Increment(ref signalCount);
            if (SignalException is not null)
            {
                throw SignalException;
            }
        }

        public void StopFlashing() => Interlocked.Increment(ref stopCount);
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
