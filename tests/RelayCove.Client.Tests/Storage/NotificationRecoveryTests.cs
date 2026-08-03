using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using RelayCove.Client.Storage;
using RelayCove.Shared.Conversations;
using RelayCove.Shared.Messages;

namespace RelayCove.Client.Tests.Storage;

public sealed class NotificationRecoveryTests : IDisposable
{
    private static readonly Guid UserId =
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid OtherUserId =
        Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Uri ServerBaseUri = new("https://relaycove.example/team/");
    private readonly string rootDirectory = Path.Combine(
        Path.GetTempPath(),
        "RelayCove.Client.NotificationRecoveryTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Recovery_WhenMoreThanBatchLimit_AdvancesAfterHandlingAndFindsLaterLowId()
    {
        var prepared = await PrepareAsync();
        await using var cache = prepared.Cache;
        var initialIds = Enumerable.Range(1000, 201).Select(id => (long)id).ToArray();
        foreach (var messageId in initialIds)
        {
            await cache.MergeIncomingMessageAsync(
                CreateMessage(messageId, prepared.Conversation.Id));
        }

        var first = await cache.ReadNotificationRecoveryBatchAsync(200);
        Assert.Equal(LocalCacheOperationStatus.Ready, first.Status);
        Assert.True(first.HasMore);
        Assert.Equal(initialIds.Take(200), first.MessageIds);
        Assert.Equal(
            LocalCacheOperationStatus.Ready,
            await cache.MarkNotificationCandidatesHandledAsync(first.MessageIds));

        var second = await cache.ReadNotificationRecoveryBatchAsync(200);
        Assert.False(second.HasMore);
        Assert.Equal([1200L], second.MessageIds);
        Assert.Equal(
            LocalCacheOperationStatus.Ready,
            await cache.MarkNotificationCandidatesHandledAsync(second.MessageIds));

        var lateLowId = await cache.MergeIncomingMessageAsync(
            CreateMessage(1, prepared.Conversation.Id));
        Assert.Equal(1, lateLowId.NotificationCandidateMessageId);
        var rescanned = await cache.ReadNotificationRecoveryBatchAsync(200);
        Assert.Equal([1L], rescanned.MessageIds);
    }

    [Fact]
    public async Task Recovery_AfterAcceptedBeforeMarkCrash_RetriesCandidateOnRestart()
    {
        var identity = CreateIdentity();
        var conversation = CreateConversation();
        await using (var cache = await AccountScopedLocalCache.CreateAsync(
                         identity,
                         NullLogger<AccountScopedLocalCache>.Instance))
        {
            await cache.AdoptNotificationStateAsync();
            await ApplySnapshotAsync(cache, conversation);
            await cache.MergeIncomingMessageAsync(CreateMessage(1, conversation.Id));
            var claimed = await cache.EvaluateNotificationCandidatesAsync(
                [1],
                foregroundConversationId: null,
                suppressAll: false);
            Assert.Single(claimed.Candidates);
            // This models the accepted-platform-before-local-mark crash window.
        }

        await using var restarted = await AccountScopedLocalCache.CreateAsync(
            identity,
            NullLogger<AccountScopedLocalCache>.Instance);
        Assert.False(await restarted.AdoptNotificationStateAsync());
        await ApplySnapshotAsync(restarted, conversation);

        var recovery = await restarted.ReadNotificationRecoveryBatchAsync(200);

        Assert.Equal(LocalCacheOperationStatus.Ready, recovery.Status);
        Assert.Equal([1L], recovery.MessageIds);
    }

    [Fact]
    public async Task MarkSummary_WhenCommitFails_RollsBackEveryCandidate()
    {
        var identity = CreateIdentity();
        var faultInjector = new NotificationHandledCommitFaultInjector();
        await using var cache = await AccountScopedLocalCache.CreateAsync(
            identity,
            NullLogger<AccountScopedLocalCache>.Instance,
            faultInjector);
        await cache.AdoptNotificationStateAsync();
        var conversation = CreateConversation();
        await ApplySnapshotAsync(cache, conversation);
        foreach (var messageId in Enumerable.Range(1, 3).Select(id => (long)id))
        {
            await cache.MergeIncomingMessageAsync(CreateMessage(messageId, conversation.Id));
        }

        var status = await cache.MarkNotificationCandidatesHandledAsync([1, 2, 3]);

        Assert.Equal(LocalCacheOperationStatus.FatalScope, status);
        Assert.Equal(0, Scalar(identity, "SELECT COUNT(*) FROM LocalMessages WHERE IsNotificationHandled = 1;"));
    }

    [Fact]
    public async Task EvaluateCandidates_WhenOnePayloadIsMalformed_IsolatesItAndReturnsValidPeer()
    {
        var prepared = await PrepareAsync();
        await using var cache = prepared.Cache;
        await cache.MergeIncomingMessageAsync(CreateMessage(1, prepared.Conversation.Id));
        await cache.MergeIncomingMessageAsync(CreateMessage(2, prepared.Conversation.Id));
        Execute(
            prepared.Identity,
            "UPDATE LocalMessages SET CreatedAt = 'invalid-date' WHERE ServerMessageId = 1;");

        var outcome = await cache.EvaluateNotificationCandidatesAsync(
            [1, 2],
            foregroundConversationId: null,
            suppressAll: false);

        Assert.Equal(LocalCacheOperationStatus.Ready, outcome.Status);
        Assert.Equal(1, outcome.HandledWithoutPlatformCount);
        Assert.Equal(2, Assert.Single(outcome.Candidates).MessageId);
        Assert.Equal(1, Scalar(
            prepared.Identity,
            "SELECT IsNotificationHandled FROM LocalMessages WHERE ServerMessageId = 1;"));
    }

    [Fact]
    public async Task AuthoritativeSnapshot_AfterRestart_ReemitsPersistedRevocationForPlatformClear()
    {
        var identity = CreateIdentity();
        var conversation = CreateConversation();
        await using (var cache = await AccountScopedLocalCache.CreateAsync(
                         identity,
                         NullLogger<AccountScopedLocalCache>.Instance))
        {
            await ApplySnapshotAsync(cache, conversation);
            Assert.Equal(
                LocalCacheOperationStatus.RevokedConversation,
                await cache.RevokeConversationAccessAsync(conversation.Id));
        }

        AccountScopedLocalCache.ResetProcessStateForTest(identity);
        await using var restarted = await AccountScopedLocalCache.CreateAsync(
            identity,
            NullLogger<AccountScopedLocalCache>.Instance);

        var outcome = await restarted
            .ApplyAuthoritativeConversationSnapshotWithRevocationsAsync(
                new ConversationListResponse([], Complete: true));

        Assert.Equal(LocalCacheOperationStatus.Ready, outcome.Status);
        Assert.Equal([conversation.Id], outcome.RevokedConversationIds);
    }

    [Fact]
    public async Task AuthoritativeSnapshot_WhenPersistenceFails_StillReturnsDeniedIdForPlatformClear()
    {
        var identity = CreateIdentity();
        var conversation = CreateConversation();
        await using var cache = await AccountScopedLocalCache.CreateAsync(
            identity,
            NullLogger<AccountScopedLocalCache>.Instance,
            new SnapshotCommitFaultInjector());
        Assert.Equal(
            LocalCacheOperationStatus.Ready,
            await cache.RegisterAuthoritativeConversationAsync(conversation));

        var outcome = await cache.ApplyAuthoritativeConversationSnapshotWithRevocationsAsync(
            new ConversationListResponse([], Complete: true));

        Assert.Equal(LocalCacheOperationStatus.FatalScope, outcome.Status);
        Assert.Equal([conversation.Id], outcome.RevokedConversationIds);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(rootDirectory))
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    private async Task<PreparedCache> PrepareAsync()
    {
        var identity = CreateIdentity();
        var cache = await AccountScopedLocalCache.CreateAsync(
            identity,
            NullLogger<AccountScopedLocalCache>.Instance);
        await cache.AdoptNotificationStateAsync();
        var conversation = CreateConversation();
        await ApplySnapshotAsync(cache, conversation);
        return new PreparedCache(identity, cache, conversation);
    }

    private AccountScopeIdentity CreateIdentity() =>
        AccountScopeIdentity.Create(ServerBaseUri, UserId, rootDirectory);

    private static Task<LocalCacheOperationStatus> ApplySnapshotAsync(
        AccountScopedLocalCache cache,
        ConversationDto conversation) =>
        cache.ApplyAuthoritativeConversationSnapshotAsync(
            new ConversationListResponse([conversation], Complete: true));

    private static ConversationDto CreateConversation() => new(
        Guid.NewGuid(),
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

    private static int Scalar(AccountScopeIdentity identity, string sql)
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
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static void Execute(AccountScopeIdentity identity, string sql)
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
        command.ExecuteNonQuery();
    }

    private sealed record PreparedCache(
        AccountScopeIdentity Identity,
        AccountScopedLocalCache Cache,
        ConversationDto Conversation);

    private sealed class NotificationHandledCommitFaultInjector : ILocalCacheFaultInjector
    {
        public void BeforeRevocationTombstone(Guid conversationId)
        {
        }

        public void BeforeNotificationHandledCommit() =>
            throw new InvalidOperationException("Injected notification handled commit failure.");
    }

    private sealed class SnapshotCommitFaultInjector : ILocalCacheFaultInjector
    {
        public void BeforeRevocationTombstone(Guid conversationId)
        {
        }

        public void BeforeAuthoritativeSnapshotCommit() =>
            throw new InvalidOperationException("Injected snapshot commit failure.");
    }
}
