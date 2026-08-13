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
    public void SelectedChannel_WhenTopicLoadPublishesOldConversation_KeepsBrowsedChannel()
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
        Assert.Equal(channelA, session.SelectedConversation);
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
        FakeSession session,
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
        public Func<long, CancellationToken, Task<IReadOnlyList<TopicSummary>>>? LoadTopicsAction { get; set; }
        public int LoginCalls { get; private set; }
        public List<string> SentContents { get; } = [];
        public int UploadCalls { get; private set; }

        public AccountId? AccountId => Account;
        public RealmEndpoint? ActiveRealm { get; set; }
        public long? CurrentUserId { get; set; }
        public long MaxFileUploadBytes { get; set; } = 10L * 1024 * 1024;
        public ClientState State => StateValue;
        public ConversationKey? SelectedConversation => Selected;
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

        public Task LoadOlderAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<TopicSummary>> LoadTopicsAsync(long channelId, CancellationToken cancellationToken = default) =>
            LoadTopicsAction?.Invoke(channelId, cancellationToken) ?? Task.FromResult<IReadOnlyList<TopicSummary>>([]);

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
        public Task MarkDisplayedReadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ClearLocalCacheAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void Publish() => StateChanged?.Invoke(this, new ClientStateChangedEventArgs(StateValue));
    }
}
