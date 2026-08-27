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
        Assert.Equal("{Binding OpenNewConversationCommand}", createButton.Attribute("Command")?.Value);
        Assert.DoesNotContain("频道", labels);
        Assert.DoesNotContain("私信", labels);
        Assert.DoesNotContain(source.Descendants(), element => element.Attribute("ItemsSource")?.Value is "{Binding FilteredChannels}" or "{Binding FilteredDirectMessages}");
        var presenceDot = source.Descendants()
            .Single(element => element.Name.LocalName == "Border" &&
                               element.Attribute("IsVisible")?.Value == "{Binding HasPresence}");
        Assert.Equal("{Binding PresenceBrush}", presenceDot.Attribute("Background")?.Value);
        Assert.Equal("{Binding PresenceLabel}", presenceDot.Attribute("SemanticProperties.Description")?.Value);
        var userStatusGlyph = source.Descendants()
            .Single(element => element.Name.LocalName == "Label" &&
                               element.Attribute("Text")?.Value == "{Binding UserStatusGlyph}");
        Assert.Equal("{Binding HasUserStatusGlyph}", userStatusGlyph.Attribute("IsVisible")?.Value);
        Assert.Equal("{Binding UserStatusDescription}", userStatusGlyph.Attribute("ToolTipProperties.Text")?.Value);

        var searchIcon = source.Descendants()
            .Single(element => element.Name.LocalName == "Image" && element.Attribute("Source")?.Value == "icon_search.png");
        Assert.Equal("{Binding ShowConversationSearchIcon}", searchIcon.Attribute("IsVisible")?.Value);

        var highlightedText = source.Descendants()
            .Where(element => element.Name.LocalName == "SearchHighlightLabel")
            .ToArray();
        Assert.Equal(2, highlightedText.Length);
        Assert.Contains(highlightedText, label => label.Attribute("SourceText")?.Value == "{Binding Title}");
        Assert.Contains(highlightedText, label => label.Attribute("SourceText")?.Value == "{Binding Detail}");
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
