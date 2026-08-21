using System.Xml.Linq;

namespace RelayCove.App.Tests;

public sealed class ConversationPaneViewTests
{
    [Fact]
    public void ConversationPane_WhenRendered_UsesOneWechatStyleTimelineWithoutChannelOrDirectGroups()
    {
        var source = XDocument.Load(FindWorkspaceFile("src", "RelayCove.App", "Controls", "ConversationPaneView.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2009/xaml";
        var createButton = source.Descendants()
            .Single(element => element.Attribute(x + "Name")?.Value == "NewConversationButton");
        var conversationList = source.Descendants()
            .Single(element => element.Name.LocalName == "CollectionView" &&
                               element.Attribute("ItemsSource")?.Value == "{Binding FilteredConversations}");
        var labels = source.Descendants()
            .Where(element => element.Name.LocalName == "Label")
            .Select(element => element.Attribute("Text")?.Value)
            .ToArray();

        Assert.NotNull(conversationList);
        Assert.Equal("{Binding OpenNewConversationCommand}", createButton.Attribute("Command")?.Value);
        Assert.DoesNotContain("频道", labels);
        Assert.DoesNotContain("私信", labels);
        Assert.DoesNotContain(source.Descendants(), element => element.Attribute("ItemsSource")?.Value is "{Binding FilteredChannels}" or "{Binding FilteredDirectMessages}");
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
