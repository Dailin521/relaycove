using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using RelayCove.Client.Accounts;
using RelayCove.Client.Notifications;
using RelayCove.Client.Storage;
using RelayCove.Client.Sync;
using RelayCove.Shared.Conversations;
using RelayCove.Shared.Messages;

namespace RelayCove.Client.Tests.Notifications;

[Collection(SqliteTestCollection.Name)]
public sealed class ClientNotificationRoundCoordinatorTests : IDisposable
{
    private static readonly Guid UserId =
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid OtherUserId =
        Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Uri ServerBaseUri = new("https://relaycove.example/team/");
    private readonly string rootDirectory = Path.Combine(
        Path.GetTempPath(),
        "RelayCove.Client.NotificationRoundTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CompletedStartup_CombinesRoundCandidatesIntoOneSummary()
    {
        var prepared = await PrepareAsync();
        await using var cache = prepared.Cache;
        var sink = new RecordingNotificationCoordinator();
        await using var coordinator = CreateCoordinator(prepared, sink);
        var token = coordinator.OpenRound(SyncReason.Startup);
        coordinator.SubmitSyncCandidates(token, [10]);
        await coordinator.SubmitRealtimeCandidateAsync(20, CancellationToken.None);

        await coordinator.CloseRoundAsync(token, ClientSyncRunStatus.Completed);

        var dispatch = Assert.Single(sink.Dispatches);
        Assert.Equal(ClientNotificationDispatchMode.Summary, dispatch.Mode);
        Assert.Equal([10L, 20L], dispatch.MessageIds.Order().ToArray());
    }

    [Theory]
    [InlineData(false, 4)]
    [InlineData(true, 4)]
    public async Task CompletedReconnect_AppliesForegroundPolicyAndRecovery(
        bool foreground,
        int expectedMode)
    {
        var prepared = await PrepareAsync(recoveryMessageIds: [1]);
        await using var cache = prepared.Cache;
        if (foreground)
        {
            prepared.ActivityState.Update(new ClientActivitySnapshot(
                IsMainWindowVisible: true,
                IsMainWindowMinimized: false,
                HasForegroundFocus: true,
                OpenConversationId: prepared.Conversation.Id));
        }

        var sink = new RecordingNotificationCoordinator();
        await using var coordinator = CreateCoordinator(prepared, sink);
        var token = coordinator.OpenRound(SyncReason.Reconnect);
        await coordinator.SnapshotCommittedAsync(token, CancellationToken.None);
        coordinator.SubmitSyncCandidates(token, [10]);
        await coordinator.SubmitRealtimeCandidateAsync(20, CancellationToken.None);

        await coordinator.CloseRoundAsync(token, ClientSyncRunStatus.Completed);

        var dispatch = Assert.Single(sink.Dispatches);
        Assert.Equal((ClientNotificationDispatchMode)expectedMode, dispatch.Mode);
        Assert.Equal([1L, 10L, 20L], dispatch.MessageIds.Order().ToArray());
    }

    [Fact]
    public async Task WindowActivated_DoesNotCaptureOrConsumeOldRecovery()
    {
        var prepared = await PrepareAsync(recoveryMessageIds: [1]);
        await using var cache = prepared.Cache;
        var sink = new RecordingNotificationCoordinator();
        await using var coordinator = CreateCoordinator(prepared, sink);
        var token = coordinator.OpenRound(SyncReason.WindowActivated);
        await coordinator.SnapshotCommittedAsync(token, CancellationToken.None);
        coordinator.SubmitSyncCandidates(token, [10]);

        await coordinator.CloseRoundAsync(token, ClientSyncRunStatus.Completed);

        var dispatch = Assert.Single(sink.Dispatches);
        Assert.Equal(ClientNotificationDispatchMode.None, dispatch.Mode);
        Assert.Equal([10L], dispatch.MessageIds);
    }

    [Fact]
    public async Task FailedBackgroundRound_DispatchesRealtimeAndOldRecoveryButKeepsSyncCandidate()
    {
        var prepared = await PrepareAsync(recoveryMessageIds: [1]);
        await using var cache = prepared.Cache;
        var sink = new RecordingNotificationCoordinator();
        await using var coordinator = CreateCoordinator(prepared, sink);
        var token = coordinator.OpenRound(SyncReason.Periodic);
        await coordinator.SnapshotCommittedAsync(token, CancellationToken.None);
        coordinator.SubmitSyncCandidates(token, [10]);
        await coordinator.SubmitRealtimeCandidateAsync(20, CancellationToken.None);

        await coordinator.CloseRoundAsync(token, ClientSyncRunStatus.TransientFailure);

        Assert.Equal(2, sink.Dispatches.Count);
        Assert.Contains(
            sink.Dispatches,
            dispatch => dispatch.Mode == ClientNotificationDispatchMode.PerMessage &&
                dispatch.MessageIds.SequenceEqual([20L]));
        Assert.Contains(
            sink.Dispatches,
            dispatch => dispatch.Mode == ClientNotificationDispatchMode.Automatic &&
                dispatch.MessageIds.SequenceEqual([1L]));
        Assert.DoesNotContain(
            sink.Dispatches.SelectMany(dispatch => dispatch.MessageIds),
            messageId => messageId == 10);
        var dispatches = sink.Dispatches.ToArray();
        Assert.All(dispatches, dispatch => Assert.NotNull(dispatch.AttentionGate));
        Assert.Same(dispatches[0].AttentionGate, dispatches[1].AttentionGate);
    }

    [Fact]
    public async Task CanceledRound_DispatchesOnlyRealtimeFirstSourceCandidate()
    {
        var prepared = await PrepareAsync(recoveryMessageIds: [1]);
        await using var cache = prepared.Cache;
        var sink = new RecordingNotificationCoordinator();
        await using var coordinator = CreateCoordinator(prepared, sink);
        var token = coordinator.OpenRound(SyncReason.Startup);
        await coordinator.SnapshotCommittedAsync(token, CancellationToken.None);
        coordinator.SubmitSyncCandidates(token, [10]);
        await coordinator.SubmitRealtimeCandidateAsync(20, CancellationToken.None);

        await coordinator.CloseRoundAsync(token, ClientSyncRunStatus.Canceled);

        var dispatch = Assert.Single(sink.Dispatches);
        Assert.Equal(ClientNotificationDispatchMode.PerMessage, dispatch.Mode);
        Assert.Equal([20L], dispatch.MessageIds);
    }

    [Fact]
    public async Task RealtimeCloseRace_DispatchesEveryCandidateExactlyOnce()
    {
        var prepared = await PrepareAsync();
        await using var cache = prepared.Cache;
        var sink = new RecordingNotificationCoordinator();
        await using var coordinator = CreateCoordinator(prepared, sink);

        for (var messageId = 1; messageId <= 100; messageId++)
        {
            var token = coordinator.OpenRound(SyncReason.Startup);
            await Task.WhenAll(
                Task.Run(() => coordinator.SubmitRealtimeCandidateAsync(
                    messageId,
                    CancellationToken.None)),
                Task.Run(() => coordinator.CloseRoundAsync(
                    token,
                    ClientSyncRunStatus.Completed)));
        }

        var dispatchedIds = sink.Dispatches
            .SelectMany(dispatch => dispatch.MessageIds)
            .Order()
            .ToArray();
        Assert.Equal(Enumerable.Range(1, 100).Select(id => (long)id), dispatchedIds);
    }

    [Fact]
    public async Task StaleGeneration_CannotAddCandidatesToLaterRound()
    {
        var prepared = await PrepareAsync();
        await using var cache = prepared.Cache;
        var sink = new RecordingNotificationCoordinator();
        await using var coordinator = CreateCoordinator(prepared, sink);
        var stale = coordinator.OpenRound(SyncReason.Startup);
        await coordinator.CloseRoundAsync(stale, ClientSyncRunStatus.Canceled);
        var current = coordinator.OpenRound(SyncReason.Startup);

        coordinator.SubmitSyncCandidates(stale, [10]);
        coordinator.SubmitSyncCandidates(current, [20]);
        await coordinator.CloseRoundAsync(current, ClientSyncRunStatus.Completed);

        var dispatch = Assert.Single(sink.Dispatches);
        Assert.Equal([20L], dispatch.MessageIds);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(rootDirectory))
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    private async Task<PreparedCache> PrepareAsync(
        IReadOnlyCollection<long>? recoveryMessageIds = null)
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
            UnreadCount: 0);
        Assert.Equal(
            LocalCacheOperationStatus.Ready,
            await cache.ApplyAuthoritativeConversationSnapshotAsync(
                new ConversationListResponse([conversation], Complete: true)));
        foreach (var messageId in recoveryMessageIds ?? [])
        {
            var outcome = await cache.MergeIncomingMessageAsync(
                CreateMessage(messageId, conversation.Id));
            Assert.Equal(messageId, outcome.NotificationCandidateMessageId);
        }

