using System.Xml.Linq;

namespace RelayCove.App.Tests;

public sealed class MainShellLayoutTests
{
    [Fact]
    public void QuoteCard_WhenAttachmentsExist_UsesControlledThumbnailsAndNamedFileCards()
    {
        var page = XDocument.Load(FindWorkspaceFile("src", "RelayCove.App", "Controls", "MessageListView.xaml"));
        XNamespace maui = "http://schemas.microsoft.com/dotnet/2021/maui";
        XNamespace controls = "clr-namespace:RelayCove.App.Controls";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2009/xaml";
        var quote = page.Descendants(maui + "DataTemplate")
            .Single(element => element.Attribute(xaml + "DataType")?.Value == "viewModels:MessageQuote");
        var attachments = Assert.Single(quote.Descendants(), element =>
            element.Attribute("BindableLayout.ItemsSource")?.Value == "{Binding Attachments}");
        var image = Assert.Single(attachments.Descendants(controls + "RealmMediaImageView"));

        Assert.Equal("{Binding ImageSourceUrl}", image.Attribute("SourceUrl")?.Value);
        Assert.Equal("AspectFit", image.Attribute("Aspect")?.Value);
        Assert.Equal("{Binding IsImage}", image.Attribute("IsVisible")?.Value);
        Assert.NotNull(image.Attribute("WidthRequest"));
        Assert.NotNull(image.Attribute("HeightRequest"));
        var file = Assert.Single(attachments.Descendants(), element =>
            element.Attribute("IsVisible")?.Value == "{Binding IsFile}");
        Assert.Contains(file.Descendants(maui + "Label"), label => label.Attribute("Text")?.Value == "{Binding Name}");
        Assert.Contains(file.Descendants(maui + "Image"), icon => icon.Attribute("Source")?.Value == "icon_attachment.png");
    }

    [Fact]
    public void DetailsOverlay_WhenBackgroundRemainsEnabled_CoversWorkspaceWithDismissBackdrop()
    {
        XNamespace maui = "http://schemas.microsoft.com/dotnet/2021/maui";
        XNamespace controls = "clr-namespace:RelayCove.App.Controls";
        var source = XDocument.Load(FindWorkspaceFile("src", "RelayCove.App", "MainPage.xaml"));
        var overlay = source.Descendants(maui + "Grid").Single(element =>
            element.Attribute("IsVisible")?.Value == "{Binding IsOverlayDetailsVisible}");
        var backdrop = Assert.Single(overlay.Elements(maui + "BoxView"));
        Assert.Equal("{StaticResource OverlayBrush}", backdrop.Attribute("Background")?.Value);
        Assert.NotEqual("True", backdrop.Attribute("InputTransparent")?.Value);
        Assert.Null(backdrop.Attribute("WidthRequest"));
        Assert.Null(backdrop.Attribute("HeightRequest"));
        Assert.Contains(backdrop.Descendants(maui + "TapGestureRecognizer"), gesture =>
            gesture.Attribute("Command")?.Value == "{Binding ToggleDetailsCommand}");
        var workspace = overlay.Parent!.Elements(maui + "Grid").Single(element =>
            element.Attribute("IsEnabled")?.Value == "{Binding IsPrimaryShellEnabled}");
        Assert.True(workspace.IsBefore(overlay));
        Assert.DoesNotContain(overlay.Ancestors(), element => ReferenceEquals(element, workspace));
        Assert.Contains(overlay.Descendants(controls + "DetailsPaneView"), pane =>
            pane.Attribute("IsModal")?.Value == "True");
    }

    [Fact]
    public void MessageList_WhenRendered_KeepsStarIndicatorAndFailureFeedback()
    {
        XNamespace maui = "http://schemas.microsoft.com/dotnet/2021/maui";
        var source = XDocument.Load(FindWorkspaceFile("src", "RelayCove.App", "Controls", "MessageListView.xaml"));

        Assert.Contains(source.Descendants(maui + "Label"), label =>
            label.Attribute("IsVisible")?.Value == "{Binding IsStarred}" &&
            label.Attribute("Text")?.Value == "★");
        Assert.Contains(source.Descendants(maui + "Label"), label =>
            label.Attribute("Text")?.Value == "{Binding MutationState}" &&
            label.Attribute("IsVisible")?.Value == "{Binding HasMutationState}");
    }

    [Theory]
    [InlineData("ProductBarView.xaml", "{Binding HasOwnPresenceStatus}")]
    [InlineData("ConversationPaneView.xaml", "{Binding HasPresence}")]
    public void PresenceIndicator_WhenShown_IsInsetFromAvatarEdges(string file, string visibility)
    {
        XNamespace maui = "http://schemas.microsoft.com/dotnet/2021/maui";
        var source = XDocument.Load(FindWorkspaceFile("src", "RelayCove.App", "Controls", file));
        var dot = source.Descendants(maui + "Border").Single(element => element.Attribute("IsVisible")?.Value == visibility);
        Assert.Equal("0,0,3,3", dot.Attribute("Margin")?.Value);
    }

    [Fact]
    public void AccountMenu_WhenRendered_OffersPencilBesideNameAndInlineEditor()
    {
        XNamespace maui = "http://schemas.microsoft.com/dotnet/2021/maui";
        var source = XDocument.Load(FindWorkspaceFile("src", "RelayCove.App", "MainPage.xaml"));
        var edit = source.Descendants(maui + "ImageButton").Single(element => element.Attribute("AutomationId")?.Value == "EditOwnNameButton");
        Assert.Equal("1", edit.Attribute("Grid.Column")?.Value);
        Assert.Equal("icon_edit.png", edit.Attribute("Source")?.Value);
        Assert.Equal("{Binding EditOwnNameCommand}", edit.Attribute("Command")?.Value);
        Assert.Contains(edit.Parent!.Elements(maui + "Label"), label => label.Attribute("Text")?.Value == "{Binding CurrentUserDisplayName}");
        var entry = source.Descendants(maui + "Entry").Single(element => element.Attribute("AutomationId")?.Value == "OwnNameEntry");
        Assert.Equal("{Binding OwnNameDraft}", entry.Attribute("Text")?.Value);
        Assert.Equal("{Binding SaveOwnNameCommand}", entry.Attribute("ReturnCommand")?.Value);
    }

