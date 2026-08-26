using RelayCove.Core;

namespace RelayCove.Core.Tests;

public sealed class DomainReducerTests
{
    [Fact]
    public void Apply_WhenMessageEventReplayedAndIdsSkip_RemainsIdempotentAndTracksHighestEventId()
    {
        var message = Message(1, new ChannelTopic(1, "general"));
        var state = DomainReducer.Apply(ClientState.Empty, new MessageUpsertEvent(message, 2));
        state = DomainReducer.Apply(state, new MessageUpsertEvent(message, 9));
        state = DomainReducer.Apply(state, new MessageUpsertEvent(message, 2));

        Assert.Single(state.Messages);
        Assert.Equal(9, state.LastEventId);
    }

    [Fact]
    public void Apply_WhenSubscriptionRemoved_ClearsChannelMessagesTopicsAndUnread()
    {
        var conversation = new ChannelTopic(7, "release");
        var state = DomainReducer.Apply(ClientState.Empty, new MessageUpsertEvent(Message(1, conversation)));
        state = DomainReducer.Apply(state, new TopicUpsertEvent(new TopicSummary(7, "release")));
        state = DomainReducer.Apply(state, new SubscriptionChangedEvent(new Subscription(7, "Release"), true));

        Assert.Empty(state.Messages);
        Assert.Empty(state.Topics);
        Assert.Equal(0, state.Unread.Total);
    }

    [Fact]
    public void Apply_WhenBatchMoveThenDelete_UpdatesOnlyRemainingMessages()
    {
        var source = new ChannelTopic(1, "one");
        var destination = new ChannelTopic(2, "two");
        var state = DomainReducer.Apply(ClientState.Empty, [new MessageUpsertEvent(Message(1, source)), new MessageUpsertEvent(Message(2, source))]);
        state = DomainReducer.Apply(state, new MessageMovedEvent([1L, 2L], destination));
        state = DomainReducer.Apply(state, new MessageDeletedEvent([1L, 404L]));

        Assert.DoesNotContain(1, state.Messages.Keys);
        Assert.Equal(destination, state.Messages[2].Conversation);
    }

    [Fact]
    public void Apply_WhenBatchUpdated_ReplacesEachKnownMessageById()
    {
        var state = DomainReducer.Apply(ClientState.Empty, [new MessageUpsertEvent(Message(1)), new MessageUpsertEvent(Message(2))]);
        state = DomainReducer.Apply(state, new MessagesUpdatedEvent([Message(1) with { Content = "one" }, Message(2) with { Content = "two" }]));

        Assert.Equal("one", state.Messages[1].Content);
        Assert.Equal("two", state.Messages[2].Content);
    }

    [Fact]
    public void Apply_WhenReadFlagTargetsAllAndThenSpecificRemove_UpdatesUnreadState()
    {
        var state = DomainReducer.Apply(ClientState.Empty, [new MessageUpsertEvent(Message(1)), new MessageUpsertEvent(Message(2))]);
        state = DomainReducer.Apply(state, new MessageFlagsChangedEvent([], true, MessageFlagOperation.Add, "read"));
        state = DomainReducer.Apply(state, new MessageFlagsChangedEvent([2L], false, MessageFlagOperation.Remove, "read"));

        Assert.True(state.Messages[1].IsRead);
        Assert.False(state.Messages[2].IsRead);
        Assert.Equal(1, state.Unread.Total);
    }

    [Fact]
    public void Apply_WhenOutboxConfirmationRacesWithRealtimeMessage_RemovesLocalEntryAndKeepsMessage()
    {
        const string localId = "1";
        var entry = new OutboxEntry(localId, new DirectMessage([2]), "hello", DateTimeOffset.UtcNow);
        var message = Message(100, new DirectMessage([2]));
        var state = DomainReducer.Apply(ClientState.Empty, new OutboxQueuedEvent(entry));
        state = DomainReducer.Apply(state, new MessageUpsertEvent(message, 4));
        state = DomainReducer.Apply(state, new SendConfirmedEvent(localId, message, 5));

        Assert.Empty(state.Outbox);
        Assert.Single(state.Messages);
    }

    [Fact]
    public void Apply_WhenRealtimeMessageCarriesLocalId_ReconcilesOutboxEntry()
    {
        const string localId = "2";
        var state = DomainReducer.Apply(ClientState.Empty, new OutboxQueuedEvent(new OutboxEntry(localId, new DirectMessage([]), "hello", DateTimeOffset.UtcNow)));
        state = DomainReducer.Apply(state, new MessageUpsertEvent(Message(101), LocalId: localId));

        Assert.Empty(state.Outbox);
        Assert.Equal(localId, state.Messages[101].ClientLocalId);
    }

