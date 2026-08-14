using CommunityToolkit.Mvvm.Input;
using RelayCove.App.Services;
using RelayCove.App.ViewModels;
using RelayCove.Core;

namespace RelayCove.App.Tests;

public sealed class ShellViewModelTests
{
    [Fact]
    public void Constructor_WhenNoLastRealm_UsesConfiguredDefault()
    {
        using var viewModel = CreateViewModel(new FakeSession(), new FakeLastRealmStore());

        Assert.Equal(PreferencesLastRealmStore.DefaultRealm, viewModel.Realm);
    }

    [Fact]
    public void Constructor_WhenLastRealmExists_UsesIt()
    {
        using var viewModel = CreateViewModel(new FakeSession(), new FakeLastRealmStore("https://chat.example.test"));

        Assert.Equal("https://chat.example.test", viewModel.Realm);
    }

    [Fact]
    public async Task LoadOlderSaved_WhenServerAnchorIsMissing_PreservesRowsAndRequiresRefresh()
    {
        var account = AccountId.Create(RealmEndpoint.Parse("https://zulip.example"), 10);
        var conversation = new DirectMessage([20]);
        var calls = 0;
        var session = new FakeSession
        {
            Account = account,
            SavedMessagesAction = (_, _, _) =>
            {
                calls++;
                return calls == 1
                    ? Task.FromResult(new MessageQueryPage([new ChatMessage(50, conversation, 20, "saved", DateTimeOffset.UnixEpoch)], false, true, true))
                    : Task.FromResult(new MessageQueryPage([], false, true, false));
            }
        };
        using var viewModel = CreateViewModel(session);

        await ((IAsyncRelayCommand)viewModel.ShowSavedCommand).ExecuteAsync(null);
        await ((IAsyncRelayCommand)viewModel.LoadOlderSavedCommand).ExecuteAsync(null);

        Assert.Single(viewModel.SavedMessages);
        Assert.Equal("已保存消息已变化，请刷新列表。", viewModel.SavedError);
        Assert.False(viewModel.HasMoreSavedMessages);
    }

    [Fact]
    public async Task LoadOlderSearch_WhenTwoServerPagesExist_KeepsBothPagesVisible()
    {
        var calls = 0;
        var session = new FakeSession
        {
            Account = AccountId.Create(RealmEndpoint.Parse("https://zulip.example"), 10),
            SearchMessagesAction = (_, beforeMessageId, _, _) =>
            {
                calls++;
                var ids = beforeMessageId is null
                    ? Enumerable.Range(51, 50)
                    : Enumerable.Range(1, 50);
                var messages = ids.Select(id => new ChatMessage(
                    id,
                    new DirectMessage([20]),
                    20,
                    $"server {id}",
                    DateTimeOffset.UnixEpoch)).ToArray();
                return Task.FromResult(new MessageQueryPage(messages, beforeMessageId is not null, true, true));
            }
        };
        using var viewModel = CreateViewModel(session);
        viewModel.OpenSearchCommand.Execute(null);
        viewModel.SearchQuery = "server";

        await ((IAsyncRelayCommand)viewModel.SearchNowCommand).ExecuteAsync(null);
        await ((IAsyncRelayCommand)viewModel.LoadOlderSearchCommand).ExecuteAsync(null);

        Assert.Equal(2, calls);
        Assert.Equal(100, viewModel.SearchResults.Count);
        Assert.Contains(viewModel.SearchResults, item => item.Id == "server-message:1");
        Assert.Contains(viewModel.SearchResults, item => item.Id == "server-message:100");
    }

    [Fact]
    public void SessionStateChanged_WhenNoConversationOrMessages_ProjectsEmptyState()
    {
        var session = new FakeSession();
        using var viewModel = CreateViewModel(session);

        session.Publish();

        Assert.Empty(viewModel.Messages);
        Assert.Equal(0, viewModel.NewMessageCount);
    }

    [Fact]
    public async Task LoginCommand_WhenAuthenticationFails_ClassifiesErrorAndClearsPassword()
    {
        var session = new FakeSession
        {
            LoginAction = (_, _, _, _) => throw new GatewayException(
                GatewayErrorKind.AuthenticationFailed,
                GatewayErrorCode.AuthenticationFailed)
        };
        using var viewModel = CreateViewModel(session);
        viewModel.Email = "person@example.test";
        viewModel.Password = "do-not-log";

        await ((IAsyncRelayCommand)viewModel.LoginCommand).ExecuteAsync(null);

        Assert.Equal(string.Empty, viewModel.Password);
        Assert.Equal("邮箱、密码或 API 凭据无效。", viewModel.LoginError);
    }

    [Fact]
    public void SessionStateChanged_WhenSubscribedAndRecentDirectMessage_ProjectsNavigationAndRawMessage()
    {
        var direct = new DirectMessage([8]);
        var channel = new ChannelTopic(4, "release");
        var state = new ClientState(
            messages: new Dictionary<long, ChatMessage>
            {
                [11] = new ChatMessage(11, channel, 7, "**raw markdown**", DateTimeOffset.UnixEpoch, senderDisplayName: "Ada")
            },
            subscriptions: new Dictionary<long, Subscription> { [4] = new Subscription(4, "engineering") },
            users: new Dictionary<long, UserProfile> { [8] = new UserProfile(8, "Bea") },
            connection: new ConnectionState(RelayCove.Core.ConnectionStatus.Connected));
        var session = new FakeSession { StateValue = state, Recent = [direct], Selected = channel };
        using var viewModel = CreateViewModel(session);

        session.Publish();

        Assert.True(viewModel.MainVisible);
        Assert.Single(viewModel.Channels);
        Assert.Equal("engineering", viewModel.Channels[0].Name);
        Assert.Single(viewModel.DirectMessages);
        Assert.Equal("Bea", viewModel.DirectMessages[0].Title);
        Assert.Single(viewModel.Messages);
        Assert.Equal("**raw markdown**", viewModel.Messages[0].Content);
    }