    [Fact]
    public void AccountMenu_WhenRendered_OffersAvatarUploadBesideIdentity()
    {
        XNamespace maui = "http://schemas.microsoft.com/dotnet/2021/maui";
        var source = XDocument.Load(FindWorkspaceFile("src", "RelayCove.App", "MainPage.xaml"));
        var upload = source.Descendants(maui + "Button").Single(element => element.Attribute("AutomationId")?.Value == "UploadOwnAvatarButton");
        Assert.Equal("2", upload.Attribute("Grid.Column")?.Value);
        Assert.Equal("{Binding UploadAvatarCommand}", upload.Attribute("Command")?.Value);
        Assert.Contains(upload.Parent!.Descendants(maui + "Label"), element => element.Attribute("Text")?.Value == "{Binding CurrentUserDisplayName}");
        Assert.Contains(source.Descendants(maui + "Label"), element => element.Attribute("Text")?.Value == "{Binding AvatarUploadStatus}");
    }

    [Theory]
    [InlineData("MainPage.xaml", 2)]
    [InlineData("Controls/ProductBarView.xaml", 1)]
    [InlineData("Controls/ConversationPaneView.xaml", 2)]
    [InlineData("Controls/DetailsPaneView.xaml", 2)]
    [InlineData("Controls/MessageListView.xaml", 1)]
    [InlineData("Controls/ContactsPageView.xaml", 1)]
    [InlineData("Controls/NavigationRailView.xaml", 1)]
    public void Avatars_WhenRendered_UseBlueFallbackAndPrioritizeCustomImage(string relativePath, int expectedCount)
    {
        XNamespace maui = "http://schemas.microsoft.com/dotnet/2021/maui";
        XNamespace controls = "clr-namespace:RelayCove.App.Controls";
        XNamespace x = "http://schemas.microsoft.com/winfx/2009/xaml";
        var source = XDocument.Load(FindWorkspaceFile("src", "RelayCove.App", relativePath));
        var avatars = source.Descendants(controls + "RealmMediaImageView")
            .Where(element => element.Attribute("MediaKind")?.Value == "Avatar").ToArray();

        Assert.Equal(expectedCount, avatars.Length);
        foreach (var avatar in avatars)
        {
            var initial = Assert.Single(avatar.Parent!.Elements(maui + "Label"));
            var frame = avatar.Ancestors(maui + "Border").First();
            Assert.Equal("{StaticResource AccentBrush}", frame.Attribute("Background")?.Value);
            Assert.DoesNotContain(frame.Element(maui + "Border.Triggers")?.Descendants(maui + "Setter") ?? [],
                setter => setter.Attribute("Property")?.Value is "Background" or "BackgroundColor");
            var avatarName = avatar.Attribute(x + "Name")?.Value;
            Assert.False(string.IsNullOrWhiteSpace(avatarName));
            Assert.Equal(
                $"{{Binding IsFallbackVisible, Source={{x:Reference {avatarName}}}, x:DataType=controls:RealmMediaImageView}}",
                initial.Attribute("IsVisible")?.Value);
        }
    }

