using RelayCove.Core;

namespace RelayCove.Core.Tests;

public sealed class ConversationSummaryReducerTests
{
    [Fact]
    public void Apply_WhenRealtimeChannelMessageArrives_ProjectsTopicAndLatestConversationSummary()
    {
        var conversation = new ChannelTopic(7, "release");
        var message = Message(42, conversation);

        var state = DomainReducer.Apply(ClientState.Empty, new MessageUpsertEvent(message, 10));

        Assert.Equal(42, state.Topics[conversation.CanonicalKey].MaxMessageId);
        Assert.Equal(message, state.ConversationSummaries[conversation.CanonicalKey].LatestMessage);
    }

    [Fact]
    public void Apply_WhenLatestMessageMoves_RefreshesSourceAndDestinationConversationSummaries()
    {
        var source = new ChannelTopic(1, "old");
        var destination = new ChannelTopic(2, "new");
        var older = Message(10, source);
        var latest = Message(20, source);
        var state = DomainReducer.Apply(ClientState.Empty,
        [
            new MessageUpsertEvent(older, 1),
            new MessageUpsertEvent(latest, 2),
            new MessageMovedEvent([20], destination, 3)
        ]);

        Assert.Equal(older.Id, state.ConversationSummaries[source.CanonicalKey].LatestMessage.Id);
        Assert.Equal(latest.Id, state.ConversationSummaries[destination.CanonicalKey].LatestMessage.Id);
        Assert.Equal(latest.Id, state.Topics[destination.CanonicalKey].MaxMessageId);
    }

    private static ChatMessage Message(long id, ConversationKey conversation) =>
        new(id, conversation, 8, "raw", DateTimeOffset.UnixEpoch.AddSeconds(id));
}