    [Fact]
    public void SessionStateChanged_WhenUnreadIsAuthoritative_ProjectsConversationAndNavigationBadges()
    {
        var channel = new ChannelTopic(4, "release");
        var direct = new DirectMessage([8]);
        var state = new ClientState(
            subscriptions: new Dictionary<long, Subscription> { [4] = new Subscription(4, "engineering") },
            users: new Dictionary<long, UserProfile> { [8] = new UserProfile(8, "Bea") },
            unread: new UnreadState(
                new Dictionary<string, int>
                {
                    [channel.CanonicalKey] = 5,
                    [direct.CanonicalKey] = 120
                }),
            connection: new ConnectionState(RelayCove.Core.ConnectionStatus.Connected));
        var session = new FakeSession { StateValue = state, Recent = [direct], Selected = channel };
        using var viewModel = CreateViewModel(session);

        session.Publish();

        Assert.Equal(5, Assert.Single(viewModel.Channels).UnreadCount);
        Assert.Equal("99+", Assert.Single(viewModel.DirectMessages).UnreadLabel);
        Assert.Equal("99+", viewModel.NavigationUnreadLabel);
        Assert.True(viewModel.HasNavigationUnread);
        Assert.Contains("成员数", viewModel.DetailsUnavailableMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void SessionStateChanged_WhenUnreadTotalIsTruncated_DoesNotInventExactTotal()
    {
        var session = new FakeSession
        {
            StateValue = new ClientState(
                unread: new UnreadState(isTruncated: true),
                connection: new ConnectionState(RelayCove.Core.ConnectionStatus.Connected))
        };
        using var viewModel = CreateViewModel(session);

        session.Publish();

        Assert.True(viewModel.HasNavigationUnread);
        Assert.Equal("有未读", viewModel.NavigationUnreadLabel);
    }

    [Fact]
    public async Task ChannelUnsubscribe_WhenConfirmed_UsesSelectedChannelAndClosesDetails()
    {
        var channel = new ChannelTopic(4, "release");
        var state = new ClientState(
            subscriptions: new Dictionary<long, Subscription> { [4] = new Subscription(4, "engineering") },
            connection: new ConnectionState(RelayCove.Core.ConnectionStatus.Connected));
        var requested = new List<long>();
        var session = new FakeSession { StateValue = state, Selected = channel };
        session.UnsubscribeChannelAction = (channelId, _) =>
        {
            requested.Add(channelId);
            session.StateValue = DomainReducer.Apply(
                session.StateValue,
                new SubscriptionRemovedEvent(channelId, Source: DomainEventSource.Local));
            session.Selected = null;
            session.Publish();
            return Task.CompletedTask;
        };
        using var viewModel = CreateViewModel(session);
        viewModel.IsDetailsOpen = true;

        viewModel.RequestChannelUnsubscribeCommand.Execute(null);

        Assert.True(viewModel.IsChannelUnsubscribeConfirmationOpen);
        Assert.Equal("engineering", viewModel.ChannelUnsubscribeTargetName);
        await ((IAsyncRelayCommand)viewModel.ConfirmChannelUnsubscribeCommand).ExecuteAsync(null);

        Assert.Equal([4], requested);
        Assert.False(viewModel.IsChannelUnsubscribeConfirmationOpen);
        Assert.False(viewModel.IsDetailsOpen);
        Assert.False(viewModel.CanUnsubscribeSelectedChannel);
    }

    [Fact]
    public void UpdateViewport_WhenMovingFromWideTo1024_CollapsesDetailsAndUsesOverlayWhenReopened()
    {
        var conversation = new DirectMessage([8]);
        var session = new FakeSession
        {
            Selected = conversation,
            Recent = [conversation],
            StateValue = new ClientState(
                users: new Dictionary<long, UserProfile> { [8] = new UserProfile(8, "Bea") },
                connection: new ConnectionState(RelayCove.Core.ConnectionStatus.Connected))
        };
        using var viewModel = CreateViewModel(session);
        session.Publish();
        viewModel.ToggleDetailsCommand.Execute(null);

        viewModel.UpdateViewport(1024);

        Assert.Equal(ShellLayoutMode.Compact, viewModel.LayoutMode);
        Assert.False(viewModel.IsDetailsOpen);
        Assert.Equal(0, viewModel.InlineDetailsWidth.Value);

        viewModel.ToggleDetailsCommand.Execute(null);
        Assert.True(viewModel.IsOverlayDetailsVisible);
        Assert.False(viewModel.IsInlineDetailsVisible);
        Assert.False(viewModel.IsPrimaryShellEnabled);
    }

    [Fact]
    public void SelectedChannel_WhenTopicLoadPublishesOldConversation_KeepsBrowsedChannelAndSelectsOnlyTopic()
    {
        var channelA = new ChannelTopic(4, "release");
        var topicB = new TopicSummary(5, "native-ui", 12);
        var session = new FakeSession
        {
            Selected = channelA,
            StateValue = new ClientState(
                subscriptions: new Dictionary<long, Subscription>
                {
                    [4] = new Subscription(4, "engineering"),
                    [5] = new Subscription(5, "product-design")
                },
                topics: new Dictionary<string, TopicSummary>
                {
                    [channelA.CanonicalKey] = new TopicSummary(4, "release", 11)
                },
                connection: new ConnectionState(RelayCove.Core.ConnectionStatus.Connected))
        };
        session.LoadTopicsAction = (channelId, _) =>
        {
            if (channelId != 5) return Task.FromResult<IReadOnlyList<TopicSummary>>([]);
            session.Publish();
            return Task.FromResult<IReadOnlyList<TopicSummary>>([topicB]);
        };
        using var viewModel = CreateViewModel(session);

        viewModel.SelectedChannel = viewModel.Channels.Single(item => item.ChannelId == 5);

        Assert.Equal(5, Assert.IsType<ChannelItem>(viewModel.SelectedChannel).ChannelId);
        Assert.Equal("native-ui", Assert.Single(viewModel.Topics).Topic);
        Assert.Equal(new ChannelTopic(5, "native-ui"), session.SelectedConversation);
        Assert.False(viewModel.ShowTopicPicker);
    }

    [Fact]
    public void UpdateViewport_WhenNarrowWithSelection_ShowsChatAndBackCommandRestoresConversationList()
    {
        var conversation = new DirectMessage([8]);
        var session = new FakeSession
        {
            Selected = conversation,
            Recent = [conversation],
            StateValue = new ClientState(
                users: new Dictionary<long, UserProfile> { [8] = new UserProfile(8, "Bea") },
                connection: new ConnectionState(RelayCove.Core.ConnectionStatus.Connected))
        };
        using var viewModel = CreateViewModel(session);
        session.Publish();

        viewModel.UpdateViewport(700);

        Assert.Equal(ShellLayoutMode.Narrow, viewModel.LayoutMode);
        Assert.True(viewModel.IsChatPaneVisible);
        Assert.False(viewModel.IsConversationPaneVisible);

        viewModel.BackToConversationListCommand.Execute(null);
        Assert.True(viewModel.IsConversationPaneVisible);
        Assert.False(viewModel.IsChatPaneVisible);
    }

    [Fact]
    public void ThemeCommands_WhenSelected_ApplyOnlyNonSensitiveAppearancePreference()
    {
        var appearance = new FakeAppearanceService();
        using var viewModel = CreateViewModel(new FakeSession(), appearanceService: appearance);

        viewModel.SetDarkThemeCommand.Execute(null);
        viewModel.SetLightThemeCommand.Execute(null);

        Assert.Equal([AppAppearanceMode.Dark, AppAppearanceMode.Light], appearance.Applied);
        Assert.True(viewModel.IsLightTheme);
    }

    [Fact]
    public void UiPreferenceCommands_WhenChanged_PersistLayoutAndResetDeterministically()
    {
        var preferences = new FakeUiPreferencesService
        {
            Current = new UiPreferences(
                UiDensityMode.Compact,
                UiFontScaleMode.Large,
                UiConversationWidthMode.Wide,
                true)
        };
        using var viewModel = CreateViewModel(
            new FakeSession(),
            uiPreferencesService: preferences);

        Assert.True(viewModel.IsCompactDensity);
        Assert.True(viewModel.IsLargeFont);
        Assert.True(viewModel.IsWideConversationWidth);
        Assert.Equal(352d, viewModel.ConversationPaneWidth.Value);
        Assert.True(viewModel.OpenDetailsByDefault);

        viewModel.SetNarrowConversationWidthCommand.Execute(null);
        Assert.Equal(UiConversationWidthMode.Narrow, preferences.Current.ConversationWidth);
        Assert.Equal(264d, viewModel.ConversationPaneWidth.Value);

        viewModel.ResetUiPreferencesCommand.Execute(null);
        Assert.True(viewModel.IsComfortableDensity);
        Assert.True(viewModel.IsDefaultFont);
        Assert.True(viewModel.IsStandardConversationWidth);
        Assert.Equal(310d, viewModel.ConversationPaneWidth.Value);
        Assert.False(viewModel.OpenDetailsByDefault);
    }

    [Fact]
    public void UpdateViewport_WhenCrossingWebBreakpoints_UsesMatchingLayoutModes()
    {
        using var viewModel = CreateViewModel(new FakeSession());

        viewModel.UpdateViewport(720);
        Assert.Equal(ShellLayoutMode.Narrow, viewModel.LayoutMode);

        viewModel.UpdateViewport(721);
        Assert.Equal(ShellLayoutMode.Compact, viewModel.LayoutMode);

        viewModel.UpdateViewport(1120);
        Assert.Equal(ShellLayoutMode.Compact, viewModel.LayoutMode);

        viewModel.UpdateViewport(1121);
        Assert.Equal(ShellLayoutMode.Wide, viewModel.LayoutMode);
    }

    [Fact]
    public void MessageRowMaximumWidth_WhenViewportChanges_MatchesWebResponsiveCaps()
    {
        using var viewModel = CreateViewModel(new FakeSession());

        viewModel.UpdateViewport(1440);
        Assert.Equal(740d, viewModel.MessageRowMaximumWidth, 3);

        viewModel.UpdateViewport(1024);
        Assert.Equal(516.64d, viewModel.MessageRowMaximumWidth, 2);

        viewModel.UpdateViewport(640);
        Assert.Equal(536d, viewModel.MessageRowMaximumWidth, 3);
    }

    [Fact]
    public void MessageRowMaximumWidth_WhenInlineDetailsOpens_UsesRemainingChatWidth()
    {
        using var viewModel = CreateViewModel(new FakeSession());
        viewModel.UpdateViewport(1440);

        viewModel.IsDetailsOpen = true;

        Assert.Equal(616.96d, viewModel.MessageRowMaximumWidth, 2);
    }

    [Fact]
    public void ConversationGroupCommands_WhenToggled_PersistIndependentPreferences()
    {
        var preferences = new FakeUiPreferencesService();
        using var viewModel = CreateViewModel(new FakeSession(), uiPreferencesService: preferences);

        viewModel.ToggleChannelsCommand.Execute(null);
        Assert.False(viewModel.AreChannelsExpanded);
        Assert.True(viewModel.AreDirectMessagesExpanded);
        Assert.False(preferences.Current.ChannelsExpanded);

        viewModel.ToggleDirectMessagesCommand.Execute(null);
        Assert.False(viewModel.AreDirectMessagesExpanded);
        Assert.False(preferences.Current.DirectMessagesExpanded);
    }

    [Fact]
    public void AccountMenu_WhenOpeningSettings_ClosesOverlayAndSelectsAppearance()
    {
        using var viewModel = CreateViewModel(new FakeSession());

        viewModel.ToggleAccountMenuCommand.Execute(null);
        Assert.True(viewModel.IsAccountMenuOpen);
        Assert.True(viewModel.IsPrimaryShellEnabled);

        viewModel.ShowSettingsCommand.Execute(null);
        Assert.False(viewModel.IsAccountMenuOpen);
        Assert.True(viewModel.IsSettingsSection);
        Assert.True(viewModel.IsAppearanceSettings);
    }

    [Fact]
    public void MessageMenu_WhenOpen_RemainsAPopoverWithoutDisablingTheShell()
    {
        using var viewModel = CreateViewModel(new FakeSession());
        var message = new MessageItem("message-1", 1, 7, "Ada", "hello", "10:00", isOwn: true);

        viewModel.OpenMessageMenuCommand.Execute(message);

        Assert.True(viewModel.IsMessageMenuOpen);
        Assert.True(viewModel.IsPrimaryShellEnabled);
    }

    [Fact]
    public void EmojiPickers_WhenOpen_RemainPopoversWithoutDisablingTheShell()
    {
        using var viewModel = CreateViewModel(new FakeSession());
        viewModel.ToggleComposerEmojiPickerCommand.Execute(null);
        Assert.True(viewModel.IsComposerEmojiPickerOpen);
        Assert.True(viewModel.IsPrimaryShellEnabled);

        viewModel.ToggleComposerEmojiPickerCommand.Execute(null);
        viewModel.IsReactionPickerOpen = true;
        Assert.True(viewModel.IsReactionPickerOpen);
        Assert.True(viewModel.IsPrimaryShellEnabled);
    }

    [Fact]
    public void OpenComposerEmojiPickerAtCommand_WhenInvoked_StoresTheTriggerAnchor()
    {
        using var viewModel = CreateViewModel(new FakeSession());

        viewModel.OpenComposerEmojiPickerAtCommand.Execute(new PopoverAnchorRequest(92.5d, 714d));

        Assert.True(viewModel.IsComposerEmojiPickerOpen);
        Assert.Equal(92.5d, viewModel.ComposerEmojiAnchorX);
        Assert.Equal(714d, viewModel.ComposerEmojiAnchorY);
        Assert.Null(viewModel.SelectedComposerEmoji);
        Assert.DoesNotContain(viewModel.EmojiChoices, choice => choice.IsComposerSelected);
    }

    [Fact]
    public void EmojiSelection_WhenKeyboardIndexChanges_UpdatesOnlyTheCustomPickerState()
    {
        using var viewModel = CreateViewModel(new FakeSession());
        var first = viewModel.EmojiChoices[0];
        var second = viewModel.EmojiChoices[1];

        viewModel.SelectedComposerEmoji = first;
        viewModel.SelectedComposerEmoji = second;
        viewModel.SelectedReactionEmoji = first;

        Assert.False(first.IsComposerSelected);
        Assert.True(second.IsComposerSelected);
        Assert.True(first.IsReactionSelected);
        Assert.False(second.IsReactionSelected);
    }

    [Fact]
    public void ApplyNativePreviewScene_WhenSettingsRequested_ProjectsSettingsWithoutInputAutomation()
    {
        using var viewModel = CreateViewModel(new FakeSession());

        viewModel.ApplyNativePreviewScene("settings");

        Assert.True(viewModel.IsSettingsSection);
        Assert.True(viewModel.IsAppearanceSettings);
    }

    [Fact]
    public async Task InitializeAsync_WhenNativePreviewIsUsed_ProjectsTheWebAcceptanceFixture()
    {
        using var viewModel = CreateViewModel(new NativeShellPreviewSession());

        await viewModel.InitializeAsync();

        Assert.Equal("Acme Workspace", viewModel.WorkspaceDisplayName);
        Assert.True(viewModel.IsNativePreview);
        Assert.False(viewModel.ShowLoadOlderButton);
        Assert.Equal(
            ["UI 设计讨论", "Windows 客户端", "产品路线图", "版本发布"],
            viewModel.Channels.Select(channel => channel.DisplayTitle));
        Assert.Equal(
            ["Maya Chen", "Alex Wu", "Daniel Okafor", "Sarah Li", "林远（自己）"],
            viewModel.DirectMessages.Select(message => message.Title));
        Assert.Equal(4, viewModel.Messages.Count);
#if DEBUG
        Assert.Equal("4 条未读消息", viewModel.Messages[2].UnreadDividerLabel);
        Assert.True(viewModel.Messages[2].ShowUnreadDivider);
#else
        Assert.Equal("未读消息", viewModel.Messages[2].UnreadDividerLabel);
        Assert.False(viewModel.Messages[2].ShowUnreadDivider);
#endif
    }

    [Theory]
    [InlineData("details", true, true)]
    [InlineData("narrow-list", false, true)]
    [InlineData("narrow-chat", false, false)]
    public void ApplyNativePreviewScene_WhenLayoutSceneRequested_ProjectsWithoutInputAutomation(
        string scene,
        bool detailsOpen,
        bool listVisible)
    {
        using var viewModel = CreateViewModel(new FakeSession());

        viewModel.ApplyNativePreviewScene(scene);

        Assert.Equal(detailsOpen, viewModel.IsDetailsOpen);
        Assert.Equal(listVisible, viewModel.IsConversationListVisibleOnNarrow);
    }

    [Fact]
    public void UpdateViewport_WhenPreviewDetailsSceneIsRequested_RespectsBuildIsolation()
    {
        var previousPreview = Environment.GetEnvironmentVariable("RELAYCOVE_NATIVE_UI_PREVIEW");
        var previousScene = Environment.GetEnvironmentVariable("RELAYCOVE_NATIVE_UI_PREVIEW_SCENE");
        try
        {
            Environment.SetEnvironmentVariable("RELAYCOVE_NATIVE_UI_PREVIEW", "1");
            Environment.SetEnvironmentVariable("RELAYCOVE_NATIVE_UI_PREVIEW_SCENE", "details");
            using var viewModel = CreateViewModel(new FakeSession());
            viewModel.ApplyNativePreviewScene("details");

            viewModel.UpdateViewport(1024);

#if DEBUG
            Assert.True(viewModel.IsDetailsOpen);
            Assert.True(viewModel.IsOverlayDetailsVisible);
#else
            Assert.False(viewModel.IsDetailsOpen);
            Assert.False(viewModel.IsOverlayDetailsVisible);
#endif
        }
        finally
        {
            Environment.SetEnvironmentVariable("RELAYCOVE_NATIVE_UI_PREVIEW", previousPreview);
            Environment.SetEnvironmentVariable("RELAYCOVE_NATIVE_UI_PREVIEW_SCENE", previousScene);
        }
    }

    [Theory]
    [InlineData("light", AppAppearanceMode.Light)]
    [InlineData("dark", AppAppearanceMode.Dark)]
    [InlineData("system", AppAppearanceMode.System)]
    [InlineData("unknown", AppAppearanceMode.Light)]
    public void ApplyNativePreviewTheme_WhenRequested_UsesDeterministicTheme(
        string theme,
        AppAppearanceMode expected)
    {
        using var viewModel = CreateViewModel(new FakeSession());

        viewModel.ApplyNativePreviewTheme(theme);

        Assert.Equal(expected, viewModel.AppearanceMode);
    }

    [Theory]
    [InlineData(2026, 8, 14, 2026, 8, 14, 9, 56, "9:56")]
    [InlineData(2026, 8, 14, 2026, 8, 13, 9, 56, "昨天")]
    [InlineData(2026, 8, 14, 2026, 8, 9, 9, 56, "周日")]
    [InlineData(2026, 8, 14, 2026, 7, 1, 9, 56, "7/1")]
    public void FormatConversationTimestamp_WhenProjected_UsesWebParityLabels(
        int nowYear,
        int nowMonth,
        int nowDay,
        int messageYear,
        int messageMonth,
        int messageDay,
        int hour,
        int minute,
        string expected)
    {
        var now = new DateTime(nowYear, nowMonth, nowDay, 12, 0, 0);
        var timestamp = new DateTime(messageYear, messageMonth, messageDay, hour, minute, 0);

        Assert.Equal(expected, ShellViewModel.FormatConversationTimestamp(timestamp, now));
    }

    [Fact]
    public void MessageMenu_WhenMessageStarStateChanges_ProjectsTheMatchingActionLabel()
    {
        using var viewModel = CreateViewModel(new FakeSession());

        viewModel.OpenMessageMenuCommand.Execute(new MessageItem("message-1", 1, 7, "Ada", "hello", "10:00", isStarred: false));
        Assert.Equal("收藏消息", viewModel.ActiveMessageStarActionLabel);

        viewModel.OpenMessageMenuCommand.Execute(new MessageItem("message-2", 2, 7, "Ada", "world", "10:01", isStarred: true));
        Assert.Equal("取消收藏", viewModel.ActiveMessageStarActionLabel);
    }

    [Fact]
    public void OpenMessageMenuAtCommand_WhenInvoked_StoresTheRequestedAnchor()
    {
        using var viewModel = CreateViewModel(new FakeSession());
        var message = new MessageItem("message-1", 1, 7, "Ada", "hello", "10:00", isOwn: true);

        viewModel.OpenMessageMenuAtCommand.Execute(new MessageMenuRequest(message, 812.5d, 244d));

        Assert.True(viewModel.IsMessageMenuOpen);
        Assert.Same(message, viewModel.ActiveMessageAction);
        Assert.Equal(812.5d, viewModel.MessageMenuAnchorX);
        Assert.Equal(244d, viewModel.MessageMenuAnchorY);
    }

    [Fact]
    public void SessionStateChanged_WhenSwitchingConversations_RestoresIndependentDrafts()
    {
        var first = new DirectMessage([8]);
        var second = new DirectMessage([9]);
        var session = new FakeSession
        {
            Selected = first,
            Recent = [first, second],
            StateValue = new ClientState(
                users: new Dictionary<long, UserProfile>
                {
                    [8] = new UserProfile(8, "Bea"),
                    [9] = new UserProfile(9, "Chen")
                },
                connection: new ConnectionState(RelayCove.Core.ConnectionStatus.Connected))
        };
        using var viewModel = CreateViewModel(session);
        session.Publish();
        viewModel.ComposerText = "first draft";

        session.Selected = second;
        session.Publish();
        viewModel.ComposerText = "second draft";
        session.Selected = first;
        session.Publish();

        Assert.Equal("first draft", viewModel.ComposerText);
    }

    [Fact]
    public async Task SendCommand_WhenUserTypesDuringSend_PreservesNewerDraftSnapshot()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var conversation = new DirectMessage([8]);
        var session = new FakeSession
        {
            Selected = conversation,
            Recent = [conversation],
            StateValue = new ClientState(connection: new ConnectionState(RelayCove.Core.ConnectionStatus.Connected)),
            SendAction = async (_, cancellationToken) =>
            {
                started.SetResult();
                await release.Task.WaitAsync(cancellationToken);
            }
        };
        using var viewModel = CreateViewModel(session);
        session.Publish();
        viewModel.ComposerText = "original";

        var send = ((IAsyncRelayCommand)viewModel.SendCommand).ExecuteAsync(null);
        await started.Task;
        viewModel.ComposerText = "newer input";
        release.SetResult();
        await send;

        Assert.Equal("newer input", viewModel.ComposerText);
        Assert.Equal(["original"], session.SentContents);
    }

    [Fact]
    public async Task SendCommand_WhenDraftIsUnchanged_ClearsOnlyConfirmedSnapshot()
    {
        var conversation = new DirectMessage([8]);
        var session = new FakeSession
        {
            Selected = conversation,
            Recent = [conversation],
            StateValue = new ClientState(connection: new ConnectionState(RelayCove.Core.ConnectionStatus.Connected))
        };
        using var viewModel = CreateViewModel(session);
        session.Publish();
        viewModel.ComposerText = "confirmed";

        await ((IAsyncRelayCommand)viewModel.SendCommand).ExecuteAsync(null);

        Assert.Equal(string.Empty, viewModel.ComposerText);
        Assert.Equal(["confirmed"], session.SentContents);
    }

    [Fact]
    public void SessionStateChanged_WhenProjectionIsUnchanged_PreservesKeyedItemIdentity()
    {
        var state = new ClientState(
            subscriptions: new Dictionary<long, Subscription> { [4] = new Subscription(4, "engineering") },
            connection: new ConnectionState(RelayCove.Core.ConnectionStatus.Connected));
        var session = new FakeSession { StateValue = state };
        using var viewModel = CreateViewModel(session);
        session.Publish();
        var original = Assert.Single(viewModel.Channels);

        session.Publish();

        Assert.Same(original, Assert.Single(viewModel.Channels));
    }

    [Fact]
    public void SessionStateChanged_WhenHistoryChanges_ProjectsLoadingErrorAndOldestState()
    {
        var conversation = new DirectMessage([8]);
        var session = new FakeSession
        {
            Selected = conversation,
            HistoryState = new ConversationHistoryState(conversation, 1, true, false, true, 50, null),
            StateValue = new ClientState(connection: new ConnectionState(ConnectionStatus.Connected))
        };
        using var viewModel = CreateViewModel(session);

        Assert.True(viewModel.IsLoadingOlder);
        Assert.True(viewModel.ShowLoadOlderButton);

        session.HistoryState = new ConversationHistoryState(conversation, 1, false, true, false, 1, "history_failed");
        session.Publish();

        Assert.False(viewModel.IsLoadingOlder);
        Assert.True(viewModel.HasReachedOldestMessage);
        Assert.False(viewModel.ShowLoadOlderButton);
        Assert.Equal("无法加载更早消息，请稍后重试。", viewModel.MessageLoadError);
    }

    [Fact]
    public async Task MessageViewport_WhenNearTop_DebouncesAutomaticLoadOlder()
    {
        var conversation = new DirectMessage([8]);
        var session = new FakeSession
        {
            Selected = conversation,
            HistoryState = new ConversationHistoryState(conversation, 1, false, false, true, 50, null),
            StateValue = new ClientState(connection: new ConnectionState(ConnectionStatus.Connected))
        };
        using var viewModel = CreateViewModel(session);

        await viewModel.ReportMessageViewportAsync(4, 5, 100, 1_000);
        await viewModel.ReportMessageViewportAsync(3, 5, 100, 1_100);
        await viewModel.ReportMessageViewportAsync(3, 5, 100, 1_400);

        Assert.Equal(2, session.LoadOlderCalls);
    }

    [Fact]
    public async Task LoadOlder_WhenAutomaticLoadIsSuppressedByError_RemainsAvailableManually()
    {
        var conversation = new DirectMessage([8]);
        var session = new FakeSession
        {
            Selected = conversation,
            HistoryState = new ConversationHistoryState(conversation, 1, false, false, true, 50, "offline"),
            StateValue = new ClientState(connection: new ConnectionState(ConnectionStatus.Offline))
        };
        using var viewModel = CreateViewModel(session);

        await viewModel.ReportMessageViewportAsync(0, 4, 0, 1_000);
        await ((IAsyncRelayCommand)viewModel.LoadOlderCommand).ExecuteAsync(null);

        Assert.Equal(1, session.LoadOlderCalls);
        Assert.True(viewModel.ShowLoadOlderButton);
    }

    [Fact]
    public async Task Messages_WhenViewportIsAwayFromBottom_ShowsNewMessageButtonUntilJumped()
    {
        var conversation = new DirectMessage([8]);
        var messages = Enumerable.Range(1, 6).ToDictionary(
            id => (long)id,
            id => new ChatMessage(id, conversation, 8, $"message {id}", DateTimeOffset.UnixEpoch.AddMinutes(id), senderDisplayName: "Bea"));
        var session = new FakeSession
        {
            Selected = conversation,
            HistoryState = new ConversationHistoryState(conversation, 1, false, true, false, 1, null),
            StateValue = new ClientState(messages: messages, connection: new ConnectionState(ConnectionStatus.Connected))
        };
        using var viewModel = CreateViewModel(session);
        var initialScrollRequest = viewModel.ScrollToLatestRequest;
        await viewModel.ReportMessageViewportAsync(1, 1, 120, 1_000);

        session.StateValue = session.StateValue with
        {
            Messages = new Dictionary<long, ChatMessage>(messages)
            {
                [7] = new ChatMessage(7, conversation, 8, "message 7", DateTimeOffset.UnixEpoch.AddMinutes(7), senderDisplayName: "Bea")
            }
        };
        session.Publish();

        Assert.Equal(initialScrollRequest, viewModel.ScrollToLatestRequest);
        Assert.Equal(1, viewModel.NewMessageCount);
        Assert.True(viewModel.ShowNewMessagesButton);

        viewModel.ScrollToLatestCommand.Execute(null);

        Assert.Equal(initialScrollRequest + 1, viewModel.ScrollToLatestRequest);
        Assert.Equal(0, viewModel.NewMessageCount);
    }

    [Fact]
    public async Task LoginCommand_WhenAlreadyExecuting_RejectsConcurrentExecution()
    {
        var blocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new FakeSession
        {
            LoginAction = async (_, _, _, cancellationToken) => await blocker.Task.WaitAsync(cancellationToken)
        };
        using var viewModel = CreateViewModel(session);

        var first = ((IAsyncRelayCommand)viewModel.LoginCommand).ExecuteAsync(null);
        await Task.Yield();
        var second = ((IAsyncRelayCommand)viewModel.LoginCommand).ExecuteAsync(null);
        blocker.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, session.LoginCalls);
    }