    [Fact]
    public void Apply_WhenCorrelatedMessageIsRefreshed_PreservesClientLocalId()
    {
        const string localId = "3";
        var message = Message(102);
        var state = DomainReducer.Apply(
            ClientState.Empty,
            new MessageUpsertEvent(message, LocalId: localId));

        state = DomainReducer.Apply(
            state,
            new MessagesUpdatedEvent([message with { Content = "updated" }]));

        Assert.Equal(localId, state.Messages[102].ClientLocalId);
        Assert.Equal("updated", state.Messages[102].Content);
    }

    [Fact]
    public void Apply_WhenOneServerEventMapsToMultipleEffects_AppliesEveryEqualEventId()
    {
        var message = Message(77, new ChannelTopic(1, "old"));
        var state = DomainReducer.Apply(ClientState.Empty,
        [
            new MessageUpsertEvent(message, 10),
            new MessageMovedEvent([77], new ChannelTopic(2, "new"), 10)
        ]);

        Assert.Equal(new ChannelTopic(2, "new"), state.Messages[77].Conversation);
        Assert.Equal(10, state.LastEventId);
    }

    [Fact]
    public void Apply_WhenAtomicEventGroupIsReplayed_SkipsWholeGroup()
    {
        var original = Message(88, new ChannelTopic(1, "old"));
        DomainEvent[] group =
        [
            new MessageUpsertEvent(original, 11),
            new MessageDeletedEvent([88], 11)
        ];

        var state = DomainReducer.Apply(ClientState.Empty, group);
        state = DomainReducer.Apply(state, group);

        Assert.Empty(state.Messages);
        Assert.Equal(11, state.LastEventId);
    }

    [Fact]
    public void Apply_WhenOutboxFails_KeepsContentOnlyInMemoryWithSafeFailureKind()
    {
        var entry = new OutboxEntry("9", new DirectMessage([2]), "secret body", DateTimeOffset.UnixEpoch);
        var state = DomainReducer.Apply(ClientState.Empty, new OutboxQueuedEvent(entry));

        state = DomainReducer.Apply(state, new OutboxFailedEvent("9", OutboxFailureKind.NetworkResultUnknown));

        Assert.Equal(OutboxState.Failed, state.Outbox["9"].State);
        Assert.Equal(OutboxFailureKind.NetworkResultUnknown, state.Outbox["9"].Failure);
        Assert.DoesNotContain("secret body", state.Outbox["9"].ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_WhenHeartbeatOrUnknownEventReceived_DoesNotThrowOrChangeMessages()
    {
        var state = DomainReducer.Apply(ClientState.Empty, new HeartbeatEvent(1));
        state = DomainReducer.Apply(state, new UnknownDomainEvent("future", 2));

        Assert.Empty(state.Messages);
        Assert.Equal(2, state.LastEventId);
    }

    [Fact]
    public void Apply_WhenReactionIsAddedReplayedAndRemoved_IsIdempotent()
    {
        var identity = new EmojiReactionIdentity("thumbs_up", "1f44d", "unicode_emoji");
        var reaction = new EmojiReaction(identity, 2, "Bea");
        var state = DomainReducer.Apply(ClientState.Empty, new MessageUpsertEvent(Message(1)));

        state = DomainReducer.Apply(state, new MessageReactionChangedEvent(1, reaction, true));
        state = DomainReducer.Apply(state, new MessageReactionChangedEvent(1, reaction, true));

        Assert.Single(state.Messages[1].Reactions);
        state = DomainReducer.Apply(state, new MessageReactionChangedEvent(1, reaction, false));
        Assert.Empty(state.Messages[1].Reactions);
    }

    [Fact]
    public void Apply_WhenStarredFlagChanges_UpdatesOnlyTargetMessages()
    {
        var state = DomainReducer.Apply(ClientState.Empty, [new MessageUpsertEvent(Message(1)), new MessageUpsertEvent(Message(2))]);

        state = DomainReducer.Apply(state, new MessageFlagsChangedEvent([1], false, MessageFlagOperation.Add, "starred"));
        state = DomainReducer.Apply(state, new MessageFlagsChangedEvent([2], false, MessageFlagOperation.Remove, "starred"));

        Assert.True(state.Messages[1].IsStarred);
        Assert.False(state.Messages[2].IsStarred);
    }

    private static ChatMessage Message(long id, ConversationKey? conversation = null) => new(id, conversation ?? new DirectMessage([]), 1, "content", DateTimeOffset.UnixEpoch);
}
