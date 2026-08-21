namespace RelayCove.App.Tests;

public sealed class DetailsPaneViewTests
{
    [Fact]
    public void DetailsPane_WhenRendered_ShowsSeparatePrivateAndChannelSettings()
    {
        var source = File.ReadAllText(FindWorkspaceFile("src", "RelayCove.App", "Controls", "DetailsPaneView.xaml"));

        Assert.Contains("x:Name=\"DirectMessageSettingsSection\"", source);
        Assert.Contains("IsVisible=\"{Binding ShowDirectMessageSettings}\"", source);
        Assert.Contains("DetailsAvatarUrl", source);
        Assert.Contains("DetailsTitle", source);
        Assert.Contains("Text=\"消息免打扰\"", source);
        Assert.Contains("Text=\"置顶聊天\"", source);
        Assert.Contains("x:Name=\"ChannelConversationSettingsSection\"", source);
        Assert.Contains("IsVisible=\"{Binding ShowChannelDetails}\"", source);
        Assert.Contains("DetailsMembers", source);
        Assert.Contains("Text=\"群聊名称\"", source);
        Assert.Contains("Text=\"群公告\"", source);
        Assert.Contains("Text=\"备注\"", source);
        Assert.Contains("Text=\"退出群聊\"", source);
        Assert.Contains("Text=\"清聊天记录\"", source);
        Assert.Contains("确认清除本机缓存", source);
        Assert.Contains("IsEnabled=\"{Binding CanManageSelectedChannel}\"", source);
        Assert.Contains("IsEnabled=\"{Binding CanExitPrivateGroup}\"", source);
        Assert.Contains("GroupInviteCandidates", source);
        Assert.Contains("RequestTransferPrivateGroupOwnershipCommand", source);
        Assert.Contains("RequestDissolvePrivateGroupCommand", source);
        Assert.Contains("服务器私有历史不会删除", source);
    }

    private static string FindWorkspaceFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Unable to locate workspace file: {Path.Combine(parts)}");
    }
}