    [Fact]
    public void SessionStateChanged_WhenOutboxResultIsUncertain_AllowsExplicitComposerRecovery()
    {
        var conversation = new DirectMessage([8]);
        var outbox = new OutboxEntry(
            "9",
            conversation,
            "recover this raw text",
            DateTimeOffset.UnixEpoch,
            OutboxState.WaitExpired);
        var session = new FakeSession
        {
            Selected = conversation,
            StateValue = new ClientState(
                outbox: new Dictionary<string, OutboxEntry> { [outbox.LocalId] = outbox },
                connection: new ConnectionState(RelayCove.Core.ConnectionStatus.Connected))
        };
        using var viewModel = CreateViewModel(session);

        session.Publish();
        var message = Assert.Single(viewModel.Messages);
        viewModel.RecoverOutboxCommand.Execute(message);

        Assert.True(message.CanRecover);
        Assert.Equal("recover this raw text", viewModel.ComposerText);
        Assert.Contains("可能产生重复消息", message.DeliveryState, StringComparison.Ordinal);
    }

    [Fact]
    public void SessionStateChanged_WhenAuthenticatedCacheFaults_KeepsMainVisibleForLogoutAndCachedData()
    {
        var session = new FakeSession
        {
            Account = RelayCove.Core.AccountId.Create(
                RealmEndpoint.Parse("https://zulip.example"), 10),
            StateValue = new ClientState(connection: new ConnectionState(
                RelayCove.Core.ConnectionStatus.Faulted,
                "local_store_error"))
        };
        using var viewModel = CreateViewModel(session);

        session.Publish();

        Assert.True(viewModel.MainVisible);
        Assert.Equal("连接故障", viewModel.ConnectionStatus);
    }

