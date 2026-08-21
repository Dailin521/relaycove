using System.Xml.Linq;

namespace RelayCove.App.Tests;

public sealed class ChatHeaderViewTests
{
    [Fact]
    public void HeaderActions_WhenRendered_KeepOnlySearchAndConversationSettings()
    {
        var xamlPath = FindWorkspaceFile("src", "RelayCove.App", "Controls", "ChatHeaderView.xaml");
        var source = XDocument.Load(xamlPath);
        XNamespace x = "http://schemas.microsoft.com/winfx/2009/xaml";
        var buttons = source.Descendants()
            .Where(element => element.Name.LocalName is "Button" or "ImageButton")
            .ToArray();
        var searchButton = buttons.Single(element => element.Attribute(x + "Name")?.Value == "SearchButton");
        var settingsButton = buttons.Single(element => element.Attribute(x + "Name")?.Value == "SettingsButton");

        Assert.Equal("{Binding OpenSearchCommand}", searchButton.Attribute("Command")?.Value);
        Assert.Equal("{Binding ToggleDetailsCommand}", settingsButton.Attribute("Command")?.Value);
        Assert.Equal("{Binding CanOpenConversationSettings}", settingsButton.Attribute("IsEnabled")?.Value);
        Assert.Equal("打开会话设置", settingsButton.Attribute("SemanticProperties.Description")?.Value);
        Assert.DoesNotContain(buttons, element => element.Attribute(x + "Name")?.Value is "DetailsButton" or "TopicMenuButton");
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
