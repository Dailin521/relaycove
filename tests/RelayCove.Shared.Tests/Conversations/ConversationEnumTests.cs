using RelayCove.Shared.Conversations;

namespace RelayCove.Shared.Tests.Conversations;

public sealed class ConversationEnumTests
{
    [Fact]
    public void ConversationEnums_WhenInspected_HaveStableProtocolValues()
    {
        Assert.Equal(1, (int)ConversationType.PublicChannel);
        Assert.Equal(2, (int)ConversationType.PrivateChannel);
        Assert.Equal(3, (int)ConversationType.Direct);
        Assert.Equal(1, (int)ConversationMemberRole.Member);
        Assert.Equal(2, (int)ConversationMemberRole.Administrator);
    }
}