    [Fact]
    public async Task LogoutCommand_WhenSecurityCleanupFails_PreservesActionableError()
    {
        var session = new FakeSession
        {
            LogoutAction = _ => throw new AggregateException(new InvalidOperationException("cleanup"))
        };
        using var viewModel = CreateViewModel(session);

        viewModel.RequestLogoutCommand.Execute(null);
        await ((IAsyncRelayCommand)viewModel.ConfirmLogoutCommand).ExecuteAsync(null);

        Assert.Equal("注销未完全完成，请重试以安全删除凭据并锁定本地缓存。", viewModel.LoginError);
    }

    [Fact]
    public void SessionStateChanged_WhenMessagesCrossDatesAndUnreadBoundary_ProjectsOwnAndDividers()
    {
        var conversation = new DirectMessage([8]);
        var session = new FakeSession
        {
            CurrentUserId = 7,
            ActiveRealm = RealmEndpoint.Parse("https://chat.example.test/"),
            Selected = conversation,
            StateValue = new ClientState(
                messages: new Dictionary<long, ChatMessage>
                {
                    [1] = new ChatMessage(1, conversation, 7, "own", new DateTimeOffset(2026, 8, 12, 8, 0, 0, TimeSpan.Zero), true, "Ada"),
                    [2] = new ChatMessage(2, conversation, 8, "unread", new DateTimeOffset(2026, 8, 13, 8, 0, 0, TimeSpan.Zero), false, "Bea")
                },
                connection: new ConnectionState(ConnectionStatus.Connected))
        };
        using var viewModel = CreateViewModel(session);

        session.Publish();

        Assert.Collection(
            viewModel.Messages,
            first =>
            {
                Assert.True(first.IsOwn);
                Assert.True(first.ShowDateDivider);
                Assert.False(first.ShowUnreadDivider);
            },
            second =>
            {
                Assert.False(second.IsOwn);
                Assert.True(second.ShowDateDivider);
                Assert.True(second.ShowUnreadDivider);
                Assert.Equal("https://chat.example.test/#narrow/near/2", second.Permalink);
            });
    }

