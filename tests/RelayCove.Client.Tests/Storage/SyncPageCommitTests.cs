using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using RelayCove.Client.Storage;
using RelayCove.Shared.Conversations;
using RelayCove.Shared.Messages;

namespace RelayCove.Client.Tests.Storage;

public sealed class SyncPageCommitTests : IDisposable
{
    private static readonly Guid UserId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private readonly string rootDirectory = Path.Combine(
        Path.GetTempPath(),
        "RelayCove.Client.SyncPageTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ConversationSnapshot_WhenComplete_RevokesMissingAndAllowsAuthoritativeRejoin()
    {
        var identity = CreateIdentity(UserId);
        await using var cache = await CreateCacheAsync(identity);
        var first = CreateConversation("First");
        var second = CreateConversation("Second");
        var oldSecondMessage = CreateMessage(1, second.Id);

        Assert.Equal(
            LocalCacheOperationStatus.Ready,
            await ApplySnapshotAsync(cache, first, second));
        Assert.Equal(
            IncomingMessageMergeResult.Inserted,
            (await cache.MergeIncomingMessageAsync(oldSecondMessage)).Result);

        Assert.Equal(
            LocalCacheOperationStatus.Ready,
            await ApplySnapshotAsync(cache, first with { Name = "First updated" }));
        Assert.Equal(
            LocalCacheOperationStatus.RevokedConversation,
            (await cache.ReadMessagesAsync(second.Id)).Status);
        Assert.Equal(0, Scalar(identity, "SELECT COUNT(*) FROM LocalMessages;"));
        Assert.Equal(1, Scalar(identity, "SELECT COUNT(*) FROM RevokedConversations;"));

        Assert.Equal(
            LocalCacheOperationStatus.Ready,
            await ApplySnapshotAsync(cache, first, second with { Name = "Second rejoined" }));
        Assert.Equal(LocalCacheOperationStatus.Ready, (await cache.ReadMessagesAsync(second.Id)).Status);
        Assert.Empty((await cache.ReadMessagesAsync(second.Id)).Messages);
        Assert.Equal(0, Scalar(identity, "SELECT COUNT(*) FROM RevokedConversations;"));
        Assert.Equal("Second rejoined", TextScalar(
            identity,
            $"SELECT Name FROM LocalConversations WHERE Id = '{second.Id:D}';"));
    }

    [Fact]
    public async Task ConversationSnapshot_WhenIncompleteOrDuplicated_RejectsWithoutChangingAuthorization()
    {
        var identity = CreateIdentity(UserId);
        await using var cache = await CreateCacheAsync(identity);
        var conversation = CreateConversation("Stable");
        await ApplySnapshotAsync(cache, conversation);

        var incomplete = await cache.ApplyAuthoritativeConversationSnapshotAsync(
            new ConversationListResponse(Array.Empty<ConversationDto>(), Complete: false));
        var duplicated = await cache.ApplyAuthoritativeConversationSnapshotAsync(
            new ConversationListResponse([conversation, conversation], Complete: true));

        Assert.Equal(LocalCacheOperationStatus.ProtocolError, incomplete);
        Assert.Equal(LocalCacheOperationStatus.ProtocolError, duplicated);
        Assert.Equal(LocalCacheOperationStatus.Ready, (await cache.ReadMessagesAsync(conversation.Id)).Status);
        Assert.Equal(1, Scalar(identity, "SELECT COUNT(*) FROM LocalConversations;"));
    }

    [Fact]
    public async Task ConversationSnapshot_WhenCommitFails_LeavesIntentAndRestartReplaysRevocation()
    {
        var identity = CreateIdentity(UserId);
        var first = CreateConversation("First");
        var second = CreateConversation("Second");
        await using (var seed = await CreateCacheAsync(identity))
        {
            await ApplySnapshotAsync(seed, first, second);
        }

        await using (var failing = await AccountScopedLocalCache.CreateAsync(
            identity,
            NullLogger<AccountScopedLocalCache>.Instance,
            new SnapshotThrowingFaultInjector()))
        {
            Assert.Equal(
                LocalCacheOperationStatus.FatalScope,
                await ApplySnapshotAsync(failing, first));
            Assert.True(failing.IsFatal);
            Assert.Equal(
                LocalCacheOperationStatus.FatalScope,
                (await failing.ReadMessagesAsync(first.Id)).Status);
        }

        Assert.Equal(1, Scalar(
            identity,
            "SELECT COUNT(*) FROM LocalAppState WHERE Key LIKE 'RevocationIntent/%';"));
        AccountScopedLocalCache.ResetProcessStateForTest(identity);

        await using var restarted = await CreateCacheAsync(identity);
        Assert.Equal(LocalCacheOperationStatus.Ready, await ApplySnapshotAsync(restarted, first));
        Assert.False(restarted.IsFatal);
        Assert.Equal(1, Scalar(identity, "SELECT COUNT(*) FROM LocalConversations;"));
        Assert.Equal(1, Scalar(identity, "SELECT COUNT(*) FROM RevokedConversations;"));
        Assert.Equal(0, Scalar(
            identity,
            "SELECT COUNT(*) FROM LocalAppState WHERE Key LIKE 'RevocationIntent/%';"));
    }

    [Fact]
    public async Task ConversationSnapshot_WhileCommitIsPending_DeniesMissingConversationAcrossStores()
    {
        var identity = CreateIdentity(UserId);
        var blocker = new BlockingSnapshotFaultInjector();
        await using var firstCache = await AccountScopedLocalCache.CreateAsync(
            identity,
            NullLogger<AccountScopedLocalCache>.Instance,
            blocker);
        await using var secondCache = await CreateCacheAsync(identity);
        var first = CreateConversation("First");
        var second = CreateConversation("Second");
        await ApplySnapshotAsync(firstCache, first, second);
        await ApplySnapshotAsync(secondCache, first, second);
        blocker.Arm();

        var snapshotTask = ApplySnapshotAsync(firstCache, first);
        Assert.True(blocker.Entered.Wait(TimeSpan.FromSeconds(5)));

        Assert.Equal(
            LocalCacheOperationStatus.RevokedConversation,
            (await secondCache.ReadMessagesAsync(second.Id)).Status);

        blocker.Release.Set();
        Assert.Equal(LocalCacheOperationStatus.Ready, await snapshotTask);
        Assert.Equal(1, Scalar(identity, "SELECT COUNT(*) FROM RevokedConversations;"));
    }

    [Fact]
    public async Task SyncPage_WhenSnapshotHasNotCommitted_RequiresAuthoritativeSnapshot()
    {
        var identity = CreateIdentity(UserId);
        await using var cache = await CreateCacheAsync(identity);
        var conversation = CreateConversation("Conversation");
        var response = CreatePage([CreateMessage(1, conversation.Id)], next: 1, upper: 1, hasMore: false);

        var cursor = await cache.ReadLastSyncCursorAsync();
        var outcome = await cache.ApplySyncPageAsync(response, 0, null);

        Assert.Equal(LocalCacheOperationStatus.AuthoritativeSnapshotRequired, cursor.Status);
        Assert.Null(cursor.Cursor);
        Assert.Equal(LocalCacheOperationStatus.AuthoritativeSnapshotRequired, outcome.Status);
        Assert.Equal(0, Scalar(identity, "SELECT COUNT(*) FROM LocalMessages;"));
    }

    [Fact]
    public async Task SyncPage_WhenPagesAndPermissionHoleCommit_AdvancesCursorAndSurvivesRestart()
    {
        var identity = CreateIdentity(UserId);
        var conversation = CreateConversation("Conversation");
        await using (var cache = await CreateCacheAsync(identity))
        {
            await ApplySnapshotAsync(cache, conversation);
            Assert.Equal(0, (await cache.ReadLastSyncCursorAsync()).Cursor);

            var firstPage = CreatePage(
                [CreateMessage(1, conversation.Id), CreateMessage(3, conversation.Id)],
                next: 3,
                upper: 5,
                hasMore: true);
            var firstOutcome = await cache.ApplySyncPageAsync(firstPage, 0, null);
            var holePage = CreatePage([], next: 5, upper: 5, hasMore: false);
            var holeOutcome = await cache.ApplySyncPageAsync(holePage, 3, 5);

            Assert.Equal(LocalCacheOperationStatus.Ready, firstOutcome.Status);
            Assert.Equal(
                [IncomingMessageMergeResult.Inserted, IncomingMessageMergeResult.Inserted],
                firstOutcome.MergeResults);
            Assert.Equal(LocalCacheOperationStatus.Ready, holeOutcome.Status);
            Assert.Empty(holeOutcome.MergeResults);
            Assert.Equal(5, holeOutcome.CommittedCursor);
        }

        await using var restarted = await CreateCacheAsync(identity);
        Assert.Equal(
            LocalCacheOperationStatus.AuthoritativeSnapshotRequired,
            (await restarted.ReadLastSyncCursorAsync()).Status);
        await ApplySnapshotAsync(restarted, conversation);
        Assert.Equal(5, (await restarted.ReadLastSyncCursorAsync()).Cursor);
        Assert.Equal(2, (await restarted.ReadMessagesAsync(conversation.Id)).Messages.Count);
    }

    [Fact]
    public async Task SyncPage_WhenRealtimeAndPendingArriveFirst_DuplicatesAndPromotesInOneCommit()
    {
        var identity = CreateIdentity(UserId);
        await using var cache = await CreateCacheAsync(identity);
        var conversation = CreateConversation("Conversation");
        var realtime = CreateMessage(1, conversation.Id);
        var pendingEcho = CreateMessage(2, conversation.Id);
        await ApplySnapshotAsync(cache, conversation);
        await cache.MergeIncomingMessageAsync(realtime);
        await cache.AddPendingMessageAsync(new PendingMessage(
            pendingEcho.ClientMessageId,
            pendingEcho.ConversationId,
            pendingEcho.SenderId,
            "local display",
            pendingEcho.Type,
            pendingEcho.Content,
            pendingEcho.ReplyToMessageId,
            pendingEcho.MentionUserIds,
            pendingEcho.CreatedAt.AddMinutes(-1)));

        var outcome = await cache.ApplySyncPageAsync(
            CreatePage([realtime, pendingEcho], next: 2, upper: 2, hasMore: false),
            0,
            null);

        Assert.Equal(LocalCacheOperationStatus.Ready, outcome.Status);
        Assert.Equal(
            [IncomingMessageMergeResult.Duplicate, IncomingMessageMergeResult.PendingPromoted],
            outcome.MergeResults);
        Assert.Equal(2, Scalar(identity, "SELECT COUNT(*) FROM LocalMessages;"));
        Assert.Equal(2, (await cache.ReadLastSyncCursorAsync()).Cursor);
    }

    [Fact]
    public async Task SyncPage_WhenLaterMessageConflicts_RollsBackEarlierInsertAndCursor()
    {
        var identity = CreateIdentity(UserId);
        await using var cache = await CreateCacheAsync(identity);
        var conversation = CreateConversation("Conversation");
        var first = CreateMessage(1, conversation.Id);
        var existingSecond = CreateMessage(2, conversation.Id);
        await ApplySnapshotAsync(cache, conversation);
        await cache.MergeIncomingMessageAsync(existingSecond);

        var outcome = await cache.ApplySyncPageAsync(
            CreatePage(
                [first, existingSecond with { Content = "conflicting content" }],
                next: 2,
                upper: 2,
                hasMore: false),
            0,
            null);

        Assert.Equal(LocalCacheOperationStatus.Conflict, outcome.Status);
        Assert.Empty(outcome.MergeResults);
        Assert.Equal(0, (await cache.ReadLastSyncCursorAsync()).Cursor);
        var messages = (await cache.ReadMessagesAsync(conversation.Id)).Messages;
        Assert.Equal(existingSecond.Id, Assert.Single(messages).Id);
    }

    [Fact]
    public async Task SyncPage_WhenLaterConversationIsUnknown_RollsBackWholePage()
    {
        var identity = CreateIdentity(UserId);
        await using var cache = await CreateCacheAsync(identity);
        var known = CreateConversation("Known");
        await ApplySnapshotAsync(cache, known);

        var outcome = await cache.ApplySyncPageAsync(
            CreatePage(
                [CreateMessage(1, known.Id), CreateMessage(2, Guid.NewGuid())],
                next: 2,
                upper: 2,
                hasMore: false),
            0,
            null);

        Assert.Equal(LocalCacheOperationStatus.UnknownConversation, outcome.Status);
        Assert.Equal(0, Scalar(identity, "SELECT COUNT(*) FROM LocalMessages;"));
        Assert.Equal(0, (await cache.ReadLastSyncCursorAsync()).Cursor);
    }

    [Fact]
    public async Task SyncPage_WhenConversationWasRevoked_RollsBackWithoutAdvancingCursor()
    {
        var identity = CreateIdentity(UserId);
        await using var cache = await CreateCacheAsync(identity);
        var conversation = CreateConversation("Revoked");
        await ApplySnapshotAsync(cache, conversation);
        await cache.RevokeConversationAccessAsync(conversation.Id);

        var outcome = await cache.ApplySyncPageAsync(
            CreatePage([CreateMessage(1, conversation.Id)], next: 1, upper: 1, hasMore: false),
            0,
            null);

        Assert.Equal(LocalCacheOperationStatus.RevokedConversation, outcome.Status);
        Assert.Equal(0, Scalar(identity, "SELECT COUNT(*) FROM LocalMessages;"));
        Assert.Equal("0", TextScalar(
            identity,
            "SELECT COALESCE((SELECT Value FROM LocalAppState WHERE Key = 'LastSyncCursor'), '0');"));
    }

    [Fact]
    public async Task SyncPage_WhenExpectedCursorIsStale_RollsBackWithoutAddingMessage()
    {
        var identity = CreateIdentity(UserId);
        await using var cache = await CreateCacheAsync(identity);
        var conversation = CreateConversation("Conversation");
        await ApplySnapshotAsync(cache, conversation);
        await cache.ApplySyncPageAsync(
            CreatePage([CreateMessage(1, conversation.Id)], next: 1, upper: 1, hasMore: false),
            0,
            null);

        var stale = await cache.ApplySyncPageAsync(
            CreatePage([CreateMessage(2, conversation.Id)], next: 2, upper: 2, hasMore: false),
            0,
            null);

        Assert.Equal(LocalCacheOperationStatus.StaleCursor, stale.Status);
        Assert.Equal(1, (await cache.ReadLastSyncCursorAsync()).Cursor);
        Assert.Equal(1, Scalar(identity, "SELECT COUNT(*) FROM LocalMessages;"));
    }

    [Fact]
    public async Task SyncPage_WhenTwoPagesRaceWithSameCursor_CommitsExactlyOne()
    {
        var identity = CreateIdentity(UserId);
        await using var cache = await CreateCacheAsync(identity);
        var conversation = CreateConversation("Conversation");
        await ApplySnapshotAsync(cache, conversation);

        var outcomes = await Task.WhenAll(
            cache.ApplySyncPageAsync(
                CreatePage([CreateMessage(1, conversation.Id)], next: 1, upper: 1, hasMore: false),
                0,
                null),
            cache.ApplySyncPageAsync(
                CreatePage([CreateMessage(2, conversation.Id)], next: 2, upper: 2, hasMore: false),
                0,
                null));

        Assert.Single(outcomes, outcome => outcome.Status == LocalCacheOperationStatus.Ready);
        Assert.Single(outcomes, outcome => outcome.Status == LocalCacheOperationStatus.StaleCursor);
        Assert.Equal(1, Scalar(identity, "SELECT COUNT(*) FROM LocalMessages;"));
        Assert.Contains((await cache.ReadLastSyncCursorAsync()).Cursor, new long?[] { 1, 2 });
    }

    [Fact]
    public async Task SyncPage_WhenResponseInvariantFails_RejectsBeforeTouchingDatabase()
    {
        var identity = CreateIdentity(UserId);
        await using var cache = await CreateCacheAsync(identity);
        var conversation = CreateConversation("Conversation");
        await ApplySnapshotAsync(cache, conversation);
        var first = CreateMessage(1, conversation.Id);
        var second = CreateMessage(2, conversation.Id);
        var invalidPages = new[]
        {
            CreatePage([second, first], next: 2, upper: 2, hasMore: false),
            CreatePage([first], next: 1, upper: 2, hasMore: false),
            CreatePage([], next: 0, upper: 2, hasMore: true),
            CreatePage([second], next: 1, upper: 1, hasMore: false),
        };

        foreach (var page in invalidPages)
        {
            var outcome = await cache.ApplySyncPageAsync(page, 0, null);
            Assert.Equal(LocalCacheOperationStatus.ProtocolError, outcome.Status);
        }

        var upperMismatch = await cache.ApplySyncPageAsync(
            CreatePage([first], next: 1, upper: 6, hasMore: true),
            0,
            expectedSnapshotUpperBound: 5);
        Assert.Equal(LocalCacheOperationStatus.ProtocolError, upperMismatch.Status);
        Assert.Equal(0, (await cache.ReadLastSyncCursorAsync()).Cursor);
        Assert.Equal(0, Scalar(identity, "SELECT COUNT(*) FROM LocalMessages;"));
    }

    [Fact]
    public async Task SyncState_WhenAccountsDiffer_UsesIndependentCursors()
    {
        var firstIdentity = CreateIdentity(UserId);
        var secondIdentity = CreateIdentity(Guid.Parse("11111111-2222-3333-4444-555555555555"));
        await using var firstCache = await CreateCacheAsync(firstIdentity);
        await using var secondCache = await CreateCacheAsync(secondIdentity);
        var conversation = CreateConversation("Shared ID");
        await ApplySnapshotAsync(firstCache, conversation);
        await ApplySnapshotAsync(secondCache, conversation);

        await firstCache.ApplySyncPageAsync(
            CreatePage([CreateMessage(1, conversation.Id)], next: 1, upper: 1, hasMore: false),
            0,
            null);

        Assert.Equal(1, (await firstCache.ReadLastSyncCursorAsync()).Cursor);
        Assert.Equal(0, (await secondCache.ReadLastSyncCursorAsync()).Cursor);
        Assert.NotEqual(firstIdentity.DatabasePath, secondIdentity.DatabasePath);
    }

    [Fact]
    public void SyncOutcomes_WhenFormatted_RedactCursorAndMergeDetails()
    {
        var cursor = new LocalSyncCursorReadOutcome(LocalCacheOperationStatus.Ready, 123456789);
        var page = new SyncPageCommitOutcome(
            LocalCacheOperationStatus.Ready,
            [IncomingMessageMergeResult.Inserted],
            123456789);

        Assert.DoesNotContain("123456789", cursor.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("123456789", page.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Inserted", page.ToString(), StringComparison.Ordinal);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(rootDirectory))
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    private AccountScopeIdentity CreateIdentity(Guid userId) => AccountScopeIdentity.Create(
        new Uri("https://relaycove.example/team/"),
        userId,
        rootDirectory);

    private static Task<AccountScopedLocalCache> CreateCacheAsync(AccountScopeIdentity identity) =>
        AccountScopedLocalCache.CreateAsync(
            identity,
            NullLogger<AccountScopedLocalCache>.Instance);

    private static async Task<LocalCacheOperationStatus> ApplySnapshotAsync(
        AccountScopedLocalCache cache,
        params ConversationDto[] conversations) =>
        await cache.ApplyAuthoritativeConversationSnapshotAsync(
            new ConversationListResponse(conversations, Complete: true));

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

    private static SyncResponse CreatePage(
        IReadOnlyList<MessageDto> messages,
        long next,
        long upper,
        bool hasMore) => new(messages, next, upper, hasMore);

    private static long Scalar(AccountScopeIdentity identity, string sql)
    {
        using var connection = OpenConnection(identity);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static string TextScalar(AccountScopeIdentity identity, string sql)
    {
        using var connection = OpenConnection(identity);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar())!;
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

    private sealed class SnapshotThrowingFaultInjector : ILocalCacheFaultInjector
    {
        public void BeforeRevocationTombstone(Guid conversationId)
        {
        }

        public void BeforeAuthoritativeSnapshotCommit() =>
            throw new IOException("Injected snapshot commit failure.");
    }

    private sealed class BlockingSnapshotFaultInjector : ILocalCacheFaultInjector
    {
        private int armed;

        public ManualResetEventSlim Entered { get; } = new(initialState: false);

        public ManualResetEventSlim Release { get; } = new(initialState: false);

        public void Arm() => Volatile.Write(ref armed, 1);

        public void BeforeRevocationTombstone(Guid conversationId)
        {
        }

        public void BeforeAuthoritativeSnapshotCommit()
        {
            if (Volatile.Read(ref armed) == 0)
            {
                return;
            }

            Entered.Set();
            if (!Release.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("The snapshot test gate timed out.");
            }
        }
    }
}
