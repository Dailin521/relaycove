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
        var emptyLabel = conversationList.Descendants()
            .Single(element => element.Name.LocalName == "Label" &&
                               element.Attribute("Text")?.Value == "{Binding ConversationFilterEmptyText}");
        var labels = source.Descendants()
            .Where(element => element.Name.LocalName == "Label")
            .Select(element => element.Attribute("Text")?.Value)
            .ToArray();

        Assert.NotNull(conversationList);
        Assert.Equal("viewModels:ShellViewModel", emptyLabel.Attribute(x + "DataType")?.Value);
        Assert.DoesNotContain("BindingContext.ConversationFilterEmptyText", source.ToString());
        Assert.Null(createButton.Attribute("Command"));
        Assert.Equal("OnNewConversationClicked", createButton.Attribute("Clicked")?.Value);
        var codeBehind = File.ReadAllText(FindWorkspaceFile("src", "RelayCove.App", "Controls", "ConversationPaneView.xaml.cs"));
        Assert.Contains("Text = \"发起私聊\"", codeBehind, StringComparison.Ordinal);
        Assert.Contains("Text = \"发起群聊\"", codeBehind, StringComparison.Ordinal);
        Assert.Contains("Text = \"查看我的所有群聊\"", codeBehind, StringComparison.Ordinal);
        Assert.Contains("Command = viewModel.OpenNewConversationCommand", codeBehind, StringComparison.Ordinal);
        Assert.Contains("Command = viewModel.ShowNewChannelConversationCommand", codeBehind, StringComparison.Ordinal);
        Assert.Contains("viewModel.PrivateGroupConversations.ToArray()", codeBehind, StringComparison.Ordinal);
        Assert.Contains("viewModel.ActivateConversation(group)", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("频道", labels);
        Assert.DoesNotContain("私信", labels);
        Assert.DoesNotContain(source.Descendants(), element => element.Attribute("ItemsSource")?.Value is "{Binding FilteredChannels}" or "{Binding FilteredDirectMessages}");
        var presenceDot = source.Descendants()
            .Single(element => element.Name.LocalName == "Border" &&
                               element.Attribute("IsVisible")?.Value == "{Binding HasPresence}");
        Assert.Equal("{Binding PresenceBrush}", presenceDot.Attribute("Background")?.Value);
        Assert.Equal("{Binding PresenceLabel}", presenceDot.Attribute("SemanticProperties.Description")?.Value);
        Assert.DoesNotContain("UserStatus", source.ToString(), StringComparison.Ordinal);

        var searchIcon = source.Descendants()
            .Single(element => element.Name.LocalName == "Image" && element.Attribute("Source")?.Value == "icon_search.png");
        Assert.Equal("{Binding ShowConversationSearchIcon}", searchIcon.Attribute("IsVisible")?.Value);

        var highlightedText = source.Descendants()
            .Where(element => element.Name.LocalName is "SearchHighlightLabel" or "MessageEmojiLabel")
            .ToArray();
        Assert.Equal(2, highlightedText.Length);
        Assert.Contains(highlightedText, label => label.Attribute("SourceText")?.Value == "{Binding Title}");
        var summary = Assert.Single(highlightedText, label => label.Attribute("Text")?.Value == "{Binding Detail}");
        Assert.Equal("MessageEmojiLabel", summary.Name.LocalName);
        Assert.Contains("ViewModel.RealmEmojis", summary.Attribute("RealmEmojis")?.Value, StringComparison.Ordinal);
        Assert.Equal("True", summary.Attribute("InputTransparent")?.Value);
        Assert.Equal("False", summary.Attribute("IsTextSelectionEnabled")?.Value);
        Assert.Equal("1", summary.Attribute("MaxLines")?.Value);
        Assert.All(highlightedText, label =>
            Assert.Contains("ViewModel.ConversationFilterQuery", label.Attribute("HighlightQuery")?.Value, StringComparison.Ordinal));
        var messageMatchLabel = source.Descendants()
            .Single(element => element.Name.LocalName == "Label" && element.Attribute("Text")?.Value == "消息");
        Assert.Equal("{Binding IsSearchMessageMatch}", messageMatchLabel.Attribute("IsVisible")?.Value);
        Assert.Equal("{StaticResource MutedLabelStyle}", messageMatchLabel.Attribute("Style")?.Value);

        var conversationRow = conversationList.Descendants()
            .First(element => element.Name.LocalName == "Border" &&
                              element.Attribute("Opacity")?.Value == "{Binding ItemOpacity}");
        var hoverTrigger = conversationRow.Descendants()
            .Single(element => element.Name.LocalName == "MultiTrigger");
        var hoverConditions = hoverTrigger.Descendants()
            .Where(element => element.Name.LocalName == "BindingCondition")
            .ToArray();
        Assert.Contains(hoverConditions, condition =>
            condition.Attribute("Binding")?.Value == "{Binding IsPointerOver}" && condition.Attribute("Value")?.Value == "True");
        Assert.Contains(hoverConditions, condition =>
            condition.Attribute("Binding")?.Value == "{Binding IsSelected}" && condition.Attribute("Value")?.Value == "False");
        Assert.Equal("{StaticResource SurfaceHoverBrush}", hoverTrigger.Descendants()
            .Single(element => element.Name.LocalName == "Setter" && element.Attribute("Property")?.Value == "Background")
            .Attribute("Value")?.Value);
        var selectedTrigger = conversationRow.Descendants()
            .Single(element => element.Name.LocalName == "DataTrigger" &&
                               element.Attribute("Binding")?.Value == "{Binding IsSelected}");
        Assert.Equal("{StaticResource SurfaceSelectedBrush}", selectedTrigger.Descendants()
            .Single(element => element.Name.LocalName == "Setter" && element.Attribute("Property")?.Value == "Background")
            .Attribute("Value")?.Value);
        var pointerGesture = conversationRow.Descendants()
            .Single(element => element.Name.LocalName == "PointerGestureRecognizer");
        Assert.Equal("OnConversationPointerEntered", pointerGesture.Attribute("PointerEntered")?.Value);
        Assert.Equal("OnConversationPointerExited", pointerGesture.Attribute("PointerExited")?.Value);

        var rowGrid = conversationRow.Descendants()
            .First(element => element.Name.LocalName == "Grid" &&
                              element.Attribute("RowDefinitions")?.Value == "Auto,Auto");
        Assert.Equal("42,*,Auto", rowGrid.Attribute("ColumnDefinitions")?.Value);
        Assert.Equal("9", rowGrid.Attribute("ColumnSpacing")?.Value);
        var avatarFrame = rowGrid.Elements()
            .First(element => element.Name.LocalName == "Border" &&
                              element.Attribute("Grid.RowSpan")?.Value == "2");
        Assert.Equal("40", avatarFrame.Attribute("WidthRequest")?.Value);
        Assert.Equal("End", avatarFrame.Attribute("HorizontalOptions")?.Value);
    }

    [Fact]
    public void ConversationRow_WhenRightClicked_UsesTargetedNativeMenuWithoutActivatingRow()
    {
        var xaml = File.ReadAllText(FindWorkspaceFile("src", "RelayCove.App", "Controls", "ConversationPaneView.xaml"));
        Assert.Contains("<windowsBehaviors:ConversationContextBehavior />", xaml);
        var source = File.ReadAllText(FindWorkspaceFile("src", "RelayCove.App", "Platforms", "Windows", "Behaviors", "ConversationContextBehavior.cs"));
        Assert.Contains("AddHandler(WinUiElement.RightTappedEvent, _rightTappedHandler, true)", source);
        Assert.Contains("Command = viewModel.ToggleConversationPinnedCommand", source);
        Assert.Contains("Command = viewModel.ToggleConversationMutedCommand", source);
        Assert.Contains("Command = viewModel.DeleteConversationCommand", source);
        Assert.Contains("CommandParameter = target", source);
        Assert.Contains("args.GetPosition(_platformView)", source);
        Assert.DoesNotContain("ActivateConversation(", source);
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