    [Fact]
    public void QuoteMessage_WhenDraftExists_AppendsOfficialFenceAndPreservesRawContent()
    {
        var conversation = new DirectMessage([8]);
        var session = new FakeSession
        {
            CurrentUserId = 7,
            ActiveRealm = RealmEndpoint.Parse("https://chat.example.test/"),
            Selected = conversation,
            StateValue = new ClientState(
                messages: new Dictionary<long, ChatMessage>
                {
                    [42] = new ChatMessage(42, conversation, 8, "raw `markdown`\n[file](/user_uploads/x)", DateTimeOffset.UnixEpoch, senderDisplayName: "Bea")
                },
                connection: new ConnectionState(ConnectionStatus.Connected))
        };
        using var viewModel = CreateViewModel(session);
        session.Publish();
        viewModel.ComposerText = "existing";

        viewModel.QuoteMessageCommand.Execute(Assert.Single(viewModel.Messages));

        Assert.StartsWith("existing\n\n@_**Bea|8** [said](https://chat.example.test/#narrow/near/42):", viewModel.ComposerText, StringComparison.Ordinal);
        Assert.Contains("``quote\nraw `markdown`", viewModel.ComposerText, StringComparison.Ordinal);
        Assert.Contains("[file](/user_uploads/x)", viewModel.ComposerText, StringComparison.Ordinal);
    }

