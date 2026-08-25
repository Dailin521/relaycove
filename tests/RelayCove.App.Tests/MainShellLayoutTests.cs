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
        Assert.Contains("ConversationPaneView", source);
        Assert.Contains("ChatHeaderView", source);
        Assert.Contains("IsConversationWorkspaceSection", source);
        Assert.Contains("IsSavedSection", source);
    }

    [Fact]
    public void SavedMessages_WhenRendered_UsesAccountMenuEntryAndExistingContentPane()
    {
        var source = XDocument.Load(FindWorkspaceFile("src", "RelayCove.App", "MainPage.xaml"));
        XNamespace maui = "http://schemas.microsoft.com/dotnet/2021/maui";
        XNamespace x = "http://schemas.microsoft.com/winfx/2009/xaml";

        var entry = source
            .Descendants(maui + "Button")
            .Single(element => element.Attribute("Text")?.Value == "收藏的消息");
        var savedPanel = source
            .Descendants(maui + "Grid")
            .Single(element => element.Attribute("IsVisible")?.Value == "{Binding IsSavedSection}");
        var workspaceContent = savedPanel.Parent;

        Assert.Equal("FirstAccountMenuButton", entry.Attribute(x + "Name")?.Value);
        Assert.Equal("{Binding ShowSavedCommand}", entry.Attribute("Command")?.Value);
        Assert.Equal("1", workspaceContent?.Attribute("Grid.Column")?.Value);
        Assert.Contains(savedPanel.Descendants(maui + "Button"), button =>
            button.Attribute("Command")?.Value == "{Binding ShowMessagesCommand}");
        Assert.Contains(savedPanel.Descendants(maui + "CollectionView"), collection =>
            collection.Attribute("ItemsSource")?.Value == "{Binding SavedMessages}");
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

    [Fact]
    public void ReactionPicker_WhenRendered_UsesItsOwnTriggerAnchor()
    {
        var source = XDocument.Load(FindWorkspaceFile("src", "RelayCove.App", "MainPage.xaml"));

        var reactionPicker = source
            .Descendants()
            .Single(element => element.Name.LocalName == "Label" && element.Attribute("Text")?.Value == "添加反应")
            .Ancestors()
            .First(element => element.Name.LocalName == "Border");
        var anchor = Assert.Single(
            reactionPicker.Descendants(),
            element => element.Name.LocalName == "PopoverAnchorBehavior");

        Assert.Contains("ReactionPickerAnchorX", anchor.Attribute("AnchorX")?.Value, StringComparison.Ordinal);
        Assert.Contains("ReactionPickerAnchorY", anchor.Attribute("AnchorY")?.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("MessageMenuAnchor", anchor.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void MessageBubble_WhenContentIsShort_SizesToContentAndKeepsOwnMessageAlignment()
    {
        var source = XDocument.Load(FindWorkspaceFile("src", "RelayCove.App", "Controls", "MessageListView.xaml"));
        XNamespace maui = "http://schemas.microsoft.com/dotnet/2021/maui";
        XNamespace x = "http://schemas.microsoft.com/winfx/2009/xaml";

        var bubble = source
            .Descendants(maui + "Border")
            .Single(element => element.Attribute(x + "Name")?.Value == "Bubble");
        var ownMessageTrigger = bubble
            .Descendants(maui + "DataTrigger")
            .Single(element =>
                element.Attribute("Binding")?.Value == "{Binding IsOwn}" &&
                element.Attribute("Value")?.Value == "True");

        Assert.Equal("Start", bubble.Attribute("HorizontalOptions")?.Value);
        Assert.Contains(
            ownMessageTrigger.Elements(maui + "Setter"),
            setter =>
                setter.Attribute("Property")?.Value == "HorizontalOptions" &&
                setter.Attribute("Value")?.Value == "End");
    }

    [Fact]
    public void MessageReactions_WhenRendered_KeepSpacingOutsideNativeButtons()
    {
        var messageSource = XDocument.Load(FindWorkspaceFile("src", "RelayCove.App", "Controls", "MessageListView.xaml"));
        var styleSource = XDocument.Load(FindWorkspaceFile("src", "RelayCove.App", "Resources", "Styles", "ComponentStyles.xaml"));
        XNamespace maui = "http://schemas.microsoft.com/dotnet/2021/maui";
        XNamespace x = "http://schemas.microsoft.com/winfx/2009/xaml";

        var reactionLayout = messageSource
            .Descendants(maui + "FlexLayout")
            .Single(element => element.Attribute("BindableLayout.ItemsSource")?.Value == "{Binding Reactions}");
        var reactionButton = reactionLayout.Descendants(maui + "Button").Single();
        var spacingContainer = Assert.IsType<XElement>(reactionButton.Parent);
        var reactionStyle = styleSource
            .Descendants(maui + "Style")
            .Single(element => element.Attribute(x + "Key")?.Value == "ReactionButtonStyle");

        Assert.Equal("Grid", spacingContainer.Name.LocalName);
        Assert.Equal("0,0,4,4", spacingContainer.Attribute("Padding")?.Value);
        Assert.Equal("0", reactionButton.Attribute("Margin")?.Value);
        Assert.DoesNotContain(reactionStyle.Elements(maui + "Setter"), setter =>
            setter.Attribute("Property")?.Value == "Margin");
    }

    [Fact]
    public void ImageAttachments_WhenRendered_ShowOnlyPreviewAndAddDownloadToMessageContextMenu()
    {
        var messageSource = XDocument.Load(FindWorkspaceFile("src", "RelayCove.App", "Controls", "MessageListView.xaml"));
        var pageSource = XDocument.Load(FindWorkspaceFile("src", "RelayCove.App", "MainPage.xaml"));
        XNamespace maui = "http://schemas.microsoft.com/dotnet/2021/maui";
        XNamespace x = "http://schemas.microsoft.com/winfx/2009/xaml";

        var attachmentLayout = messageSource
            .Descendants(maui + "VerticalStackLayout")
            .Single(element => element.Attribute("BindableLayout.ItemsSource")?.Value == "{Binding Attachments}");
        var imagePreview = attachmentLayout
            .Descendants(maui + "Border")
            .Single(element => element.Attribute("IsVisible")?.Value == "{Binding IsImage}");
        var bubble = messageSource
            .Descendants(maui + "Border")
            .Single(element => element.Attribute(x + "Name")?.Value == "Bubble");
        var imageOnlyTrigger = bubble
            .Descendants(maui + "DataTrigger")
            .Single(element => element.Attribute("Binding")?.Value == "{Binding IsImageOnly}");
        var fileDetails = attachmentLayout
            .Descendants(maui + "Grid")
            .Single(element => element.Attribute("IsVisible")?.Value == "{Binding IsFile}");
        var imageDownload = pageSource
            .Descendants(maui + "Button")
            .Single(element => element.Attribute("Text")?.Value == "下载原图");

        Assert.Single(
            imagePreview.Descendants(),
            element => element.Name.LocalName == "ImageAttachmentContextBehavior");
        Assert.Contains(imageOnlyTrigger.Elements(maui + "Setter"), setter =>
            setter.Attribute("Property")?.Value == "Padding" && setter.Attribute("Value")?.Value == "0");
        Assert.Contains(imageOnlyTrigger.Elements(maui + "Setter"), setter =>
            setter.Attribute("Property")?.Value == "Background" && setter.Attribute("Value")?.Value == "Transparent");
        Assert.Contains(imageOnlyTrigger.Elements(maui + "Setter"), setter =>
            setter.Attribute("Property")?.Value == "StrokeThickness" && setter.Attribute("Value")?.Value == "0");
        Assert.Contains(fileDetails.Descendants(maui + "Label"), label =>
            label.Attribute("Text")?.Value == "{Binding Name}");
        Assert.Contains(fileDetails.Descendants(maui + "Button"), button =>
            button.Attribute("Text")?.Value == "下载");
        Assert.Equal("{Binding HasActiveMessageAttachment}", imageDownload.Attribute("IsVisible")?.Value);
        Assert.Equal("{Binding DownloadAttachmentCommand}", imageDownload.Attribute("Command")?.Value);
        Assert.Equal("{Binding ActiveMessageAttachment}", imageDownload.Attribute("CommandParameter")?.Value);
    }

    [Fact]
    public void MessageBody_WhenRendered_EnablesNativeTextSelectionWithoutSelectingListRows()
    {
        var source = XDocument.Load(FindWorkspaceFile("src", "RelayCove.App", "Controls", "MessageListView.xaml"));
        XNamespace maui = "http://schemas.microsoft.com/dotnet/2021/maui";

        var messageBody = source
            .Descendants(maui + "Label")
            .Single(element =>
                element.Attribute("Text")?.Value == "{Binding Body}" &&
                element.Attribute("IsVisible")?.Value == "{Binding HasBody}");
        var messageCollection = source
            .Descendants(maui + "CollectionView")
            .Single(element => element.Attribute("ItemsSource")?.Value == "{Binding Messages}");

        Assert.Single(
            messageBody.Descendants(),
            element => element.Name.LocalName == "SelectableTextBehavior");
        Assert.Single(
            source.Descendants(),
            element => element.Name.LocalName == "SelectableTextBehavior");
        Assert.Equal("None", messageCollection.Attribute("SelectionMode")?.Value);
    }

    [Fact]
    public void ConversationFilterLoadMore_WhenSearchEnds_UsesDirectVisibilityBindingOutsideCollectionFooter()
    {
        var source = XDocument.Load(FindWorkspaceFile("src", "RelayCove.App", "Controls", "ConversationPaneView.xaml"));
        XNamespace maui = "http://schemas.microsoft.com/dotnet/2021/maui";
        XNamespace x = "http://schemas.microsoft.com/winfx/2009/xaml";

        var button = source
            .Descendants(maui + "Button")
            .Single(element => element.Attribute(x + "Name")?.Value == "ConversationFilterLoadMoreButton");

        Assert.Equal("{Binding ShowMoreConversationFilterResults}", button.Attribute("IsVisible")?.Value);
        Assert.Equal("{Binding LoadMoreConversationFilterCommand}", button.Attribute("Command")?.Value);
        Assert.Empty(source.Descendants(maui + "CollectionView.Footer"));
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
