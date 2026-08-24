using System.Xml.Linq;

namespace RelayCove.App.Tests;

public sealed class MainShellLayoutTests
{
    [Fact]
    public void MainShell_WhenRendered_UsesConversationAndChatWithoutPrimaryNavigationRail()
    {
        var source = File.ReadAllText(FindWorkspaceFile("src", "RelayCove.App", "MainPage.xaml"));

        Assert.DoesNotContain("NavigationRailView", source);
        Assert.DoesNotContain("ContactsPageView", source);
        Assert.DoesNotContain("IsSavedSection", source);
        Assert.Contains("ConversationPaneView", source);
        Assert.Contains("ChatHeaderView", source);
    }

    [Fact]
    public void MainPage_WhenWindowActivates_RechecksForegroundAfterNativeActivationSettles()
    {
        var source = File.ReadAllText(FindWorkspaceFile("src", "RelayCove.App", "MainPage.xaml.cs"));

        Assert.Contains("RecheckWindowActivation();", source);
        Assert.Contains("_viewModel.SetWindowActive(false);", source);
        Assert.Contains("Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(100)", source);
        Assert.Contains("revision == _windowActivationRevision", source);
    }

    [Fact]
    public void ProductBar_WhenRendered_ProvidesLeadingAccountAvatarAndTrailingSettings()
    {
        var source = File.ReadAllText(FindWorkspaceFile("src", "RelayCove.App", "Controls", "ProductBarView.xaml"));
        var codeBehind = File.ReadAllText(FindWorkspaceFile("src", "RelayCove.App", "Controls", "ProductBarView.xaml.cs"));
        var mainPage = File.ReadAllText(FindWorkspaceFile("src", "RelayCove.App", "MainPage.xaml"));
        var mainDocument = XDocument.Parse(mainPage);
        XNamespace maui = "http://schemas.microsoft.com/dotnet/2021/maui";
        XNamespace x = "http://schemas.microsoft.com/winfx/2009/xaml";

        Assert.Contains("x:Name=\"AccountButton\"", source);
        Assert.Contains("SourceUrl=\"{Binding CurrentUserAvatarUrl}\"", source);
        Assert.Contains("Command=\"{Binding ToggleAccountMenuCommand}\"", source);
        var leadingContentStart = source.IndexOf("<TitleBar.LeadingContent>", StringComparison.Ordinal);
        var leadingContentEnd = source.IndexOf("</TitleBar.LeadingContent>", StringComparison.Ordinal);
        var accountButtonIndex = source.IndexOf("x:Name=\"AccountButtonBorder\"", StringComparison.Ordinal);
        Assert.InRange(accountButtonIndex, leadingContentStart, leadingContentEnd);
        Assert.DoesNotContain("Text=\"R\"", source);
        Assert.Contains("x:Name=\"SettingsButton\"", source);
        Assert.Contains("Source=\"icon_settings.png\"", source);
        Assert.Contains("Binding=\"{Binding IsSettingsSection, Source={x:Reference Root}", source);
        Assert.Contains("SettingsButton.Command = viewModel.ToggleSettingsCommand;", codeBehind);
        var accountMenuButton = mainDocument
            .Descendants(maui + "Button")
            .Single(element => element.Attribute(x + "Name")?.Value == "FirstAccountMenuButton");
        Assert.Equal("Start", accountMenuButton.Ancestors(maui + "Border").First().Attribute("HorizontalOptions")?.Value);
    }

    [Fact]
    public void NewPrivateGroupDialog_WhenRendered_UsesSeparateRowsForSearchAndGroupName()
    {
        var source = XDocument.Load(FindWorkspaceFile("src", "RelayCove.App", "MainPage.xaml"));
        XNamespace maui = "http://schemas.microsoft.com/dotnet/2021/maui";
        XNamespace x = "http://schemas.microsoft.com/winfx/2009/xaml";

        var searchEntry = source
            .Descendants(maui + "Entry")
            .Single(element => element.Attribute(x + "Name")?.Value == "NewConversationSearchEntry");
        var groupNameEntry = source
            .Descendants(maui + "Entry")
            .Single(element => element.Attribute(x + "Name")?.Value == "NewPrivateGroupNameEntry");
        var inputGrid = searchEntry.Parent;

        Assert.NotNull(inputGrid);
        Assert.Same(inputGrid, groupNameEntry.Parent);
        Assert.Equal("Auto,Auto", inputGrid.Attribute("RowDefinitions")?.Value);
        Assert.Equal("8", inputGrid.Attribute("RowSpacing")?.Value);
        Assert.Equal("0", searchEntry.Attribute("Grid.Row")?.Value);
        Assert.Equal("1", groupNameEntry.Attribute("Grid.Row")?.Value);
        Assert.Null(groupNameEntry.Attribute("Margin"));
    }

    [Fact]
    public void EmojiPickers_WhenRendered_UseUnclippedHorizontalCategoryScrollers()
    {
        var source = XDocument.Load(FindWorkspaceFile("src", "RelayCove.App", "MainPage.xaml"));
        XNamespace maui = "http://schemas.microsoft.com/dotnet/2021/maui";

        var categoryScrollers = source
            .Descendants(maui + "ScrollView")
            .Where(element =>
                element.Attribute("Orientation")?.Value == "Horizontal" &&
                element.Descendants(maui + "HorizontalStackLayout").Any(layout =>
                    layout.Attribute("BindableLayout.ItemsSource")?.Value == "{Binding EmojiCategories}"))
            .ToArray();

        Assert.Equal(2, categoryScrollers.Length);
        Assert.All(categoryScrollers, scroller =>
        {
            Assert.Equal("42", scroller.Attribute("HeightRequest")?.Value);
            Assert.Equal("Never", scroller.Attribute("HorizontalScrollBarVisibility")?.Value);
            var layout = Assert.Single(scroller.Descendants(maui + "HorizontalStackLayout"));
            Assert.Equal("0,3,8,5", layout.Attribute("Padding")?.Value);
        });
        Assert.Equal(2, source.Descendants().Count(element =>
            element.Name.LocalName == "HorizontalDragScrollBehavior"));
        var raw = source.ToString();
        Assert.DoesNotContain("选择后插入光标位置", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("再次选择可移除", raw, StringComparison.Ordinal);
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
