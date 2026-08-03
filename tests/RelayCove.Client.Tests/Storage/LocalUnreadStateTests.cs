using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using RelayCove.Client.Storage;
using RelayCove.Shared.Conversations;
using RelayCove.Shared.Messages;

namespace RelayCove.Client.Tests.Storage;

public sealed class LocalUnreadStateTests : IDisposable
{
    private static readonly Guid UserId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly Guid OtherUserId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private readonly string rootDirectory = Path.Combine(
        Path.GetTempPath(),
        "RelayCove.Client.UnreadTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task MergeRealtime_WhenOtherMessageAdvancesConversation_IncrementsUnreadAndReturnsCandidate()
    {
        var identity = CreateIdentity();
        await using var cache = await CreateCacheAsync(identity);
        var conversation = CreateConversation(lastMessageId: 100, lastReadMessageId: 90, unreadCount: 2);
        await ApplySnapshotAsync(cache, conversation);

        var outcome = await cache.MergeIncomingMessageAsync(CreateMessage(101, conversation.Id));

        Assert.Equal(IncomingMessageMergeResult.Inserted, outcome.Result);
        Assert.Equal(101, outcome.NotificationCandidateMessageId);
        Assert.Equal(new MessageAttention(IsRead: false, IsNotificationHandled: false),
            ReadMessageAttention(identity, 101));
        Assert.Equal(
            new ConversationAttention(101, 90, PendingReadThroughMessageId: null, 3),
            ReadConversationAttention(identity, conversation.Id));
    }

    [Fact]
    public async Task MergeRealtime_WhenMessageIsOwnOrBelowReadBoundary_MarksHandledWithoutUnread()
    {
        var identity = CreateIdentity();
        await using var cache = await CreateCacheAsync(identity);
        var ownConversation = CreateConversation(lastMessageId: 100, lastReadMessageId: 90, unreadCount: 2);
        var oldConversation = CreateConversation(lastMessageId: 100, lastReadMessageId: 100, unreadCount: 0);
        await ApplySnapshotAsync(cache, ownConversation, oldConversation);

        var ownOutcome = await cache.MergeIncomingMessageAsync(
            CreateMessage(101, ownConversation.Id, UserId));
        var oldOutcome = await cache.MergeIncomingMessageAsync(
            CreateMessage(99, oldConversation.Id));

        Assert.Null(ownOutcome.NotificationCandidateMessageId);
        Assert.Equal(new MessageAttention(IsRead: true, IsNotificationHandled: true),
            ReadMessageAttention(identity, 101));
        Assert.Equal(
            new ConversationAttention(101, 90, PendingReadThroughMessageId: null, 2),
            ReadConversationAttention(identity, ownConversation.Id));
        Assert.Null(oldOutcome.NotificationCandidateMessageId);
        Assert.Equal(new MessageAttention(IsRead: true, IsNotificationHandled: true),
            ReadMessageAttention(identity, 99));
        Assert.Equal(
            new ConversationAttention(100, 100, PendingReadThroughMessageId: null, 0),
            ReadConversationAttention(identity, oldConversation.Id));
    }

    [Fact]
    public async Task MergeLive_WhenConversationIsForeground_AdvancesReadThroughAndSuppressesCandidate()
    {
        var identity = CreateIdentity();
        await using var cache = await CreateCacheAsync(identity);
        var conversation = CreateConversation(lastMessageId: 100, lastReadMessageId: 90, unreadCount: 2);
        await ApplySnapshotAsync(cache, conversation);
        var context = new LocalMessageIngestionContext(
            IncomingMessageSource.Realtime,
            conversation.Id);
        Assert.DoesNotContain(
            conversation.Id.ToString(),
            context.ToString(),
            StringComparison.OrdinalIgnoreCase);

        var outcome = await cache.MergeIncomingMessageAsync(
            CreateMessage(101, conversation.Id),
            context);

        Assert.Null(outcome.NotificationCandidateMessageId);
        Assert.Equal(new MessageAttention(IsRead: true, IsNotificationHandled: true),
            ReadMessageAttention(identity, 101));
        Assert.Equal(
            new ConversationAttention(101, 90, 101, 2),
            ReadConversationAttention(identity, conversation.Id));
    }

    [Fact]
    public async Task MergeForegroundRealtime_WhenMessageIsAheadOfSyncCursor_DoesNotConsumeUnseenGap()
    {
        var identity = CreateIdentity();
        await using var cache = await CreateCacheAsync(identity);
        var conversation = CreateConversation(lastMessageId: 5000, lastReadMessageId: 90, unreadCount: 100);
        var syncedMessage = CreateMessage(100, conversation.Id);
        await ApplySnapshotAsync(cache, conversation);
        var page = new SyncResponse([syncedMessage], 100, 100, HasMore: false);
        Assert.Equal(
            LocalCacheOperationStatus.Ready,
            (await cache.ApplySyncPageAsync(page, 0, null)).Status);

        var realtime = await cache.MergeIncomingMessageAsync(
            CreateMessage(5000, conversation.Id),
            new LocalMessageIngestionContext(IncomingMessageSource.Realtime, conversation.Id));

        Assert.Null(realtime.NotificationCandidateMessageId);
        Assert.Equal(new MessageAttention(IsRead: true, IsNotificationHandled: true),
            ReadMessageAttention(identity, 5000));
        Assert.Equal(
            new ConversationAttention(5000, 100, 5000, 98),
            ReadConversationAttention(identity, conversation.Id));
    }

    [Fact]
    public async Task MergeForegroundRealtime_WhenCursorBelongsToAnotherConversation_UsesLocalMessageTarget()
    {
        var identity = CreateIdentity();
        await using var cache = await CreateCacheAsync(identity);
        var foreground = CreateConversation(lastMessageId: 50, lastReadMessageId: 0, unreadCount: 1);
        var cursorOwner = CreateConversation(lastMessageId: 60, lastReadMessageId: 60, unreadCount: 0);
        await ApplySnapshotAsync(cache, foreground, cursorOwner);
        Assert.Equal(
            LocalCacheOperationStatus.Ready,
            (await cache.ApplySyncPageAsync(
                new SyncResponse(
                    [CreateMessage(50, foreground.Id), CreateMessage(60, cursorOwner.Id)],
                    60,
                    60,
                    HasMore: false),
                0,
                null)).Status);

        await cache.MergeIncomingMessageAsync(
            CreateMessage(63, foreground.Id),
            new LocalMessageIngestionContext(IncomingMessageSource.Realtime, foreground.Id));
        var batch = await cache.ReadPendingReadThroughBatchAsync(null, 100);

        Assert.Equal(
            new ConversationAttention(63, 50, PendingReadThroughMessageId: 63, 0),
            ReadConversationAttention(identity, foreground.Id));
        var target = Assert.Single(batch.Targets);
        Assert.Equal(foreground.Id, target.ConversationId);
        Assert.Equal(63, target.RawPendingMessageId);
        Assert.Equal(50, target.SafeMessageId);
    }

    [Fact]
    public async Task MergeForegroundRealtime_WhenSnapshotAdvancedReadBoundary_DoesNotConsumeExcludedRows()
    {
        var identity = CreateIdentity();
        await using var cache = await CreateCacheAsync(identity);
        var initial = CreateConversation(lastMessageId: 60, lastReadMessageId: 50, unreadCount: 10);
        await ApplySnapshotAsync(cache, initial);
        var page = new SyncResponse(
            Enumerable.Range(51, 10)
                .Select(id => CreateMessage(id, initial.Id))
                .ToArray(),
            NextCursor: 60,
            SnapshotUpperBound: 60,
            HasMore: false);
        Assert.Equal(
            LocalCacheOperationStatus.Ready,
            (await cache.ApplySyncPageAsync(page, 0, null)).Status);
        await ApplySnapshotAsync(cache, initial with
        {
            LastMessageId = 62,
            LastReadMessageId = 60,
            UnreadCount = 2,
        });
        Assert.Equal(
            10,
            Scalar(
                identity,
                "SELECT COUNT(*) FROM LocalMessages WHERE ServerMessageId <= 60 AND IsRead = 0;"));

        var realtime = await cache.MergeIncomingMessageAsync(
            CreateMessage(63, initial.Id),
            new LocalMessageIngestionContext(IncomingMessageSource.Realtime, initial.Id));

        Assert.Null(realtime.NotificationCandidateMessageId);
        Assert.Equal(
            0,
            Scalar(
                identity,
                "SELECT COUNT(*) FROM LocalMessages WHERE ServerMessageId <= 60 AND IsRead = 0;"));
        Assert.Equal(
            new ConversationAttention(63, 60, PendingReadThroughMessageId: 63, 2),
            ReadConversationAttention(identity, initial.Id));
    }

    [Fact]
    public async Task MergeDuplicate_WhenConversationBecomesForeground_ConsumesExistingUnreadOnce()
    {
        var identity = CreateIdentity();
        await using var cache = await CreateCacheAsync(identity);
        var conversation = CreateConversation(lastMessageId: 100, lastReadMessageId: 90, unreadCount: 2);
        var message = CreateMessage(101, conversation.Id);
        await ApplySnapshotAsync(cache, conversation);

        var background = await cache.MergeIncomingMessageAsync(message);
        var foreground = await cache.MergeIncomingMessageAsync(
            message,
            new LocalMessageIngestionContext(IncomingMessageSource.Realtime, conversation.Id));
        var repeatedForeground = await cache.MergeIncomingMessageAsync(
            message,
            new LocalMessageIngestionContext(IncomingMessageSource.Realtime, conversation.Id));
        var historyObservation = await cache.MergeIncomingMessageAsync(
            message,
            LocalMessageIngestionContext.Background(IncomingMessageSource.History));
        var sendObservation = await cache.MergeIncomingMessageAsync(
            message,
            LocalMessageIngestionContext.Background(IncomingMessageSource.SendResponse));

        Assert.Equal(101, background.NotificationCandidateMessageId);
        Assert.Equal(IncomingMessageMergeResult.Duplicate, foreground.Result);
        Assert.Null(foreground.NotificationCandidateMessageId);
        Assert.Null(repeatedForeground.NotificationCandidateMessageId);
        Assert.Null(historyObservation.NotificationCandidateMessageId);
        Assert.Null(sendObservation.NotificationCandidateMessageId);
        Assert.Equal(new MessageAttention(IsRead: true, IsNotificationHandled: true),
            ReadMessageAttention(identity, 101));
        Assert.Equal(
            new ConversationAttention(101, 90, 101, 2),
            ReadConversationAttention(identity, conversation.Id));
    }

    [Fact]
    public async Task MergeDuplicate_WhenSenderDisplayNameChanges_RefreshesMutableProjectionWithoutConflict()
    {
        var identity = CreateIdentity();
        await using var cache = await CreateCacheAsync(identity);
        var conversation = CreateConversation(lastMessageId: 100, lastReadMessageId: 90, unreadCount: 2);
        var message = CreateMessage(101, conversation.Id);
        await ApplySnapshotAsync(cache, conversation);
        await cache.MergeIncomingMessageAsync(message);

        var outcome = await cache.MergeIncomingMessageAsync(
            message with { SenderDisplayName = "Renamed Sender" });

        Assert.Equal(IncomingMessageMergeResult.Duplicate, outcome.Result);
        Assert.Null(outcome.NotificationCandidateMessageId);
        Assert.Equal("Renamed Sender", TextScalar(
            identity,
            "SELECT SenderDisplayName FROM LocalMessages WHERE ServerMessageId = 101;"));
        Assert.Equal(3, ReadConversationAttention(identity, conversation.Id).UnreadCount);
    }

    [Fact]
    public async Task ApplySyncPage_WhenRealtimeInsertedFirst_DoesNotRepeatUnreadOrCandidate()
    {
        var identity = CreateIdentity();
        await using var cache = await CreateCacheAsync(identity);
        var conversation = CreateConversation(lastMessageId: 0, lastReadMessageId: 0, unreadCount: 0);
        var message = CreateMessage(1, conversation.Id);
        await ApplySnapshotAsync(cache, conversation);
        var realtime = await cache.MergeIncomingMessageAsync(message);

        var sync = await cache.ApplySyncPageAsync(
            new SyncResponse([message], 1, 1, HasMore: false),
            0,
            null);

        Assert.Equal(1, realtime.NotificationCandidateMessageId);
        Assert.Equal([IncomingMessageMergeResult.Duplicate], sync.MergeResults);
        Assert.Empty(sync.NotificationCandidateMessageIds);
        Assert.Equal(
            new ConversationAttention(1, 0, PendingReadThroughMessageId: null, 1),
            ReadConversationAttention(identity, conversation.Id));
    }

    [Fact]
    public async Task MergeHistoryDuplicate_WhenRealtimeCandidateExists_MonotonicallyHandlesWithoutUnreadReplay()
    {
        var identity = CreateIdentity();
        await using var cache = await CreateCacheAsync(identity);
        var conversation = CreateConversation(lastMessageId: 0, lastReadMessageId: 0, unreadCount: 0);
        var message = CreateMessage(1, conversation.Id);
        await ApplySnapshotAsync(cache, conversation);
        await cache.MergeIncomingMessageAsync(message);

        var history = await cache.MergeIncomingMessageAsync(
            message,
            LocalMessageIngestionContext.Background(IncomingMessageSource.History));

        Assert.Equal(IncomingMessageMergeResult.Duplicate, history.Result);
        Assert.Null(history.NotificationCandidateMessageId);
        Assert.Equal(new MessageAttention(IsRead: true, IsNotificationHandled: true),
            ReadMessageAttention(identity, 1));
        Assert.Equal(0, ReadConversationAttention(identity, conversation.Id).UnreadCount);
    }

    [Fact]
    public async Task MergeHistoryInsert_WhenRealtimeAdvancedLocalPreview_DoesNotConsumeRealtimeUnread()
    {
        var identity = CreateIdentity();
        await using var cache = await CreateCacheAsync(identity);
        var conversation = CreateConversation(
            lastMessageId: 100,
            lastReadMessageId: 100,
            unreadCount: 0);
        await ApplySnapshotAsync(cache, conversation);
        await cache.MergeIncomingMessageAsync(CreateMessage(200, conversation.Id));

        var history = await cache.MergeIncomingMessageAsync(
            CreateMessage(150, conversation.Id),
            LocalMessageIngestionContext.Background(IncomingMessageSource.History));

        Assert.Equal(IncomingMessageMergeResult.Inserted, history.Result);
        Assert.Null(history.NotificationCandidateMessageId);
        Assert.Equal(
            new MessageAttention(IsRead: true, IsNotificationHandled: true),
            ReadMessageAttention(identity, 150));
        Assert.Equal(
            new MessageAttention(IsRead: false, IsNotificationHandled: false),
            ReadMessageAttention(identity, 200));
        Assert.Equal(
            new ConversationAttention(200, 100, PendingReadThroughMessageId: null, 1),
            ReadConversationAttention(identity, conversation.Id));
    }

    [Theory]
    [InlineData(IncomingMessageSource.History, 100)]
    [InlineData(IncomingMessageSource.SendResponse, 101)]
    public async Task MergeObservation_WhenSourceIsNotLive_SuppressesCandidateAndUnread(
        IncomingMessageSource source,
        long expectedLastMessageId)
    {
        var identity = CreateIdentity();
        await using var cache = await CreateCacheAsync(identity);
        var conversation = CreateConversation(lastMessageId: 100, lastReadMessageId: 90, unreadCount: 2);
        await ApplySnapshotAsync(cache, conversation);

        var outcome = await cache.MergeIncomingMessageAsync(
            CreateMessage(101, conversation.Id),
            LocalMessageIngestionContext.Background(source));

        Assert.Null(outcome.NotificationCandidateMessageId);
        Assert.Equal(new MessageAttention(
            IsRead: source == IncomingMessageSource.History,
            IsNotificationHandled: true),
            ReadMessageAttention(identity, 101));
        Assert.Equal(
            new ConversationAttention(expectedLastMessageId, 90, PendingReadThroughMessageId: null, 2),
            ReadConversationAttention(identity, conversation.Id));
    }

    [Fact]
    public async Task MergePendingPromotion_WhenOwnSendIsAcknowledged_DoesNotCreateUnreadOrCandidate()
    {
        var identity = CreateIdentity();
        await using var cache = await CreateCacheAsync(identity);
        var conversation = CreateConversation(lastMessageId: 100, lastReadMessageId: 90, unreadCount: 2);
        var message = CreateMessage(101, conversation.Id, UserId);
        await ApplySnapshotAsync(cache, conversation);
        await cache.AddPendingMessageAsync(new PendingMessage(
            message.ClientMessageId,
            message.ConversationId,
            message.SenderId,
            message.SenderDisplayName,
            message.Type,
            message.Content,
            message.ReplyToMessageId,
            message.MentionUserIds,
            message.CreatedAt.AddSeconds(-1)));

        Assert.Equal(new MessageAttention(IsRead: true, IsNotificationHandled: true),
            ReadPendingMessageAttention(identity, message.ClientMessageId));

        var outcome = await cache.MergeIncomingMessageAsync(
            message,
            LocalMessageIngestionContext.Background(IncomingMessageSource.SendResponse));

        Assert.Equal(IncomingMessageMergeResult.PendingPromoted, outcome.Result);
        Assert.Null(outcome.NotificationCandidateMessageId);
        Assert.Equal(new MessageAttention(IsRead: true, IsNotificationHandled: true),
            ReadMessageAttention(identity, 101));
        Assert.Equal(
            new ConversationAttention(101, 90, PendingReadThroughMessageId: null, 2),
            ReadConversationAttention(identity, conversation.Id));
    }

    [Fact]
    public async Task AddPendingMessage_WhenSenderIsNotCurrentAccount_RejectsWithoutPersisting()
    {
        var identity = CreateIdentity();
        await using var cache = await CreateCacheAsync(identity);
        var conversation = CreateConversation(lastMessageId: 0, lastReadMessageId: 0, unreadCount: 0);
        await ApplySnapshotAsync(cache, conversation);
        var message = CreateMessage(1, conversation.Id);
        var pending = new PendingMessage(
            message.ClientMessageId,
            message.ConversationId,
            message.SenderId,
            message.SenderDisplayName,
            message.Type,
            message.Content,
            message.ReplyToMessageId,
            message.MentionUserIds,
            message.CreatedAt);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            cache.AddPendingMessageAsync(pending));

        Assert.Equal(0, Scalar(identity, "SELECT COUNT(*) FROM LocalMessages;"));
    }

