using System.Xml.Linq;

namespace RelayCove.App.Tests;

public sealed class ChatHeaderViewTests
{
    [Fact]
    public void TopicMenuButton_WhenRendered_OpensCurrentTopicMenuInsteadOfDisabledPlaceholder()
    {
        var xamlPath = FindWorkspaceFile("src", "RelayCove.App", "Controls", "ChatHeaderView.xaml");
        var codePath = FindWorkspaceFile("src", "RelayCove.App", "Controls", "ChatHeaderView.xaml.cs");
        var mainPageCodePath = FindWorkspaceFile("src", "RelayCove.App", "MainPage.xaml.cs");
        var source = XDocument.Load(xamlPath);
        var codeBehind = File.ReadAllText(codePath);
        var mainPageCodeBehind = File.ReadAllText(mainPageCodePath);
        XNamespace x = "http://schemas.microsoft.com/winfx/2009/xaml";
        var button = source.Descendants()
            .Single(element => element.Attribute(x + "Name")?.Value == "TopicMenuButton");

        Assert.Equal("{Binding HasSelectedTopic}", button.Attribute("IsEnabled")?.Value);
        Assert.Equal("OnTopicMenuClicked", button.Attribute("Clicked")?.Value);
        Assert.Equal("当前话题操作", button.Attribute("SemanticProperties.Description")?.Value);
        Assert.Contains("OpenTopicMenuAtCommand.Execute(new TopicMenuRequest", codeBehind);
        Assert.Contains("RestoreFocusToHeader: true", codeBehind);
        Assert.Contains("HeaderTopicMenuFocusRequest", mainPageCodeBehind);
        Assert.Contains("ChatHeader.FocusTopicMenuButton", mainPageCodeBehind);
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
