using RelayCove.Core;

namespace RelayCove.Core.Tests;

public sealed class ConversationKeyTests
{
    [Fact]
    public void DirectMessage_WhenNoOtherUsers_UsesSelfCanonicalKey()
    {
        var key = new DirectMessage([]);

        Assert.Equal("dm:self", key.CanonicalKey);
    }

    [Fact]
    public void DirectMessage_WhenGroupContainsDuplicates_SortsAndDeduplicates()
    {
        var key = new DirectMessage([8, 3, 8, 5]);

        Assert.Equal([3L, 5L, 8L], key.OtherUserIds);
        Assert.Equal("dm:3,5,8", key.CanonicalKey);
    }

    [Fact]
    public void ChannelTopic_WhenEmptyTopic_ProducesStableKey()
    {
        var key = new ChannelTopic(7, string.Empty);

        Assert.Equal("channel:7:", key.CanonicalKey);
    }

    [Fact]
    public void DirectMessage_WhenParticipantsMatch_HasValueEqualityAndImmutableParticipants()
    {
        var first = new DirectMessage([8, 3, 5]);
        var second = new DirectMessage([5, 8, 3]);

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.False(first.OtherUserIds is long[]);
    }
}