    [Fact]
    public void GeneratedAvatars_WhenShownInConversationAndTitleBar_UseAccountMenuStyle()
    {
        XNamespace maui = "http://schemas.microsoft.com/dotnet/2021/maui";
        XNamespace x = "http://schemas.microsoft.com/winfx/2009/xaml";
        var menu = XDocument.Load(FindWorkspaceFile("src", "RelayCove.App", "MainPage.xaml"));
        var titleBar = XDocument.Load(FindWorkspaceFile("src", "RelayCove.App", "Controls", "ProductBarView.xaml"));
        var conversations = XDocument.Load(FindWorkspaceFile("src", "RelayCove.App", "Controls", "ConversationPaneView.xaml"));
        var menuInitial = menu.Descendants(maui + "Label").Single(element =>
            element.Attribute("Text")?.Value == "{Binding CurrentUserInitial}");
        var titleInitial = titleBar.Descendants(maui + "Label").Single(element =>
            element.Attribute("Text")?.Value == "{Binding CurrentUserInitial}");
        var conversationInitials = conversations.Descendants(maui + "Label").Where(element =>
            element.Attribute("Text")?.Value == "{Binding Initial}" &&
            !element.Ancestors(maui + "DataTemplate").Any(template =>
                template.Attribute(x + "DataType")?.Value == "viewModels:ConversationAvatarTile"));

        foreach (var initial in conversationInitials.Append(titleInitial).Append(menuInitial))
        {
            Assert.Equal("{StaticResource AvatarInitialLabelStyle}", initial.Attribute("Style")?.Value);
            Assert.Null(initial.Attribute("FontSize"));
            var frame = initial.Ancestors(maui + "Border").First();
            Assert.Equal("{StaticResource AccentBrush}", frame.Attribute("Background")?.Value);
            var shape = frame.Element(maui + "Border.StrokeShape")!.Element(maui + "RoundRectangle")!;
            Assert.Equal(double.Parse(frame.Attribute("WidthRequest")!.Value) / 2,
                double.Parse(shape.Attribute("CornerRadius")!.Value));
        }
    }

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
        Assert.Contains("Text=\"忘记密码？\"", source, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding OpenPasswordResetCommand}\"", source, StringComparison.Ordinal);
        Assert.Contains("SemanticProperties.Hint=\"在浏览器中打开当前服务器的密码重置页面，通过注册邮箱找回密码\"", source, StringComparison.Ordinal);
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
    public void NewPrivateGroupDialog_WhenErrorShown_ReservesWrappedErrorRowAboveButtonsOutsideMemberList()
    {
        var source = XDocument.Load(FindWorkspaceFile("src", "RelayCove.App", "MainPage.xaml"));
        XNamespace maui = "http://schemas.microsoft.com/dotnet/2021/maui";
        var error = source.Descendants(maui + "Label").Single(element =>
            element.Attribute("Text")?.Value == "{Binding NewConversationError}");
        var feedback = error.Parent!;
        var layout = feedback.Parent!;
        var choices = layout.Descendants(maui + "CollectionView").Single(element =>
            element.Attribute("ItemsSource")?.Value == "{Binding NewConversationChoices}");
        var createButton = layout.Descendants(maui + "Button").Single(element =>
            element.Attribute("Command")?.Value == "{Binding StartNewChannelConversationCommand}");
        var errorRow = int.Parse(feedback.Attribute("Grid.Row")!.Value);
        var rowDefinitions = layout.Attribute("RowDefinitions")!.Value.Split(',');

        Assert.Same(layout, choices.Parent!.Parent);
        Assert.Same(layout, createButton.Parent!.Parent);
        Assert.True(int.Parse(choices.Parent.Attribute("Grid.Row")!.Value) < errorRow);
        Assert.True(int.Parse(createButton.Parent.Attribute("Grid.Row")!.Value) > errorRow);
        Assert.Equal("Auto", rowDefinitions[errorRow]);
        Assert.Equal("{Binding HasNewConversationError}", error.Attribute("IsVisible")?.Value);
        Assert.Equal("WordWrap", error.Attribute("LineBreakMode")?.Value);
        Assert.Null(error.Attribute("MaxLines"));
        Assert.Null(choices.Parent.Attribute("MinimumHeightRequest"));
        var disabledReason = layout.Descendants(maui + "Label").Single(element =>
            element.Attribute("Text")?.Value == "{Binding PrivateGroupCreateDisabledReason}");
        Assert.Same(feedback, disabledReason.Parent);
        Assert.Equal("{Binding ShowPrivateGroupCreateDisabledReason}", disabledReason.Attribute("IsVisible")?.Value);
        Assert.Equal("WordWrap", disabledReason.Attribute("LineBreakMode")?.Value);
    }

    [Fact]
    public void NewConversationDialog_WhenRendered_UsesRowSelectionForPrivateAndRightCheckboxForGroup()
    {
        var source = XDocument.Load(FindWorkspaceFile("src", "RelayCove.App", "MainPage.xaml"));
        XNamespace maui = "http://schemas.microsoft.com/dotnet/2021/maui";
        var choices = source.Descendants(maui + "CollectionView").Single(element =>
            element.Attribute("ItemsSource")?.Value == "{Binding NewConversationChoices}");
        Assert.Empty(choices.Descendants(maui + "RadioButton"));
        var row = Assert.Single(choices.Descendants(maui + "DataTemplate")).Element(maui + "Border")!;
        var tap = Assert.Single(row.Descendants(maui + "TapGestureRecognizer"));
        Assert.Contains("SelectNewDirectConversationContactCommand", tap.Attribute("Command")?.Value, StringComparison.Ordinal);
        Assert.Equal("{Binding .}", tap.Attribute("CommandParameter")?.Value);
        Assert.Equal("Transparent", row.Attribute("Background")?.Value);
        var selectedTrigger = Assert.Single(row.Descendants(maui + "DataTrigger"));
        Assert.Equal("{Binding IsSelected}", selectedTrigger.Attribute("Binding")?.Value);
        Assert.Equal("True", selectedTrigger.Attribute("Value")?.Value);
        var highlight = Assert.Single(selectedTrigger.Elements(maui + "Setter"));
        Assert.Equal("Background", highlight.Attribute("Property")?.Value);
        Assert.Equal("{StaticResource SurfaceSelectedBrush}", highlight.Attribute("Value")?.Value);
        var checkbox = Assert.Single(choices.Descendants(maui + "CheckBox"));
        Assert.Equal("Fill", choices.Attribute("HorizontalOptions")?.Value);
        Assert.Equal("Fill", row.Attribute("HorizontalOptions")?.Value);
        Assert.Equal("Fill", checkbox.Parent?.Attribute("HorizontalOptions")?.Value);
        Assert.Equal("32,*,32", checkbox.Parent?.Attribute("ColumnDefinitions")?.Value);
        Assert.Equal("2", checkbox.Attribute("Grid.Column")?.Value);
        Assert.Equal("32", checkbox.Attribute("WidthRequest")?.Value);
        Assert.Equal("32", checkbox.Attribute("MinimumWidthRequest")?.Value);
        Assert.Equal("32", checkbox.Attribute("MaximumWidthRequest")?.Value);
        Assert.Equal("End", checkbox.Attribute("HorizontalOptions")?.Value);
        var name = choices.Descendants(maui + "Label").Single(element => element.Attribute("Text")?.Value == "{Binding Name}");
        Assert.Equal("1", name.Attribute("MaxLines")?.Value);
        Assert.Equal("TailTruncation", name.Attribute("LineBreakMode")?.Value);
        Assert.Equal("{Binding IsSelected, Mode=TwoWay}", checkbox.Attribute("IsChecked")?.Value);
        Assert.Contains("IsNewChannelConversationMode", checkbox.Attribute("IsVisible")?.Value, StringComparison.Ordinal);
        Assert.DoesNotContain(source.Descendants(maui + "Button"), button =>
            button.Attribute("Command")?.Value is "{Binding ShowNewDirectConversationCommand}" or
                "{Binding ShowNewChannelConversationCommand}" or "{Binding StartSelfConversationCommand}");
    }

    [Fact]
    public void MessageMenu_WhenCopyIsAvailable_UsesTheSharedCopyIconAndCommand()
    {
        var source = XDocument.Load(FindWorkspaceFile("src", "RelayCove.App", "MainPage.xaml"));
        XNamespace maui = "http://schemas.microsoft.com/dotnet/2021/maui";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2009/xaml";

        var copy = Assert.Single(source.Descendants(maui + "Button"), button =>
            button.Attribute(xaml + "Name")?.Value == "CopyMessageMenuButton");

        Assert.Equal("复制", copy.Attribute("Text")?.Value);
        Assert.Equal("icon_copy.png", copy.Attribute("ImageSource")?.Value);
        Assert.Equal("{Binding CopyActiveMessageCommand}", copy.Attribute("Command")?.Value);
        Assert.Equal("{Binding CanCopyActiveMessage}", copy.Attribute("IsVisible")?.Value);
    }

    [Fact]
    public void EmojiPickers_WhenStickersAdded_KeepLargerDefaultAndCompactReactionGrids()
    {
        var source = XDocument.Load(FindWorkspaceFile("src", "RelayCove.App", "MainPage.xaml"));
        XNamespace maui = "http://schemas.microsoft.com/dotnet/2021/maui";

        var lists = source.Descendants(maui + "CollectionView")
            .Where(element => element.Attribute("ItemsSource")?.Value == "{Binding VisibleEmojiChoices}").ToArray();

        Assert.Equal(2, lists.Length);
        var composer = Assert.Single(lists, list => list.Attribute("IsVisible") is not null);
        var reaction = Assert.Single(lists, list => list.Attribute("IsVisible") is null);
        Assert.Same(reaction, Assert.Single(reaction.Parent!.Elements()));
        Assert.All(lists, list =>
        {
            Assert.Single(list.Descendants(), element => element.Name.LocalName == "CompactEmojiGridBehavior");
            var grid = Assert.Single(list.Descendants(maui + "GridItemsLayout"));
            Assert.Equal("0", grid.Attribute("HorizontalItemSpacing")?.Value);
            Assert.Equal("0", grid.Attribute("VerticalItemSpacing")?.Value);
            Assert.DoesNotContain(list.Descendants(maui + "Label"), label =>
                label.Parent!.Name != maui + "CollectionView.EmptyView");
        });
        var composerGrid = Assert.Single(composer.Descendants(maui + "GridItemsLayout"));
        Assert.Equal("{Binding ComposerEmojiPickerColumns}", composerGrid.Attribute("Span")?.Value);
        var composerCell = Assert.Single(composer.Descendants(maui + "Grid"), grid =>
            grid.Attribute("SemanticProperties.Description")?.Value == "{Binding AccessibleLabel}");
        Assert.Equal("32", composerCell.Attribute("HeightRequest")?.Value);
        var composerImage = Assert.Single(composerCell.Descendants(), image => image.Name.LocalName == "RealmMediaImageView");
        Assert.Equal("25", composerImage.Attribute("WidthRequest")?.Value);
        var reactionGrid = Assert.Single(reaction.Descendants(maui + "GridItemsLayout"));
        Assert.Equal("{Binding EmojiPickerColumns}", reactionGrid.Attribute("Span")?.Value);
        var raw = source.ToString();
        Assert.DoesNotContain("选择后插入光标位置", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("再次选择可移除", raw, StringComparison.Ordinal);
    }

    [Fact]
    public void StickerSearchResults_WhenRendered_HideNamesAndFavoriteActionsOutsideFavoritesTab()
    {
        var source = XDocument.Load(FindWorkspaceFile("src", "RelayCove.App", "MainPage.xaml"));
        XNamespace maui = "http://schemas.microsoft.com/dotnet/2021/maui";
        var list = Assert.Single(source.Descendants(maui + "CollectionView"), element =>
            element.Attribute("ItemsSource")?.Value == "{Binding Stickers.Items}");
        var card = Assert.Single(list.Descendants(maui + "DataTemplate")).Element(maui + "Grid")!;
        Assert.Equal("82,Auto", card.Attribute("RowDefinitions")?.Value);
        var footer = Assert.Single(card.Elements(maui + "Grid"));
        Assert.Equal("{Binding ViewModel.Stickers.IsFavorites, Source={x:Reference RootPage}, x:DataType=local:MainPage}",
            footer.Attribute("IsVisible")?.Value);
        Assert.Contains(footer.Descendants(maui + "Label"), label => label.Attribute("Text")?.Value == "{Binding Label}");
        Assert.Contains(footer.Descendants(maui + "Button"), button =>
            button.Attribute("Command")?.Value == "{Binding ViewModel.Stickers.ManageCommand, Source={x:Reference RootPage}, x:DataType=local:MainPage}");
        Assert.DoesNotContain(source.Descendants(maui + "Picker"), picker =>
            picker.Attribute("Title")?.Value == "表情分类");
        Assert.Null(list.Attribute("RemainingItemsThreshold"));
        var paging = Assert.Single(list.Descendants(), element => element.Name.LocalName == "StickerInfiniteScrollBehavior");
        Assert.Equal("KeepScrollOffset", list.Attribute("ItemsUpdatingScrollMode")?.Value);
        Assert.Equal("{Binding Stickers.LoadMoreCommand}",
            paging.Attribute("LoadMoreCommand")?.Value);
        Assert.Equal("{Binding Stickers.HasMoreItems}",
            paging.Attribute("HasMoreItems")?.Value);
    }

    [Fact]
    public void EmojiPickers_WhenWindowShrinks_ConstrainScrollableListToRemainingPopupHeight()
    {
        var source = XDocument.Load(FindWorkspaceFile("src", "RelayCove.App", "MainPage.xaml"));
        XNamespace maui = "http://schemas.microsoft.com/dotnet/2021/maui";
        var lists = source.Descendants(maui + "CollectionView")
            .Where(element => element.Attribute("ItemsSource")?.Value == "{Binding VisibleEmojiChoices}").ToArray();

        Assert.Equal(2, lists.Length);
        Assert.All(lists, list =>
        {
            Assert.Null(list.Attribute("HeightRequest"));
            var composer = list.Attribute("IsVisible") is not null;
            Assert.Equal(composer ? "1" : "0", list.Attribute("Grid.Row")?.Value);
            Assert.Equal("Always", list.Attribute("VerticalScrollBarVisibility")?.Value);
            Assert.Equal("Never", list.Attribute("HorizontalScrollBarVisibility")?.Value);
            var layout = list.Parent!;
            Assert.Equal(maui + "Grid", layout.Name);
            Assert.Equal(composer ? "Auto,*,Auto,Auto,Auto" : "*", layout.Attribute("RowDefinitions")?.Value);
            Assert.Equal(composer ? "{Binding StickerPickerHeight}" : "{Binding EmojiPickerHeight}", layout.Parent!.Attribute("HeightRequest")?.Value);
            var anchor = Assert.Single(layout.Parent.Descendants(), element => element.Name.LocalName == "PopoverAnchorBehavior");
            Assert.NotNull(anchor.Attribute("IsOpen"));
        });
        var composerBorder = lists.Single(list => list.Attribute("IsVisible") is not null).Parent!.Parent!;
        Assert.Contains(composerBorder.Descendants(maui + "DataTrigger"), trigger =>
            trigger.Attribute("Binding")?.Value == "{Binding Stickers.IsDefault}" &&
            trigger.Descendants(maui + "Setter").Any(setter =>
                setter.Attribute("Property")?.Value == "HeightRequest" &&
                setter.Attribute("Value")?.Value == "{Binding DefaultEmojiPickerHeight}"));
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
        var resultGrid = labels[0].Parent!.Parent!;
        Assert.Null(resultGrid.Attribute("ColumnDefinitions"));
        Assert.DoesNotContain(resultGrid.Descendants(), element => element.Attribute("Text")?.Value == "{Binding Kind}");
        Assert.Contains(resultGrid.Descendants(), element =>
            element.Name.LocalName == "Label" &&
            element.Attribute("Text")?.Value == "{Binding TimestampText}" &&
            element.Attribute("IsVisible")?.Value == "{Binding HasTimestamp}");
        Assert.Contains(resultGrid.Parent!.Descendants(), element =>
            element.Name.LocalName == "TapGestureRecognizer" &&
            element.Attribute("Command")?.Value?.Contains("SelectSearchResultCommand", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void SearchResults_WhenImagesExist_UsesControlledPreviewsAndRetainsRowNavigation()
    {
        var source = XDocument.Load(FindWorkspaceFile("src", "RelayCove.App", "MainPage.xaml"));
        XNamespace maui = "http://schemas.microsoft.com/dotnet/2021/maui";
        XNamespace controls = "clr-namespace:RelayCove.App.Controls";
        var images = source.Descendants(maui + "FlexLayout")
            .Single(element => element.Attribute("AutomationId")?.Value == "SearchImagePreviews");

        Assert.Equal("{Binding Images}", images.Attribute("BindableLayout.ItemsSource")?.Value);
        Assert.Equal("{Binding HasImages}", images.Attribute("IsVisible")?.Value);
        Assert.Equal("Wrap", images.Attribute("Wrap")?.Value);
        Assert.Equal("True", images.Attribute("InputTransparent")?.Value);
        var preview = Assert.Single(images.Descendants(controls + "RealmMediaImageView"));
        Assert.Equal("{Binding SourceUrl}", preview.Attribute("SourceUrl")?.Value);
        Assert.Equal("AspectFit", preview.Attribute("Aspect")?.Value);
        Assert.Empty(images.Descendants(maui + "Image"));
        var row = images.Ancestors(maui + "Border").First();
        Assert.Contains(row.Descendants(maui + "TapGestureRecognizer"), gesture =>
            gesture.Attribute("Command")?.Value?.Contains("SelectSearchResultCommand", StringComparison.Ordinal) == true);
        Assert.Contains(row.Descendants(controls + "SearchHighlightLabel"), label =>
            label.Attribute("SourceText")?.Value == "{Binding Subtitle}" &&
            label.Attribute("IsVisible")?.Value == "{Binding HasSubtitle}");
    }

    [Fact]
    public void SearchDialog_WhenBackgroundStaysEnabled_KeepsPointerShieldWithoutKeyboardNavigation()
    {
        var source = XDocument.Load(FindWorkspaceFile("src", "RelayCove.App", "MainPage.xaml"));
        var code = File.ReadAllText(FindWorkspaceFile("src", "RelayCove.App", "MainPage.xaml.cs"));
        XNamespace maui = "http://schemas.microsoft.com/dotnet/2021/maui";
        var overlay = source.Descendants(maui + "Grid")
            .Single(element => element.Attribute("IsVisible")?.Value == "{Binding IsSearchOpen}");
        var shield = Assert.Single(overlay.Elements(maui + "BoxView"));
        var dialog = Assert.Single(overlay.Elements(maui + "Border"));

        Assert.Equal("{StaticResource OverlayBrush}", shield.Attribute("Background")?.Value);
        Assert.Contains(shield.Descendants(maui + "TapGestureRecognizer"), gesture =>
            gesture.Attribute("Command")?.Value == "{Binding CloseSearchCommand}");
        Assert.Null(dialog.Attribute("HandlerChanged"));
        Assert.DoesNotContain("TabFocusNavigation", code, StringComparison.Ordinal);
        Assert.DoesNotContain("HandleSearchKey", code, StringComparison.Ordinal);
    }

    [Fact]
    public void SearchEntry_WhenQueryChanges_UsesDebouncedSearchAndKeepsButtonOrEnterForImmediateSearch()
    {
        var source = XDocument.Load(FindWorkspaceFile("src", "RelayCove.App", "MainPage.xaml"));
        var code = File.ReadAllText(FindWorkspaceFile("src", "RelayCove.App", "MainPage.xaml.cs"));
        var viewModel = File.ReadAllText(FindWorkspaceFile("src", "RelayCove.App", "ViewModels", "ShellViewModel.cs"));
        XNamespace maui = "http://schemas.microsoft.com/dotnet/2021/maui";
        var entry = source.Descendants(maui + "Entry").Single(element => element.Attribute("AutomationId")?.Value == "SearchEntry");
        var button = source.Descendants(maui + "Button").Single(element => element.Attribute("AutomationId")?.Value == "SearchSubmitButton");

        Assert.Same(entry.Parent, button.Parent);
        Assert.Equal("1", button.Attribute("Grid.Column")?.Value);
        Assert.Equal("搜索", button.Attribute("Text")?.Value);
        Assert.Equal("{Binding SearchNowCommand}", button.Attribute("Command")?.Value);
        Assert.Equal("OnSearchCompleted", entry.Attribute("Completed")?.Value);
        Assert.Contains("_viewModel.SearchNowCommand.Execute(null);", code, StringComparison.Ordinal);
        Assert.Contains("ScheduleServerSearch(value);", viewModel, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromMilliseconds(300)", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain(source.Descendants(maui + "Label"), label =>
            label.Attribute("Text")?.Value?.StartsWith("搜索消息、文件、图片、视频", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void ReactionPicker_WhenRendered_UsesItsOwnTriggerAnchor()
    {
        var source = XDocument.Load(FindWorkspaceFile("src", "RelayCove.App", "MainPage.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2009/xaml";

        var reactionPicker = source
            .Descendants()
            .Single(element => element.Attribute(x + "Name")?.Value == "ReactionEmojiCollection")
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
            .Single(element => element.Attribute("BindableLayout.ItemsSource")?.Value == "{Binding Attachments}" &&
                element.Ancestors(maui + "DataTemplate").First().Attribute(x + "DataType")?.Value == "viewModels:MessageItem");
        var imagePreview = attachmentLayout
            .Descendants(maui + "Border")
            .Single(element => element.Attribute("IsVisible")?.Value == "{Binding IsImage}");
        var imageAttachmentCard = attachmentLayout
            .Descendants(maui + "Border")
            .Single(element => element.Attribute("SemanticProperties.Description")?.Value == "{Binding AccessibleLabel}");
        var imageCardTrigger = imageAttachmentCard
            .Descendants(maui + "DataTrigger")
            .Single(element => element.Attribute("Binding")?.Value == "{Binding IsImage}");
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
        Assert.Null(imagePreview.Attribute("HeightRequest"));
        Assert.Equal("220", imagePreview.Attribute("MaximumHeightRequest")?.Value);
        Assert.Equal("360", imagePreview.Attribute("MaximumWidthRequest")?.Value);
        Assert.Equal("Start", imagePreview.Attribute("HorizontalOptions")?.Value);
        Assert.Equal("Start", imagePreview.Attribute("VerticalOptions")?.Value);
        Assert.Equal("Transparent", imagePreview.Attribute("Background")?.Value);
        Assert.Contains(imageCardTrigger.Elements(maui + "Setter"), setter =>
            setter.Attribute("Property")?.Value == "Background" && setter.Attribute("Value")?.Value == "Transparent");
        Assert.Contains(imageCardTrigger.Elements(maui + "Setter"), setter =>
            setter.Attribute("Property")?.Value == "StrokeThickness" && setter.Attribute("Value")?.Value == "0");
        Assert.Contains(imageOnlyTrigger.Elements(maui + "Setter"), setter =>
            setter.Attribute("Property")?.Value == "Padding" && setter.Attribute("Value")?.Value == "0");
        Assert.Contains(imageOnlyTrigger.Elements(maui + "Setter"), setter =>
            setter.Attribute("Property")?.Value == "Background" && setter.Attribute("Value")?.Value == "Transparent");
        Assert.Contains(imageOnlyTrigger.Elements(maui + "Setter"), setter =>
            setter.Attribute("Property")?.Value == "StrokeThickness" && setter.Attribute("Value")?.Value == "0");
        Assert.Contains(fileDetails.Descendants(maui + "Label"), label =>
            label.Attribute("Text")?.Value == "{Binding Name}");
        Assert.Contains(fileDetails.Descendants(maui + "Button"), button =>
            button.Attribute("Text")?.Value == "{Binding DownloadActionText}");
        Assert.Equal("{Binding HasActiveMessageAttachment}", imageDownload.Attribute("IsVisible")?.Value);
        Assert.Equal("{Binding DownloadAttachmentCommand}", imageDownload.Attribute("Command")?.Value);
        Assert.Equal("{Binding ActiveMessageAttachment}", imageDownload.Attribute("CommandParameter")?.Value);
        var imageCopy = pageSource.Descendants(maui + "Button")
            .Single(button => button.Attribute(x + "Name")?.Value == "ImageCopyMenuButton");
        Assert.Equal("{Binding HasActiveMessageAttachment}", imageCopy.Attribute("IsVisible")?.Value);
        Assert.Equal("{Binding CopyActiveImageCommand}", imageCopy.Attribute("Command")?.Value);
        Assert.Equal("复制图片", imageCopy.Attribute("Text")?.Value);
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
                element.Attribute("IsVisible")?.Value == "{Binding HasPlainBody}" &&
                element.Ancestors(maui + "DataTemplate").First()
                    .Attribute(XName.Get("DataType", "http://schemas.microsoft.com/winfx/2009/xaml"))?.Value == "viewModels:MessageItem");
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
        var emojiBody = Assert.Single(source.Descendants(), element => element.Name.LocalName == "MessageEmojiLabel");
        Assert.Equal("{Binding BodyRuns}", emojiBody.Attribute("Runs")?.Value);
        Assert.Equal("{Binding HasCustomEmoji}", emojiBody.Attribute("IsVisible")?.Value);
    }

    [Fact]
    public void MessageBody_WhenRendered_UsesReadableLineHeightAndFullFontBounds()
    {
        var source = XDocument.Load(FindWorkspaceFile("src", "RelayCove.App", "Controls", "MessageListView.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2009/xaml";
        var bubble = source.Descendants().Single(element => element.Attribute(x + "Name")?.Value == "Bubble");
        var padding = (Microsoft.Maui.Thickness)new Microsoft.Maui.Converters.ThicknessTypeConverter()
            .ConvertFromInvariantString(bubble.Attribute("Padding")!.Value)!;
        Assert.Equal(14d, padding.Left);
        Assert.Equal(14d, padding.Right);
        Assert.Equal(7d, padding.Top);
        Assert.Equal(10d, padding.Bottom);

        var plainLabel = source.Descendants().Single(element =>
            element.Name.LocalName == "Label" &&
            element.Attribute("Text")?.Value == "{Binding Body}" &&
            element.Attribute("IsVisible")?.Value == "{Binding HasPlainBody}");
        var emojiLabel = source.Descendants().Single(element => element.Name.LocalName == "MessageEmojiLabel");
        Assert.Equal("1.5", plainLabel.Attribute("LineHeight")?.Value);
        Assert.Equal("1.5", emojiLabel.Attribute("LineHeight")?.Value);

        var plainBody = File.ReadAllText(FindWorkspaceFile("src", "RelayCove.App", "Platforms", "Windows", "Behaviors", "SelectableTextBehavior.cs"));
        var emojiBody = File.ReadAllText(FindWorkspaceFile("src", "RelayCove.App", "Platforms", "Windows", "Handlers", "MessageEmojiLabelHandler.cs"));
        Assert.Contains("platformView.TextLineBounds = TextLineBounds.Full;", plainBody);
        Assert.Contains("handler.PlatformView.TextLineBounds = TextLineBounds.Full;", emojiBody);
        Assert.Contains("handler.PlatformView.ClearValue(RichTextBlock.LineHeightProperty);", emojiBody);
        Assert.Contains("if (view.MaxLines != 1 && view.LineHeight > 0)", emojiBody);
    }

    [Fact]
    public void MessageEmojiBody_WhenAlignedToText_AccountsForDescentInMeasuredBounds()
    {
        var source = File.ReadAllText(FindWorkspaceFile("src", "RelayCove.App", "Platforms", "Windows", "Handlers", "MessageEmojiLabelHandler.cs"));

        // Moving the image without changing its measured baseline leaves a blank
        // strip above it. Body images must use a layout margin instead.
        Assert.Contains("TranslationY = view.MaxLines == 1 ? EmojiInlineLayout.Descent(view.FontSize) : 0", source);
        Assert.Contains("native.Margin = new Microsoft.UI.Xaml.Thickness(0, 0, 0, -EmojiInlineLayout.Descent(view.FontSize));", source);
        Assert.Contains("native.Height = view.EmojiSize;", source);
    }

    [Fact]
    public void MessageActions_WhenRendered_OmitsHoverToolbarAndKeepsRightClickMenu()
    {
        var source = XDocument.Load(FindWorkspaceFile("src", "RelayCove.App", "Controls", "MessageListView.xaml"));
        var page = XDocument.Load(FindWorkspaceFile("src", "RelayCove.App", "MainPage.xaml"));
        XNamespace maui = "http://schemas.microsoft.com/dotnet/2021/maui";

        Assert.DoesNotContain(source.Descendants(), element => element.Attribute("AutomationId")?.Value is
            "MessageQuickActions" or "MessageQuickActionsHost" or "MessageEditButton" or "MessageMoreButton");
        Assert.Single(source.Descendants(), element => element.Name.LocalName == "MessageContextBehavior");
        var menu = page.Descendants(maui + "Grid")
            .Single(element => element.Attribute("IsVisible")?.Value == "{Binding IsMessageMenuOpen}");
        Assert.Contains(menu.Descendants(maui + "Button"), button =>
            button.Attribute("Command")?.Value == "{Binding OpenEditDialogCommand}");
        Assert.Contains(menu.Descendants(maui + "Button"), button =>
            button.Attribute("Command")?.Value == "{Binding QuoteMessageCommand}");
    }

    [Fact]
    public void MessageMenu_WhenOpen_AllowsPointerInputToOtherMessagesAndKeepsMenuInteractive()
    {
        var page = XDocument.Load(FindWorkspaceFile("src", "RelayCove.App", "MainPage.xaml"));
        XNamespace maui = "http://schemas.microsoft.com/dotnet/2021/maui";
        var overlay = page.Descendants(maui + "Grid")
            .Single(element => element.Attribute("IsVisible")?.Value == "{Binding IsMessageMenuOpen}");

        Assert.Equal("True", overlay.Attribute("InputTransparent")?.Value);
        Assert.Equal("False", overlay.Attribute("CascadeInputTransparent")?.Value);
        var menu = Assert.Single(overlay.Elements());
        Assert.Equal(maui + "Border", menu.Name);
        Assert.NotEqual("True", menu.Attribute("InputTransparent")?.Value);
        Assert.Contains(menu.Descendants(maui + "Button"), button =>
            button.Attribute("Command")?.Value == "{Binding QuoteMessageCommand}");
    }

    [Fact]
    public void MessageContext_WhenAttached_DoesNotRegisterKeyboardOrHoverTriggers()
    {
        var source = File.ReadAllText(FindWorkspaceFile(
            "src", "RelayCove.App", "Platforms", "Windows", "Behaviors", "MessageContextBehavior.cs"));

        Assert.DoesNotContain("KeyDown +=", source, StringComparison.Ordinal);
        Assert.DoesNotContain("PointerEntered +=", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GotFocus +=", source, StringComparison.Ordinal);
        Assert.DoesNotContain("FocusState.Keyboard", source, StringComparison.Ordinal);
        Assert.Contains("platformView.IsTabStop = false;", source, StringComparison.Ordinal);
        Assert.Contains("AddHandler(Microsoft.UI.Xaml.UIElement.RightTappedEvent, _rightTappedHandler, true)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MessageMenu_WhenDismissed_DoesNotKeepFocusSubscriptionsOrStealPointerFocus()
    {
        var messageBehavior = File.ReadAllText(FindWorkspaceFile(
            "src", "RelayCove.App", "Platforms", "Windows", "Behaviors", "MessageContextBehavior.cs"));
        var imageBehavior = File.ReadAllText(FindWorkspaceFile(
            "src", "RelayCove.App", "Platforms", "Windows", "Behaviors", "ImageAttachmentContextBehavior.cs"));

        foreach (var behavior in new[] { messageBehavior, imageBehavior })
        {
            Assert.DoesNotContain(".Focus(", behavior, StringComparison.Ordinal);
            Assert.DoesNotContain("PropertyChanged +=", behavior, StringComparison.Ordinal);
            Assert.Contains("AddHandler(Microsoft.UI.Xaml.UIElement.RightTappedEvent, _rightTappedHandler, true)", behavior, StringComparison.Ordinal);
            Assert.Contains("eventArgs.GetPosition(pageRoot)", behavior, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void MessageMenu_WhenOpenedAgain_RepositionsEvenWhenPointerCoordinatesAreUnchanged()
    {
        var page = XDocument.Load(FindWorkspaceFile("src", "RelayCove.App", "MainPage.xaml"));
        var anchor = page.Descendants().Single(element =>
            element.Name.LocalName == "PopoverAnchorBehavior" &&
            element.Attribute("AnchorX")?.Value.Contains("MessageMenuAnchorX", StringComparison.Ordinal) == true);

        Assert.Contains("IsMessageMenuOpen", anchor.Attribute("IsOpen")?.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void MessageBody_WhenContextMenuOpens_SuppressesNativeTextMenuWithoutDisablingSelection()
    {
        var behavior = File.ReadAllText(FindWorkspaceFile(
            "src", "RelayCove.App", "Platforms", "Windows", "Behaviors", "SelectableTextBehavior.cs"));
        var page = XDocument.Load(FindWorkspaceFile("src", "RelayCove.App", "MainPage.xaml"));
        XNamespace maui = "http://schemas.microsoft.com/dotnet/2021/maui";

        Assert.Contains("platformView.IsTextSelectionEnabled = true;", behavior, StringComparison.Ordinal);
        Assert.Contains("platformView.ContextFlyout = null;", behavior, StringComparison.Ordinal);
        Assert.Contains("platformView.ContextMenuOpening += OnContextMenuOpening;", behavior, StringComparison.Ordinal);
        Assert.Contains("eventArgs.Handled = true;", behavior, StringComparison.Ordinal);
        Assert.Contains("_platformView.ContextMenuOpening -= OnContextMenuOpening;", behavior, StringComparison.Ordinal);
        Assert.Contains("_platformView.ContextFlyout = _originalContextFlyout;", behavior, StringComparison.Ordinal);

        var menu = page.Descendants(maui + "Grid")
            .Single(element => element.Attribute("IsVisible")?.Value == "{Binding IsMessageMenuOpen}");
        var copy = menu.Descendants(maui + "Button").Single(button =>
            button.Attribute("Text")?.Value == "复制");
        Assert.Equal("{Binding CopyActiveMessageCommand}", copy.Attribute("Command")?.Value);
        Assert.Equal("{Binding CanCopyActiveMessage}", copy.Attribute("IsVisible")?.Value);
        Assert.Contains(menu.Descendants(maui + "Button"), button =>
            button.Attribute("Command")?.Value == "{Binding QuoteMessageCommand}");

        var messageContext = File.ReadAllText(FindWorkspaceFile(
            "src", "RelayCove.App", "Platforms", "Windows", "Behaviors", "MessageContextBehavior.cs"));
        Assert.Contains("GetSelectedText(eventArgs.OriginalSource", messageContext, StringComparison.Ordinal);
        Assert.Contains("WinUiTextBlock { IsTextSelectionEnabled: true }", messageContext, StringComparison.Ordinal);
        Assert.Contains("WinUiRichTextBlock { IsTextSelectionEnabled: true }", messageContext, StringComparison.Ordinal);
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

    [Fact]
    public void StickerPaging_WhenPreservingPosition_DoesNotHoldALockForANullSenderLayoutEvent()
    {
        var behavior = File.ReadAllText(FindWorkspaceFile("src", "RelayCove.App", "Platforms", "Windows",
            "Behaviors", "StickerInfiniteScrollBehavior.cs"));
        Assert.DoesNotContain("ReferenceEquals(sender", behavior);
        Assert.DoesNotContain("ChangeView(", behavior);
        Assert.DoesNotContain("ContainerContentChanging +=", behavior);
        Assert.Contains("DispatcherQueuePriority.Low", behavior);
        Assert.Contains("finally", behavior);
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