    [Fact]
    public async Task ApplySyncPage_WhenMessagesSpanSnapshotBoundary_OnlyBeyondBoundaryIncrementsUnread()
    {
        var identity = CreateIdentity();
        await using var cache = await CreateCacheAsync(identity);
        var conversation = CreateConversation(lastMessageId: 101, lastReadMessageId: 99, unreadCount: 2);
        await ApplySnapshotAsync(cache, conversation);
        var beyondSnapshot = CreateMessage(102, conversation.Id);
        var page = new SyncResponse(
            [CreateMessage(100, conversation.Id), CreateMessage(101, conversation.Id), beyondSnapshot],
            NextCursor: 102,
            SnapshotUpperBound: 102,
            HasMore: false);

        var outcome = await cache.ApplySyncPageAsync(page, 0, null);

        Assert.Equal(LocalCacheOperationStatus.Ready, outcome.Status);
        Assert.Equal([100L, 101L, 102L], outcome.NotificationCandidateMessageIds);
        Assert.Equal(
            new ConversationAttention(102, 99, PendingReadThroughMessageId: null, 3),
            ReadConversationAttention(identity, conversation.Id));

        var duplicate = await cache.MergeIncomingMessageAsync(beyondSnapshot);
        Assert.Equal(IncomingMessageMergeResult.Duplicate, duplicate.Result);
        Assert.Null(duplicate.NotificationCandidateMessageId);
        Assert.Equal(3, ReadConversationAttention(identity, conversation.Id).UnreadCount);
    }

