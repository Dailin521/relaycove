using System.Xml.Linq;

namespace RelayCove.App.Tests;

public sealed class ConversationPaneViewTests
{
    [Fact]
    public void ChannelHeader_WhenRendered_ExposesCreateChannelInsteadOfBrowse()
    {
        var source = XDocument.Load(FindWorkspaceFile("src", "RelayCove.App", "Controls", "ConversationPaneView.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2009/xaml";
        var browseButton = source.Descendants()
            .SingleOrDefault(element => element.Attribute(x + "Name")?.Value == "BrowseChannelsButton");
        var createButton = source.Descendants()
            .Single(element => element.Attribute(x + "Name")?.Value == "CreateChannelButton");
        var channelLabel = source.Descendants()
            .Single(element => element.Name.LocalName == "Label" && element.Attribute("Text")?.Value == "频道");

        Assert.Null(browseButton);
        Assert.Equal("18,*,Auto,26", channelLabel.Parent?.Attribute("ColumnDefinitions")?.Value);
        Assert.Equal("OnCreateChannelClicked", createButton.Attribute("Clicked")?.Value);
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