    [Fact]
    public void InsertComposerEmoji_WhenSelectionExists_ReplacesSelectionAndRestoresCaret()
    {
        using var viewModel = CreateViewModel(new FakeSession());
        viewModel.ComposerText = "hello xx world";
        viewModel.ComposerCursorPosition = 6;
        viewModel.ComposerSelectionLength = 2;
        var choice = viewModel.EmojiChoices.Single(item => item.Emoji == "🚀");

        viewModel.InsertComposerEmojiCommand.Execute(choice);

        Assert.Equal("hello 🚀 world", viewModel.ComposerText);
        Assert.Equal(8, viewModel.ComposerCursorPosition);
        Assert.Equal(0, viewModel.ComposerSelectionLength);
        Assert.Equal(1, viewModel.ComposerFocusRequest);
    }

    [Fact]
    public void SearchQuery_WhenStateIsLoaded_ReturnsRealLocalSourcesWithoutInventingPresence()
    {
        var conversation = new ChannelTopic(4, "release");
        var session = new FakeSession
        {
            CurrentUserId = 7,
            StateValue = new ClientState(
                subscriptions: new Dictionary<long, Subscription> { [4] = new Subscription(4, "engineering") },
                users: new Dictionary<long, UserProfile> { [8] = new UserProfile(8, "Bea") },
                topics: new Dictionary<string, TopicSummary> { [conversation.CanonicalKey] = new TopicSummary(4, "release", 5) },
                messages: new Dictionary<long, ChatMessage> { [5] = new ChatMessage(5, conversation, 8, "native search", DateTimeOffset.UnixEpoch, senderDisplayName: "Bea") },
                connection: new ConnectionState(ConnectionStatus.Connected))
        };
        using var viewModel = CreateViewModel(session);
        session.Publish();

        viewModel.SearchQuery = "release";

        var result = Assert.Single(viewModel.SearchResults);
        Assert.Equal("话题", result.Kind);
        Assert.Equal(conversation, result.Conversation);
        Assert.DoesNotContain(viewModel.SearchResults, item => item.Subtitle.Contains("在线", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StartNewConversationCommand_WhenKnownUsersAreSelected_UsesCanonicalGroupDirectMessage()
    {
        var session = new FakeSession
        {
            CurrentUserId = 7,
            StateValue = new ClientState(
                users: new Dictionary<long, UserProfile>
                {
                    [7] = new UserProfile(7, "Ada"),
                    [9] = new UserProfile(9, "Chen"),
                    [8] = new UserProfile(8, "Bea")
                },
                connection: new ConnectionState(ConnectionStatus.Connected))
        };
        using var viewModel = CreateViewModel(session);

        viewModel.OpenNewConversationCommand.Execute(null);
        Assert.Equal(["Bea", "Chen"], viewModel.NewConversationChoices.Select(choice => choice.Name));
        Assert.DoesNotContain(viewModel.NewConversationChoices, choice => choice.UserId == 7);
        foreach (var choice in viewModel.NewConversationChoices) choice.IsSelected = true;

        await ((IAsyncRelayCommand)viewModel.StartNewConversationCommand).ExecuteAsync(null);

        var direct = Assert.IsType<DirectMessage>(session.Selected);
        Assert.Equal([8L, 9L], direct.OtherUserIds);
        Assert.False(viewModel.IsNewConversationOpen);
    }

    [Fact]
    public async Task SessionAction_WhenInvalidOperationContainsSecret_DoesNotExposeExceptionText()
    {
        var sentinel = "api-key-secret-sentinel";
        var session = new FakeSession
        {
            Selected = new DirectMessage([8]),
            StateValue = new ClientState(connection: new ConnectionState(ConnectionStatus.Connected)),
            SendAction = (_, _) => throw new InvalidOperationException(sentinel)
        };
        using var viewModel = CreateViewModel(session);
        session.Publish();
        viewModel.ComposerText = "safe";

        await ((IAsyncRelayCommand)viewModel.SendCommand).ExecuteAsync(null);

        Assert.NotNull(viewModel.LoginError);
        Assert.DoesNotContain(sentinel, viewModel.LoginError, StringComparison.Ordinal);
    }

    [Fact]
    public void AddDroppedAttachments_WhenWindowsDropSuppliesFiles_UsesTheSameValidatedDraftPath()
    {
        using var viewModel = CreateViewModel(new FakeSession());
        var dropped = new SelectedAttachmentFile(
            "notes.txt",
            "text/plain",
            3,
            _ => Task.FromResult<Stream>(new MemoryStream([1, 2, 3])));
        viewModel.IsFileDragActive = true;

        viewModel.AddDroppedAttachmentsCommand.Execute(new[] { dropped });

        Assert.False(viewModel.IsFileDragActive);
        Assert.Equal("notes.txt", Assert.Single(viewModel.Attachments).FileName);
        Assert.True(viewModel.HasAttachments);
    }

    [Fact]
    public async Task SendCommand_WhenAttachmentIsSelected_UploadsOnceThenSendsOneMarkdownMessage()
    {
        var conversation = new DirectMessage([8]);
        var filePicker = new FakeFileSelectionService
        {
            Files =
            [
                new SelectedAttachmentFile(
                    "design[1].png",
                    "image/png",
                    3,
                    _ => Task.FromResult<Stream>(new MemoryStream([1, 2, 3])))
            ]
        };
        var session = new FakeSession
        {
            Selected = conversation,
            StateValue = new ClientState(connection: new ConnectionState(ConnectionStatus.Connected))
        };
        using var viewModel = CreateViewModel(session, fileSelectionService: filePicker);
        session.Publish();

        await ((IAsyncRelayCommand)viewModel.PickAttachmentsCommand).ExecuteAsync(null);
        viewModel.ComposerText = "caption";
        await ((IAsyncRelayCommand)viewModel.SendCommand).ExecuteAsync(null);

        Assert.Equal(1, session.UploadCalls);
        Assert.Equal("caption\n[design\\[1\\].png](https://example.test/user_uploads/design[1].png)", Assert.Single(session.SentContents));
        Assert.Empty(viewModel.Attachments);
        Assert.Equal(string.Empty, viewModel.ComposerText);
    }

    [Fact]
    public async Task SendCommand_WhenUploadSucceededButSendFailed_ReusesUploadedReferenceOnExplicitRetry()
    {
        var conversation = new DirectMessage([8]);
        var filePicker = new FakeFileSelectionService
        {
            Files =
            [
                new SelectedAttachmentFile(
                    "notes.txt",
                    "text/plain",
                    3,
                    _ => Task.FromResult<Stream>(new MemoryStream([1, 2, 3])))
            ]
        };
        var failSend = true;
        var session = new FakeSession
        {
            Selected = conversation,
            StateValue = new ClientState(connection: new ConnectionState(ConnectionStatus.Connected)),
            SendAction = (_, _) => failSend
                ? throw new GatewayException(GatewayErrorKind.Offline, GatewayErrorCode.NetworkError)
                : Task.CompletedTask
        };
        using var viewModel = CreateViewModel(session, fileSelectionService: filePicker);
        session.Publish();
        await ((IAsyncRelayCommand)viewModel.PickAttachmentsCommand).ExecuteAsync(null);

        await ((IAsyncRelayCommand)viewModel.SendCommand).ExecuteAsync(null);
        failSend = false;
        session.StateValue = new ClientState(connection: new ConnectionState(ConnectionStatus.Connected));
        session.Publish();
        await ((IAsyncRelayCommand)viewModel.SendCommand).ExecuteAsync(null);

        Assert.Equal(1, session.UploadCalls);
        Assert.Equal(2, session.SentContents.Count);
        Assert.Empty(viewModel.Attachments);
    }

    [Fact]
    public void OpenImageViewerCommand_WhenAttachmentIsImage_OpensModalAndCloseRestoresShell()
    {
        using var viewModel = CreateViewModel(new FakeSession());
        var image = new MessageAttachmentItem(
            "image",
            "preview.png",
            "https://chat.example.test/user_uploads/7/preview.png");

        viewModel.OpenImageViewerCommand.Execute(image);

        Assert.True(viewModel.IsImageViewerOpen);
        Assert.False(viewModel.IsPrimaryShellEnabled);
        Assert.Same(image, viewModel.ActiveImageAttachment);

        viewModel.CloseImageViewerCommand.Execute(null);

        Assert.False(viewModel.IsImageViewerOpen);
        Assert.True(viewModel.IsPrimaryShellEnabled);
        Assert.Null(viewModel.ActiveImageAttachment);
    }

    [Fact]
    public async Task DownloadAttachmentCommand_WhenControlledReadSucceeds_SavesExactBytes()
    {
        var media = new FakeRealmMediaService
        {
            FileResult = new RealmMediaResult([4, 5, 6], "application/pdf")
        };
        var save = new FakeFileSaveService();
        using var viewModel = CreateViewModel(
            new FakeSession(),
            realmMediaService: media,
            fileSaveService: save);
        var attachment = new MessageAttachmentItem(
            "file",
            "guide.pdf",
            "https://chat.example.test/user_uploads/7/guide.pdf");

        await ((IAsyncRelayCommand)viewModel.DownloadAttachmentCommand).ExecuteAsync(attachment);

        Assert.Equal(1, media.FileCalls);
        Assert.Equal("guide.pdf", save.FileName);
        Assert.Equal([4, 5, 6], save.Content);
        Assert.Equal("已保存 guide.pdf。", viewModel.MediaActionStatus);
    }

    private static ShellViewModel CreateViewModel(
        IClientSession session,
        FakeLastRealmStore? lastRealmStore = null,
        FakeAppearanceService? appearanceService = null,
        FakeUiPreferencesService? uiPreferencesService = null,
        FakePlatformInteractionService? platformInteractions = null,
        FakeFileSelectionService? fileSelectionService = null,
        FakeRealmMediaService? realmMediaService = null,
        FakeFileSaveService? fileSaveService = null) =>
        new(
            session,
            lastRealmStore ?? new FakeLastRealmStore(),
            new InlineDispatcher(),
            appearanceService ?? new FakeAppearanceService(),
            uiPreferencesService ?? new FakeUiPreferencesService(),
            platformInteractions ?? new FakePlatformInteractionService(),
            fileSelectionService ?? new FakeFileSelectionService(),
            realmMediaService ?? new FakeRealmMediaService(),
            fileSaveService ?? new FakeFileSaveService());

    private sealed class FakeUiPreferencesService : IUiPreferencesService
    {
        public UiPreferences Current { get; set; } = new();
        public List<UiPreferences> Saved { get; } = [];

        public void Save(UiPreferences preferences)
        {
            Current = preferences;
            Saved.Add(preferences);
        }

        public UiPreferences Reset()
        {
            Current = new UiPreferences();
            Saved.Add(Current);
            return Current;
        }
    }

    private sealed class FakeRealmMediaService : IRealmMediaService
    {
        public RealmMediaResult FileResult { get; set; } = new([1, 2, 3], "application/octet-stream");
        public int FileCalls { get; private set; }

        public Task<Microsoft.Maui.Controls.ImageSource> GetImageAsync(
            string sourceUrl,
            RealmMediaKind kind,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Microsoft.Maui.Controls.ImageSource>(
                Microsoft.Maui.Controls.ImageSource.FromStream(() => new MemoryStream([1])));

        public Task<RealmMediaResult> GetFileAsync(
            string sourceUrl,
            CancellationToken cancellationToken = default)
        {
            FileCalls++;
            return Task.FromResult(FileResult);
        }
    }

    private sealed class FakeFileSaveService : IFileSaveService
    {
        public bool Result { get; set; } = true;
        public string? FileName { get; private set; }
        public byte[]? Content { get; private set; }

        public Task<bool> SaveAsync(
            string fileName,
            byte[] content,
            CancellationToken cancellationToken = default)
        {
            FileName = fileName;
            Content = content;
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeFileSelectionService : IFileSelectionService
    {
        public IReadOnlyList<SelectedAttachmentFile> Files { get; set; } = [];
        public Task<IReadOnlyList<SelectedAttachmentFile>> PickMultipleAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Files);
    }

    private sealed class FakePlatformInteractionService : IPlatformInteractionService
    {
        public List<string> Copied { get; } = [];
        public List<Uri> Opened { get; } = [];

        public Task CopyTextAsync(string text, CancellationToken cancellationToken = default)
        {
            Copied.Add(text);
            return Task.CompletedTask;
        }

        public Task OpenUriAsync(Uri uri, CancellationToken cancellationToken = default)
        {
            Opened.Add(uri);
            return Task.CompletedTask;
        }
    }

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public void Dispatch(Action action) => action();
    }

    private sealed class FakeLastRealmStore(string? value = null) : ILastRealmStore
    {
        private string? _value = value;

        public string Get() => _value ?? PreferencesLastRealmStore.DefaultRealm;
        public void Set(string realm) => _value = realm;
    }

    private sealed class FakeAppearanceService : IAppearanceService
    {
        public List<AppAppearanceMode> Applied { get; } = [];
        public AppAppearanceMode Current { get; private set; } = AppAppearanceMode.System;

        public void Apply(AppAppearanceMode mode)
        {
            Current = mode;
            Applied.Add(mode);
        }
    }

    [Fact]
    public async Task ChannelBrowser_CloseCommand_ClearsStateAndIgnoresLateCatalog()
    {
        var completion = new TaskCompletionSource<IReadOnlyList<ChannelSummary>>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken receivedToken = default;
        var session = new FakeSession
        {
            Account = AccountId.Create(RealmEndpoint.Parse("https://chat.example.test/"), 10),
            StateValue = new ClientState(connection: new ConnectionState(ConnectionStatus.Connected)),
            AvailableChannelsAction = token =>
            {
                receivedToken = token;
                return completion.Task;
            }
        };
        using var viewModel = CreateViewModel(session);

        var open = ((IAsyncRelayCommand)viewModel.OpenChannelBrowserCommand).ExecuteAsync(null);
        await Task.Yield();
        viewModel.CloseChannelBrowserCommand.Execute(null);
        Assert.True(receivedToken.IsCancellationRequested);
        completion.SetResult([new ChannelSummary(4, "late", null, false, null)]);
        await open;

        Assert.False(viewModel.IsChannelBrowserOpen);
        Assert.False(viewModel.IsChannelBrowserLoading);
        Assert.Empty(viewModel.AvailableChannels);
        Assert.Null(viewModel.ChannelBrowserError);
    }

    [Fact]
    public void Projection_WhenSelectedSubscriptionPreferenceChanges_NotifiesActionLabels()
    {
        var conversation = new ChannelTopic(7, "topic");
        var session = new FakeSession
        {
            Selected = conversation,
            StateValue = new ClientState(
                connection: new ConnectionState(ConnectionStatus.Connected),
                subscriptions: new Dictionary<long, Subscription> { [7] = new Subscription(7, "engineering") })
        };
        using var viewModel = CreateViewModel(session);
        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, eventArgs) => changed.Add(eventArgs.PropertyName);

        session.StateValue = session.StateValue with
        {
            Subscriptions = new Dictionary<long, Subscription> { [7] = new Subscription(7, "engineering", isMuted: true, isPinned: true) }
        };
        session.Publish();

        Assert.Contains(nameof(ShellViewModel.SelectedChannelMuteLabel), changed);
        Assert.Contains(nameof(ShellViewModel.SelectedChannelPinLabel), changed);
        Assert.Equal("取消静音", viewModel.SelectedChannelMuteLabel);
        Assert.Equal("取消置顶", viewModel.SelectedChannelPinLabel);
    }

    private sealed class FakeSession : IClientSession
    {
        public ClientState StateValue { get; set; } = ClientState.Empty;
        public ConversationKey? Selected { get; set; }
        public IReadOnlyList<ConversationKey> Recent { get; set; } = [];
        public AccountId? Account { get; set; }
        public Func<string, string, string, CancellationToken, Task>? LoginAction { get; set; }
        public Func<CancellationToken, Task>? LogoutAction { get; set; }
        public Func<string, CancellationToken, Task>? SendAction { get; set; }
        public Func<AttachmentUpload, CancellationToken, Task<UploadedAttachment>>? UploadAction { get; set; }
        public Func<long, CancellationToken, Task>? UnsubscribeChannelAction { get; set; }
        public Func<long, CancellationToken, Task<IReadOnlyList<TopicSummary>>>? LoadTopicsAction { get; set; }
        public Func<CancellationToken, Task>? LoadOlderAction { get; set; }
        public Func<long?, int, CancellationToken, Task<MessageQueryPage>>? SavedMessagesAction { get; set; }
        public Func<string, long?, int, CancellationToken, Task<MessageQueryPage>>? SearchMessagesAction { get; set; }
        public Func<CancellationToken, Task<IReadOnlyList<ChannelSummary>>>? AvailableChannelsAction { get; set; }
        public int LoginCalls { get; private set; }
        public List<string> SentContents { get; } = [];
        public int UploadCalls { get; private set; }
        public int LoadOlderCalls { get; private set; }

        public AccountId? AccountId => Account;
        public RealmEndpoint? ActiveRealm { get; set; }
        public long? CurrentUserId { get; set; }
        public long MaxFileUploadBytes { get; set; } = 10L * 1024 * 1024;
        public ClientState State => StateValue;
        public ConversationKey? SelectedConversation => Selected;
        public ConversationHistoryState HistoryState { get; set; } = ConversationHistoryState.Empty;
        public IReadOnlyList<ConversationKey> RecentDirectMessages => Recent;
        public event EventHandler<ClientStateChangedEventArgs>? StateChanged;

        public Task<bool> RestoreAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task LoginAsync(string realm, string email, string password, CancellationToken cancellationToken = default)
        {
            LoginCalls++;
            return LoginAction?.Invoke(realm, email, password, cancellationToken) ?? Task.CompletedTask;
        }

        public Task LogoutAsync(CancellationToken cancellationToken = default) =>
            LogoutAction?.Invoke(cancellationToken) ?? Task.CompletedTask;
        public Task SelectConversationAsync(ConversationKey conversation, CancellationToken cancellationToken = default)
        {
            Selected = conversation;
            return Task.CompletedTask;
        }

        public Task LoadOlderAsync(CancellationToken cancellationToken = default)
        {
            LoadOlderCalls++;
            return LoadOlderAction?.Invoke(cancellationToken) ?? Task.CompletedTask;
        }
        public Task<IReadOnlyList<TopicSummary>> LoadTopicsAsync(long channelId, CancellationToken cancellationToken = default) =>
            LoadTopicsAction?.Invoke(channelId, cancellationToken) ?? Task.FromResult<IReadOnlyList<TopicSummary>>([]);
        public Task<MessageQueryPage> LoadSavedMessagesAsync(long? beforeMessageId, int limit, CancellationToken cancellationToken = default) =>
            SavedMessagesAction?.Invoke(beforeMessageId, limit, cancellationToken) ?? Task.FromResult(new MessageQueryPage([], false, true, true));
        public Task<MessageQueryPage> SearchMessagesAsync(string query, long? beforeMessageId, int limit, CancellationToken cancellationToken = default) =>
            SearchMessagesAction?.Invoke(query, beforeMessageId, limit, cancellationToken) ?? Task.FromResult(new MessageQueryPage([], false, true, true));
        public Task<IReadOnlyList<ChannelSummary>> GetAvailableChannelsAsync(CancellationToken cancellationToken = default) =>
            AvailableChannelsAction?.Invoke(cancellationToken) ?? Task.FromResult<IReadOnlyList<ChannelSummary>>([]);

        public Task SendAsync(string content, CancellationToken cancellationToken = default)
        {
            SentContents.Add(content);
            return SendAction?.Invoke(content, cancellationToken) ?? Task.CompletedTask;
        }
        public Task SetReactionAsync(long messageId, EmojiReactionIdentity reaction, bool add, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task EditMessageAsync(long messageId, string content, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteMessageAsync(long messageId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetMessageStarredAsync(long messageId, bool isStarred, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<UploadedAttachment> UploadAttachmentAsync(AttachmentUpload upload, CancellationToken cancellationToken = default)
        {
            UploadCalls++;
            return UploadAction?.Invoke(upload, cancellationToken) ??
                Task.FromResult(new UploadedAttachment(upload.FileName, $"https://example.test/user_uploads/{upload.FileName}"));
        }
        public Task<RealmMediaResult> GetRealmMediaAsync(RealmMediaRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new RealmMediaResult([1], "image/png"));
        public Task UnsubscribeChannelAsync(long channelId, CancellationToken cancellationToken = default) =>
            UnsubscribeChannelAction?.Invoke(channelId, cancellationToken) ?? Task.CompletedTask;
        public Task MarkDisplayedReadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ClearLocalCacheAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void Publish() => StateChanged?.Invoke(this, new ClientStateChangedEventArgs(StateValue));
    }
}
