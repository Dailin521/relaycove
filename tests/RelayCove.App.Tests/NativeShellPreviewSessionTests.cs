using RelayCove.App.Services;
using RelayCove.Core;

namespace RelayCove.App.Tests;

public sealed class NativeShellPreviewSessionTests
{
    [Fact]
    public void Constructor_WhenCreated_MirrorsTheWebAcceptanceWorkspace()
    {
        var session = new NativeShellPreviewSession();

        Assert.Equal(6, session.CurrentUserId);
        Assert.Equal("林远", session.State.Users[6].FullName);
        Assert.Equal(4, session.State.Subscriptions.Count);
        Assert.Equal(2, session.State.Subscriptions.Values.Count(PrivateGroupPolicy.IsEligible));
        Assert.Contains(session.State.Subscriptions.Values, item => item.Name == "产品设计群");
        Assert.Contains(session.State.Subscriptions.Values, item => item.Name == "Windows 客户端群");
        Assert.Equal(5, session.RecentDirectMessages.Count);
        Assert.Equal(new ChannelTopic(6, string.Empty), session.SelectedConversation);
        Assert.True(session.CanCreatePrivateGroup);

        var selectedMessages = session.State.Messages.Values
            .Where(message => message.Conversation == session.SelectedConversation)
            .OrderBy(message => message.Id)
            .ToArray();
        Assert.Equal(4, selectedMessages.Length);
        Assert.Equal("Maya Chen", selectedMessages[0].SenderDisplayName);
        Assert.Equal(3, selectedMessages[1].Reactions.Count);
        Assert.Contains("```quote", selectedMessages[2].Content, StringComparison.Ordinal);
        Assert.Contains("relaycove-team-avatars.png", selectedMessages[3].Content, StringComparison.Ordinal);
        Assert.Equal(102, session.UnreadDividerAfterMessageId);
        Assert.Equal("4 条未读消息", session.UnreadDividerLabel);
    }

    [Theory]
    [InlineData("640", 640)]
    [InlineData("1024", 1024)]
    [InlineData("invalid", 1440)]
    [InlineData("479", 1440)]
    public void ParsePreviewDimension_WhenValueProvided_UsesOnlyBoundedIntegers(string value, int expected)
    {
        Assert.Equal(expected, NativeShellPreviewSession.ParsePreviewDimension(value, 1440, 480, 3840));
    }
}
