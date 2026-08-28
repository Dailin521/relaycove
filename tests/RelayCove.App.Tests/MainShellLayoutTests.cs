using System.Xml.Linq;

namespace RelayCove.App.Tests;

public sealed class MainShellLayoutTests
{
    [Fact]
    public void LoginPage_WhenRendered_SeparatesRealmLoginAndOfficialRegistration()
    {
        var source = File.ReadAllText(FindWorkspaceFile("src", "RelayCove.App", "MainPage.xaml"));

        Assert.Contains("Title=\"RichChat\"", source, StringComparison.Ordinal);
        Assert.Contains("Source=\"richchat_mark.png\"", source, StringComparison.Ordinal);
        Assert.Contains("Text=\"欢迎使用 RichChat\"", source, StringComparison.Ordinal);
        Assert.Contains("Text=\"Zulip Realm\"", source, StringComparison.Ordinal);
        Assert.Contains("Text=\"前往 Zulip 官方注册\"", source, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding OpenRegistrationCommand}\"", source, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding LoginCommand.IsRunning}\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Stage 21 的人工密码登录", source, StringComparison.Ordinal);
    }

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
    public void AccountMenu_WhenStatusIsAvailable_ShowsReadOnlySummaryWithoutWriteActions()
    {
        var source = File.ReadAllText(FindWorkspaceFile("src", "RelayCove.App", "MainPage.xaml"));

        Assert.Contains("AutomationId=\"AccountStatusSummary\"", source, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding OwnStatusSummary}\"", source, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding HasOwnStatusSummary}\"", source, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"AccountMenuOwnPresenceDot\"", source, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding HasOwnPresenceStatus}\"", source, StringComparison.Ordinal);
        Assert.Contains("Background=\"{Binding OwnPresenceBrush}\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SetOwnPresence", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SetOwnUserStatus", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ClearOwnUserStatus", source, StringComparison.Ordinal);
        Assert.DoesNotContain("正在更新我的状态", source, StringComparison.Ordinal);
        Assert.DoesNotContain("正在更新个人状态", source, StringComparison.Ordinal);
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
        var componentStyles = File.ReadAllText(FindWorkspaceFile("src", "RelayCove.App", "Resources", "Styles", "ComponentStyles.xaml"));
        var nativeButtonBehavior = File.ReadAllText(FindWorkspaceFile(
            "src", "RelayCove.App", "Platforms", "Windows", "Behaviors", "ProductBarButtonBehavior.cs"));
        var mainPage = File.ReadAllText(FindWorkspaceFile("src", "RelayCove.App", "MainPage.xaml"));
        var mainDocument = XDocument.Parse(mainPage);
        XNamespace maui = "http://schemas.microsoft.com/dotnet/2021/maui";
        XNamespace x = "http://schemas.microsoft.com/winfx/2009/xaml";

        Assert.Contains("Title=\"RichChat\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Subtitle=", source, StringComparison.Ordinal);
        Assert.DoesNotContain("WorkspaceDisplayName", source, StringComparison.Ordinal);
        Assert.Equal(4, source.Split("Style=\"{StaticResource ProductBarImageButtonStyle}\"", StringSplitOptions.None).Length - 1);
        Assert.Contains("<HorizontalStackLayout Spacing=\"6\">", source, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"ProductBarImageButtonStyle\"", componentStyles, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"PointerOver\"", componentStyles, StringComparison.Ordinal);
        Assert.Equal(4, source.Split("<windowsBehaviors:ProductBarButtonBehavior />", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("PointerGestureRecognizer", source, StringComparison.Ordinal);
        Assert.DoesNotContain("LightSurfaceSelectedColor", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IsDownloadCenterOpen", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IsSettingsSection", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IsPinned", source, StringComparison.Ordinal);
        Assert.Contains("platformView.PointerEntered += OnPointerEntered;", nativeButtonBehavior, StringComparison.Ordinal);
        Assert.Contains("platformView.PointerExited += OnPointerExited;", nativeButtonBehavior, StringComparison.Ordinal);
        Assert.Contains("NormalOpacity = 0.72d", nativeButtonBehavior, StringComparison.Ordinal);
        Assert.Contains("HoverOpacity = 1d", nativeButtonBehavior, StringComparison.Ordinal);
        Assert.Contains("PressedOpacity = 0.55d", nativeButtonBehavior, StringComparison.Ordinal);
        Assert.Contains("Microsoft.UI.Colors.Transparent", nativeButtonBehavior, StringComparison.Ordinal);
        Assert.DoesNotContain("button.Resources[", nativeButtonBehavior, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"AccountButton\"", source);
        Assert.Contains("SourceUrl=\"{Binding CurrentUserAvatarUrl}\"", source);
        Assert.Contains("x:Name=\"ProductBarOwnPresenceDot\"", source);
        Assert.Contains("IsVisible=\"{Binding HasOwnPresenceStatus}\"", source);
        Assert.Contains("Background=\"{Binding OwnPresenceBrush}\"", source);
        Assert.Contains("nameof(ShellViewModel.OwnPresenceBrush)", codeBehind);
        Assert.Contains("Command=\"{Binding ToggleAccountMenuCommand}\"", source);
        var leadingContentStart = source.IndexOf("<TitleBar.LeadingContent>", StringComparison.Ordinal);
        var leadingContentEnd = source.IndexOf("</TitleBar.LeadingContent>", StringComparison.Ordinal);
        var accountButtonIndex = source.IndexOf("x:Name=\"AccountButtonBorder\"", StringComparison.Ordinal);
        Assert.InRange(accountButtonIndex, leadingContentStart, leadingContentEnd);
        Assert.DoesNotContain("Text=\"R\"", source);
        Assert.Contains("x:Name=\"DownloadButton\"", source);
        Assert.Contains("Source=\"icon_download.png\"", source);
        Assert.DoesNotContain("<ProgressBar", source, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"CompletedDownloadDot\"", source);
        Assert.Contains("x:Name=\"FailedDownloadDot\"", source);
        Assert.Contains("SemanticProperties.Description=\"存在未查看的下载完成\"", source);
        Assert.Contains("SemanticProperties.Description=\"存在未查看的下载失败\"", source);
        Assert.DoesNotContain("IsVisible=\"{Binding HasDownloadButtonAttention}\"", source);
        Assert.Contains("CompletedDownloadDot.IsVisible = _viewModel?.HasUnseenCompletedDownloads == true;", codeBehind);
        Assert.Contains("FailedDownloadDot.IsVisible = _viewModel?.HasUnseenDownloadFailure == true;", codeBehind);
        Assert.Contains("Dispatcher.Dispatch(SynchronizeDownloadAttention);", codeBehind);
        Assert.Contains("DownloadButton.Command = viewModel.ToggleDownloadCenterCommand;", codeBehind);
        Assert.True(
            source.IndexOf("x:Name=\"DownloadButton\"", StringComparison.Ordinal) <
            source.IndexOf("x:Name=\"SettingsButton\"", StringComparison.Ordinal));
        Assert.Contains("x:Name=\"SettingsButton\"", source);
        Assert.Contains("Source=\"icon_settings.png\"", source);
        Assert.Contains("SettingsButton.Command = viewModel.ToggleSettingsCommand;", codeBehind);
        var accountMenuButton = mainDocument
            .Descendants(maui + "Button")
            .Single(element => element.Attribute(x + "Name")?.Value == "FirstAccountMenuButton");
        Assert.Equal("Start", accountMenuButton.Ancestors(maui + "Border").First().Attribute("HorizontalOptions")?.Value);
    }

    [Fact]
    public void DownloadCenter_WhenRendered_ProvidesBrowserStyleProgressHistoryAndFileActions()
    {
        var source = File.ReadAllText(FindWorkspaceFile("src", "RelayCove.App", "MainPage.xaml"));

        Assert.Contains("IsVisible=\"{Binding IsDownloadCenterOpen}\"", source, StringComparison.Ordinal);
        Assert.Contains("Text=\"下载内容\"", source, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding ShowDownloadCenterCurrentTask}\"", source, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding RecentDownloads}\"", source, StringComparison.Ordinal);
        Assert.Contains("Text=\"打开下载文件夹\"", source, StringComparison.Ordinal);
        Assert.Contains("Text=\"清除记录\"", source, StringComparison.Ordinal);
        Assert.Contains("Text=\"在文件夹中显示\"", source, StringComparison.Ordinal);
        Assert.Contains("Text=\"从记录中移除\"", source, StringComparison.Ordinal);
        Assert.Contains("FlyoutBase.ContextFlyout", source, StringComparison.Ordinal);
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
        Assert.Equal(3, source.Descendants().Count(element =>
            element.Name.LocalName == "HorizontalDragScrollBehavior"));
        var raw = source.ToString();
        Assert.DoesNotContain("选择后插入光标位置", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("再次选择可移除", raw, StringComparison.Ordinal);
    }

    [Fact]
    public void SearchResults_WhenRendered_HighlightTitleAndSubtitleWithCurrentQuery()
    {
        var source = XDocument.Load(FindWorkspaceFile("src", "RelayCove.App", "MainPage.xaml"));

        var labels = source.Descendants()
            .Where(element => element.Name.LocalName == "SearchHighlightLabel")
            .ToArray();

        Assert.Equal(2, labels.Length);
        Assert.All(labels, label =>
            Assert.Contains("SearchQuery", label.Attribute("HighlightQuery")?.Value, StringComparison.Ordinal));
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
            .Single(element => element.Attribute("ItemsSource")?.Value?.Contains("MessageItems", StringComparison.Ordinal) == true);

        Assert.Single(
            messageBody.Descendants(),
            element => element.Name.LocalName == "SelectableTextBehavior");
        Assert.Single(
            source.Descendants(),
            element => element.Name.LocalName == "SelectableTextBehavior");
        Assert.Equal("None", messageCollection.Attribute("SelectionMode")?.Value);
    }

    [Fact]
    public void DownloadStatus_WhenRendered_ExposesProgressCancelAndRetryWithoutBlockingComposer()
    {
        var source = XDocument.Load(FindWorkspaceFile("src", "RelayCove.App", "MainPage.xaml"));
        XNamespace maui = "http://schemas.microsoft.com/dotnet/2021/maui";

        var status = source.Descendants(maui + "Border")
            .Single(element => element.Attribute("IsVisible")?.Value == "{Binding IsMediaDownloadStatusVisible}");

        Assert.Contains(status.Descendants(maui + "ProgressBar"), progress =>
            progress.Attribute("Progress")?.Value == "{Binding MediaDownloadProgress}");
        Assert.Contains(status.Descendants(maui + "ActivityIndicator"), indicator =>
            indicator.Attribute("IsRunning")?.Value == "{Binding IsMediaDownloadIndeterminate}");
        Assert.Contains(status.Descendants(maui + "Button"), button =>
            button.Attribute("Command")?.Value == "{Binding DownloadAttachmentCancelCommand}");
        Assert.Contains(status.Descendants(maui + "Button"), button =>
            button.Attribute("Command")?.Value == "{Binding RetryMediaDownloadCommand}");
        Assert.Contains(source.Descendants(), element => element.Name.LocalName == "ComposerView" &&
            element.Attribute("Grid.Row")?.Value == "3");
    }

    [Fact]
    public void ComposerAttachments_WhenUploading_ShowPerFileProgress()
    {
        var source = XDocument.Load(FindWorkspaceFile("src", "RelayCove.App", "Controls", "ComposerView.xaml"));
        XNamespace maui = "http://schemas.microsoft.com/dotnet/2021/maui";

        var progress = source.Descendants(maui + "ProgressBar")
            .Single(element => element.Attribute("Progress")?.Value == "{Binding UploadProgress}");

        Assert.Equal("{Binding IsUploading}", progress.Attribute("IsVisible")?.Value);
        Assert.Equal("{StaticResource AccentColor}", progress.Attribute("ProgressColor")?.Value);
    }

    [Fact]
    public void MessageListActivation_WhenPositioningCachedConversation_KeepsCollectionVisible()
    {
        var source = File.ReadAllText(FindWorkspaceFile(
            "src",
            "RelayCove.App",
            "Controls",
            "MessageListView.xaml.cs"));
        var hostSource = File.ReadAllText(FindWorkspaceFile(
            "src",
            "RelayCove.App",
            "Controls",
            "ConversationMessageHost.cs"));
        var mainPage = File.ReadAllText(FindWorkspaceFile("src", "RelayCove.App", "MainPage.xaml"));

        Assert.DoesNotContain("MessageCollection.Opacity", source, StringComparison.Ordinal);
        Assert.Contains("MessageCollection.InputTransparent = isPositioning;", source, StringComparison.Ordinal);
        Assert.Contains("<controls:ConversationMessageHost Grid.Row=\"1\"", mainPage, StringComparison.Ordinal);
        Assert.Contains("Presentations=\"{Binding MessagePresentations}\"", mainPage, StringComparison.Ordinal);
        Assert.DoesNotContain("BindableLayout.ItemsSource=\"{Binding MessagePresentations}\"", mainPage, StringComparison.Ordinal);
        Assert.Contains("view.IsVisible = isActive;", hostSource, StringComparison.Ordinal);
        Assert.Contains("view.InputTransparent = !isActive;", hostSource, StringComparison.Ordinal);
    }

    [Fact]
    public void MessageListHistory_WhenRendered_UsesScrollPaginationWithoutManualHistoryPrompts()
    {
        var source = File.ReadAllText(FindWorkspaceFile(
            "src",
            "RelayCove.App",
            "Controls",
            "MessageListView.xaml"));

        Assert.DoesNotContain("Text=\"加载更早消息\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"已到最早消息\"", source, StringComparison.Ordinal);
        Assert.Contains("Scrolled=\"OnMessageCollectionScrolled\"", source, StringComparison.Ordinal);
        Assert.Contains("ItemsUpdatingScrollMode=\"KeepScrollOffset\"", source, StringComparison.Ordinal);

        var codeBehind = File.ReadAllText(FindWorkspaceFile(
            "src",
            "RelayCove.App",
            "Controls",
            "MessageListView.xaml.cs"));
        Assert.Contains("Properties.MouseWheelDelta", codeBehind, StringComparison.Ordinal);
        Assert.Contains("if (_topHistoryLoadLatched && wheelDelta != 0)", codeBehind, StringComparison.Ordinal);
        Assert.Contains("_viewModel.IsLoadingOlder ||", codeBehind, StringComparison.Ordinal);
        Assert.Contains("ResetTopHistoryLoadState();", codeBehind, StringComparison.Ordinal);
        Assert.Contains("if (!_viewModel.IsLoadingOlder &&", codeBehind, StringComparison.Ordinal);
        Assert.Contains("CaptureTopHistoryAnchor();", codeBehind, StringComparison.Ordinal);
        Assert.Contains("const int anchorIndex = 0;", codeBehind, StringComparison.Ordinal);
        Assert.Contains("_pendingPrependAnchorId = _firstVisibleMessageId;", codeBehind, StringComparison.Ordinal);
        Assert.Contains("RestorePrependAnchor(anchorId);", codeBehind, StringComparison.Ordinal);
        Assert.Contains("RequestOlderFromTopInputAsync", codeBehind, StringComparison.Ordinal);
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