        return new PreparedCache(identity, cache, conversation, new ClientActivityState());
    }

    private static ClientNotificationRoundCoordinator CreateCoordinator(
        PreparedCache prepared,
        IClientNotificationCoordinator notificationCoordinator) =>
        new(
            prepared.Cache,
            notificationCoordinator,
            prepared.ActivityState,
            NullLogger<ClientNotificationRoundCoordinator>.Instance);

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

    private sealed record PreparedCache(
        AccountScopeIdentity Identity,
        AccountScopedLocalCache Cache,
        ConversationDto Conversation,
        ClientActivityState ActivityState);

    private sealed class RecordingNotificationCoordinator : IClientNotificationCoordinator
    {
        private readonly ConcurrentQueue<DispatchRecord> dispatches = new();

        public IReadOnlyCollection<DispatchRecord> Dispatches => dispatches.ToArray();

        public Task<ClientNotificationDispatchOutcome> DispatchAsync(
            IReadOnlyCollection<long> messageIds,
            ClientNotificationDispatchMode mode,
            CancellationToken cancellationToken = default,
            ClientNotificationAttentionGate? attentionGate = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            dispatches.Enqueue(new DispatchRecord(messageIds.ToArray(), mode, attentionGate));
            return Task.FromResult(new ClientNotificationDispatchOutcome(
                ClientNotificationDispatchStatus.Completed,
                messageIds.Count,
                messageIds.Count,
                HandledWithoutPlatformCount: 0));
        }

        public Task ConversationRevokedAsync(
            Guid conversationId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed record DispatchRecord(
        IReadOnlyList<long> MessageIds,
        ClientNotificationDispatchMode Mode,
        ClientNotificationAttentionGate? AttentionGate);
}