    [Fact]
    public async Task ApplySyncPage_WhenForegroundConversationHasLargeHistory_CoalescesReadThrough()
    {
        var identity = CreateIdentity();
        await using var cache = await CreateCacheAsync(identity);
        var conversation = CreateConversation(
            lastMessageId: 10_000,
            lastReadMessageId: 0,
            unreadCount: 10_000);
        await ApplySnapshotAsync(cache, conversation);
        SeedUnreadMessages(identity, conversation.Id, 1, 10_000);
        var messages = Enumerable.Range(10_001, 200)
            .Select(id => CreateMessage(id, conversation.Id))
            .ToArray();
        var stopwatch = Stopwatch.StartNew();

        var outcome = await cache.ApplySyncPageAsync(
            new SyncResponse(messages, 10_200, 10_200, HasMore: false),
            0,
            null,
            new LocalMessageIngestionContext(IncomingMessageSource.Sync, conversation.Id));
        stopwatch.Stop();

        Assert.Equal(LocalCacheOperationStatus.Ready, outcome.Status);
        Assert.Empty(outcome.NotificationCandidateMessageIds);
        Assert.Equal(10_200, Scalar(
            identity,
            "SELECT COUNT(*) FROM LocalMessages WHERE IsRead = 1 AND IsNotificationHandled = 1;"));
        Assert.Equal(0, ReadConversationAttention(identity, conversation.Id).UnreadCount);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"Coalesced foreground page took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task ApplySyncPage_WhenLaterMessageConflicts_RollsBackUnreadCandidatesAndCursor()
    {
        var identity = CreateIdentity();
        await using var cache = await CreateCacheAsync(identity);
        var conversation = CreateConversation(lastMessageId: 102, lastReadMessageId: 100, unreadCount: 2);
        var existing = CreateMessage(102, conversation.Id);
        await ApplySnapshotAsync(cache, conversation);
        await cache.MergeIncomingMessageAsync(existing);
        var page = new SyncResponse(
            [CreateMessage(101, conversation.Id), existing with { Content = "conflict" }],
            NextCursor: 102,
            SnapshotUpperBound: 102,
            HasMore: false);

        var outcome = await cache.ApplySyncPageAsync(
            page,
            0,
            null,
            new LocalMessageIngestionContext(IncomingMessageSource.Sync, conversation.Id));

        Assert.Equal(LocalCacheOperationStatus.Conflict, outcome.Status);
        Assert.Empty(outcome.NotificationCandidateMessageIds);
        Assert.Equal(0, (await cache.ReadLastSyncCursorAsync()).Cursor);
        Assert.Equal(1, Scalar(identity, "SELECT COUNT(*) FROM LocalMessages;"));
        Assert.Equal(
            new ConversationAttention(102, 100, PendingReadThroughMessageId: null, 2),
            ReadConversationAttention(identity, conversation.Id));
    }

    [Fact]
    public async Task MergeRealtime_WhenIdsArriveOutOfOrder_CountsEachFirstArrivalAgainstAuthority()
    {
        var identity = CreateIdentity();
        await using var cache = await CreateCacheAsync(identity);
        var conversation = CreateConversation(lastMessageId: 9, lastReadMessageId: 9, unreadCount: 0);
        await ApplySnapshotAsync(cache, conversation);

        await cache.MergeIncomingMessageAsync(CreateMessage(20, conversation.Id));
        await cache.MergeIncomingMessageAsync(CreateMessage(19, conversation.Id));

        Assert.Equal(
            new ConversationAttention(20, 9, PendingReadThroughMessageId: null, 2),
            ReadConversationAttention(identity, conversation.Id));
    }

    [Fact]
    public async Task IngestionContext_WhenInvalid_RejectsBeforeWriting()
    {
        var identity = CreateIdentity();
        await using var cache = await CreateCacheAsync(identity);
        var conversation = CreateConversation(lastMessageId: 0, lastReadMessageId: 0, unreadCount: 0);
        await ApplySnapshotAsync(cache, conversation);
        var message = CreateMessage(1, conversation.Id);

        await Assert.ThrowsAsync<ArgumentException>(() => cache.MergeIncomingMessageAsync(
            message,
            new LocalMessageIngestionContext(IncomingMessageSource.Realtime, Guid.Empty)));
        await Assert.ThrowsAsync<ArgumentException>(() => cache.ApplySyncPageAsync(
            new SyncResponse([message], 1, 1, HasMore: false),
            0,
            null,
            LocalMessageIngestionContext.Background(IncomingMessageSource.Realtime)));

        Assert.Equal(0, Scalar(identity, "SELECT COUNT(*) FROM LocalMessages;"));
    }

    [Fact]
    public async Task MergeForegroundRealtime_WhenSyncCursorIsCorrupt_FailsScopeClosedWithoutPartialWrite()
    {
        var identity = CreateIdentity();
        await using var cache = await CreateCacheAsync(identity);
        var conversation = CreateConversation(lastMessageId: 0, lastReadMessageId: 0, unreadCount: 0);
        await ApplySnapshotAsync(cache, conversation);
        ExecuteNonQuery(
            identity,
            """
            INSERT INTO LocalAppState (Key, Value, UpdatedAt)
            VALUES ('LastSyncCursor', 'invalid', '2026-08-03T00:00:00Z')
            ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;
            """);

        var outcome = await cache.MergeIncomingMessageAsync(
            CreateMessage(1, conversation.Id),
            new LocalMessageIngestionContext(IncomingMessageSource.Realtime, conversation.Id));

        Assert.Equal(LocalCacheOperationStatus.FatalScope, outcome.Status);
        Assert.True(cache.IsFatal);
        Assert.Equal(0, Scalar(identity, "SELECT COUNT(*) FROM LocalMessages;"));
    }

    [Fact]
    public async Task ApplySyncPage_WhenSyncCursorIsCorrupt_FailsScopeClosedWithoutPartialWrite()
    {
        var identity = CreateIdentity();
        await using var cache = await CreateCacheAsync(identity);
        var conversation = CreateConversation(lastMessageId: 0, lastReadMessageId: 0, unreadCount: 0);
        await ApplySnapshotAsync(cache, conversation);
        ExecuteNonQuery(
            identity,
            """
            INSERT INTO LocalAppState (Key, Value, UpdatedAt)
            VALUES ('LastSyncCursor', 'invalid', '2026-08-03T00:00:00Z')
            ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;
            """);

        var outcome = await cache.ApplySyncPageAsync(
            new SyncResponse([CreateMessage(1, conversation.Id)], 1, 1, HasMore: false),
            0,
            null);

        Assert.Equal(LocalCacheOperationStatus.FatalScope, outcome.Status);
        Assert.True(cache.IsFatal);
        Assert.Equal(0, Scalar(identity, "SELECT COUNT(*) FROM LocalMessages;"));
    }

    [Fact]
    public async Task MergeRealtime_WhenAccountsShareConversationId_KeepsForegroundStateScoped()
    {
        var foregroundIdentity = CreateIdentity();
        var backgroundIdentity = AccountScopeIdentity.Create(
            new Uri("https://relaycove.example/team/"),
            Guid.Parse("bbbbbbbb-cccc-dddd-eeee-ffffffffffff"),
            rootDirectory);
        await using var foregroundCache = await CreateCacheAsync(foregroundIdentity);
        await using var backgroundCache = await CreateCacheAsync(backgroundIdentity);
        var conversation = CreateConversation(lastMessageId: 0, lastReadMessageId: 0, unreadCount: 0);
        var message = CreateMessage(1, conversation.Id);
        await ApplySnapshotAsync(foregroundCache, conversation);
        await ApplySnapshotAsync(backgroundCache, conversation);

        await foregroundCache.MergeIncomingMessageAsync(
            message,
            new LocalMessageIngestionContext(IncomingMessageSource.Realtime, conversation.Id));
        await backgroundCache.MergeIncomingMessageAsync(message);

        Assert.Equal(new MessageAttention(IsRead: true, IsNotificationHandled: true),
            ReadMessageAttention(foregroundIdentity, 1));
        Assert.Equal(new MessageAttention(IsRead: false, IsNotificationHandled: false),
            ReadMessageAttention(backgroundIdentity, 1));
        Assert.Equal(0, ReadConversationAttention(foregroundIdentity, conversation.Id).UnreadCount);
        Assert.Equal(1, ReadConversationAttention(backgroundIdentity, conversation.Id).UnreadCount);
    }

    [Fact]
    public async Task ConversationSnapshot_WhenRealtimeWinsRace_PreservesNewerLocalUnreadState()
    {
        var identity = CreateIdentity();
        await using var cache = await CreateCacheAsync(identity);
        var staleSnapshot = CreateConversation(lastMessageId: 100, lastReadMessageId: 90, unreadCount: 2);
        await ApplySnapshotAsync(cache, staleSnapshot);
        await cache.MergeIncomingMessageAsync(CreateMessage(101, staleSnapshot.Id));

        await ApplySnapshotAsync(cache, staleSnapshot with { Name = "stale refresh" });

        Assert.Equal(
            new ConversationAttention(101, 90, PendingReadThroughMessageId: null, 3),
            ReadConversationAttention(identity, staleSnapshot.Id));
    }

    [Fact]
    public async Task ConversationSnapshot_WhenStaleListMissesHigherRealtime_AddsBothUnreadSources()
    {
        var identity = CreateIdentity();
        await using var cache = await CreateCacheAsync(identity);
        var conversation = CreateConversation(lastMessageId: 9, lastReadMessageId: 9, unreadCount: 0);
        var realtime = CreateMessage(20, conversation.Id);
        await ApplySnapshotAsync(cache, conversation);
        await cache.MergeIncomingMessageAsync(realtime);

        await ApplySnapshotAsync(cache, conversation with
        {
            LastMessageId = 19,
            UnreadCount = 10,
            UpdatedAt = conversation.UpdatedAt.AddSeconds(1),
        });

        Assert.Equal(11, ReadConversationAttention(identity, conversation.Id).UnreadCount);

        var backfill = Enumerable.Range(10, 10)
            .Select(id => CreateMessage(id, conversation.Id))
            .Append(realtime)
            .ToArray();
        var page = await cache.ApplySyncPageAsync(
            new SyncResponse(backfill, 20, 20, HasMore: false),
            0,
            null);

        Assert.Equal(LocalCacheOperationStatus.Ready, page.Status);
        Assert.Equal(11, ReadConversationAttention(identity, conversation.Id).UnreadCount);
        Assert.Equal(11, Scalar(
            identity,
            $"""
            SELECT COUNT(*)
            FROM LocalMessages
            WHERE ConversationId = '{conversation.Id:D}'
              AND SenderId <> '{UserId:D}'
              AND IsRead = 0;
            """));
    }

    [Fact]
    public async Task ConversationSnapshot_WhenServerCatchesForegroundMessage_DoesNotResurrectItsUnread()
    {
        var identity = CreateIdentity();
        await using var cache = await CreateCacheAsync(identity);
        var conversation = CreateConversation(lastMessageId: 100, lastReadMessageId: 90, unreadCount: 2);
        await ApplySnapshotAsync(cache, conversation);
        await cache.MergeIncomingMessageAsync(
            CreateMessage(101, conversation.Id),
            new LocalMessageIngestionContext(IncomingMessageSource.Realtime, conversation.Id));

        await ApplySnapshotAsync(cache, conversation with
        {
            LastMessageId = 101,
            UnreadCount = 3,
            UpdatedAt = conversation.UpdatedAt.AddSeconds(1),
        });

        Assert.Equal(
            new ConversationAttention(101, 90, 101, 2),
            ReadConversationAttention(identity, conversation.Id));
    }

    [Fact]
    public async Task ConversationSnapshot_WhenServerConfirmsPendingReadThrough_ClearsTargetWithoutRegression()
    {
        var identity = CreateIdentity();
        await using var cache = await CreateCacheAsync(identity);
        var conversation = CreateConversation(lastMessageId: 0, lastReadMessageId: 0, unreadCount: 0);
        await ApplySnapshotAsync(cache, conversation);
        await cache.MergeIncomingMessageAsync(
            CreateMessage(1, conversation.Id),
            new LocalMessageIngestionContext(IncomingMessageSource.Realtime, conversation.Id));

        await ApplySnapshotAsync(cache, conversation with
        {
            LastMessageId = 1,
            LastReadMessageId = 1,
            UpdatedAt = conversation.UpdatedAt.AddSeconds(1),
        });

        Assert.Equal(
            new ConversationAttention(1, 1, PendingReadThroughMessageId: null, 0),
            ReadConversationAttention(identity, conversation.Id));
    }

    [Fact]
    public async Task ConversationSnapshot_WhenServerCatchesLargeGap_ImportsUnseenUnreadAndSubtractsKnownReadRow()
    {
        var identity = CreateIdentity();
        await using var cache = await CreateCacheAsync(identity);
        var conversation = CreateConversation(lastMessageId: 100, lastReadMessageId: 90, unreadCount: 2);
        await ApplySnapshotAsync(cache, conversation);
        var message99 = CreateMessage(99, conversation.Id);
        var message100 = CreateMessage(100, conversation.Id);
        await cache.MergeIncomingMessageAsync(message99);
        await cache.ApplySyncPageAsync(
            new SyncResponse([message100], 100, 100, HasMore: false),
            0,
            null);
        await cache.MergeIncomingMessageAsync(
            message100,
            new LocalMessageIngestionContext(IncomingMessageSource.Realtime, conversation.Id));
        await cache.MergeIncomingMessageAsync(
            CreateMessage(5000, conversation.Id),
            new LocalMessageIngestionContext(IncomingMessageSource.Realtime, conversation.Id));

        await ApplySnapshotAsync(cache, conversation with
        {
            LastMessageId = 5000,
            UnreadCount = 100,
            UpdatedAt = conversation.UpdatedAt.AddSeconds(1),
        });

        Assert.Equal(
            new ConversationAttention(5000, 100, 5000, 97),
            ReadConversationAttention(identity, conversation.Id));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(rootDirectory))
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    private AccountScopeIdentity CreateIdentity() => AccountScopeIdentity.Create(
        new Uri("https://relaycove.example/team/"),
        UserId,
        rootDirectory);

    private static Task<AccountScopedLocalCache> CreateCacheAsync(AccountScopeIdentity identity) =>
        AccountScopedLocalCache.CreateAsync(
            identity,
            NullLogger<AccountScopedLocalCache>.Instance);

    private static async Task ApplySnapshotAsync(
        AccountScopedLocalCache cache,
        params ConversationDto[] conversations) =>
        Assert.Equal(
            LocalCacheOperationStatus.Ready,
            await cache.ApplyAuthoritativeConversationSnapshotAsync(
                new ConversationListResponse(conversations, Complete: true)));

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

    private static MessageDto CreateMessage(
        long id,
        Guid conversationId,
        Guid? senderId = null) => new(
        id,
        Guid.NewGuid(),
        conversationId,
        senderId ?? OtherUserId,
        "Sender",
        MessageType.Text,
        $"message {id}",
        null,
        Array.Empty<AttachmentDto>(),
        Array.Empty<Guid>(),
        DateTimeOffset.Parse("2026-08-03T03:00:00Z").AddSeconds(id));

    private static MessageAttention ReadMessageAttention(
        AccountScopeIdentity identity,
        long messageId)
    {
        using var connection = OpenConnection(identity);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT IsRead, IsNotificationHandled
            FROM LocalMessages
            WHERE ServerMessageId = $messageId;
            """;
        command.Parameters.AddWithValue("$messageId", messageId);
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        return new MessageAttention(reader.GetBoolean(0), reader.GetBoolean(1));
    }

    private static MessageAttention ReadPendingMessageAttention(
        AccountScopeIdentity identity,
        Guid clientMessageId)
    {
        using var connection = OpenConnection(identity);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT IsRead, IsNotificationHandled
            FROM LocalMessages
            WHERE ClientMessageId = $clientMessageId AND ServerMessageId IS NULL;
            """;
        command.Parameters.AddWithValue("$clientMessageId", clientMessageId.ToString("D"));
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        return new MessageAttention(reader.GetBoolean(0), reader.GetBoolean(1));
    }

    private static ConversationAttention ReadConversationAttention(
        AccountScopeIdentity identity,
        Guid conversationId)
    {
        using var connection = OpenConnection(identity);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT LastMessageId, LastReadMessageId, PendingReadThroughMessageId, UnreadCount
            FROM LocalConversations
            WHERE Id = $conversationId;
            """;
        command.Parameters.AddWithValue("$conversationId", conversationId.ToString("D"));
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        return new ConversationAttention(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.IsDBNull(2) ? null : reader.GetInt64(2),
            reader.GetInt32(3));
    }

    private static long Scalar(AccountScopeIdentity identity, string sql)
    {
        using var connection = OpenConnection(identity);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static void SeedUnreadMessages(
        AccountScopeIdentity identity,
        Guid conversationId,
        int firstMessageId,
        int count)
    {
        using var connection = OpenConnection(identity);
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO LocalMessages (
                ServerMessageId, ClientMessageId, ConversationId, SenderId,
                SenderDisplayName, Type, Content, ReplyToMessageId, CreatedAt,
                LocalSendStatus)
            VALUES (
                $serverMessageId, $clientMessageId, $conversationId, $senderId,
                'Sender', 1, 'seed', NULL, $createdAt, 2);
            """;
        var serverMessageId = command.Parameters.Add("$serverMessageId", SqliteType.Integer);
        var clientMessageId = command.Parameters.Add("$clientMessageId", SqliteType.Text);
        command.Parameters.AddWithValue("$conversationId", conversationId.ToString("D"));
        command.Parameters.AddWithValue("$senderId", OtherUserId.ToString("D"));
        var createdAt = command.Parameters.Add("$createdAt", SqliteType.Text);
        command.Prepare();

        for (var offset = 0; offset < count; offset++)
        {
            var messageId = firstMessageId + offset;
            serverMessageId.Value = messageId;
            clientMessageId.Value = Guid.NewGuid().ToString("D");
            createdAt.Value = DateTimeOffset.Parse("2026-08-03T03:00:00Z")
                .AddSeconds(messageId)
                .ToString("O");
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static string TextScalar(AccountScopeIdentity identity, string sql)
    {
        using var connection = OpenConnection(identity);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar())!;
    }

    private static void ExecuteNonQuery(AccountScopeIdentity identity, string sql)
    {
        using var connection = OpenConnection(identity);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
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

    private sealed record MessageAttention(bool IsRead, bool IsNotificationHandled);

    private sealed record ConversationAttention(
        long LastMessageId,
        long LastReadMessageId,
        long? PendingReadThroughMessageId,
        int UnreadCount);
}
