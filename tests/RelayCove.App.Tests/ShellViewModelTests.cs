using System.Collections.Specialized;
using CommunityToolkit.Mvvm.Input;
using RelayCove.App.Services;
using RelayCove.App.ViewModels;
using RelayCove.Core;

namespace RelayCove.App.Tests;

public sealed class ShellViewModelTests
{
    [Fact]
    public void DirectConversation_WhenOfficialPresenceAndUserStatusAreAvailable_ShowsBothLayers()
    {
        var direct = new DirectMessage([20]);
        var now = DateTimeOffset.UtcNow;
        var session = new FakeSession
        {
            Account = AccountId.Create(RealmEndpoint.Parse("https://zulip.example"), 10),
            CurrentUserId = 10,
            Selected = direct,
            Recent = [direct],
            StateValue = new ClientState(
                users: new Dictionary<long, UserProfile>
                {
                    [10] = new UserProfile(10, "Ada"),
                    [20] = new UserProfile(20, "Bea")
                },
                connection: new ConnectionState(ConnectionStatus.Connected),
                presence: new PresenceState(true, new Dictionary<long, UserPresence>
                {
                    [20] = new UserPresence(20, now, now)
                }),
                userStatuses: new UserStatusState(true, new Dictionary<long, UserStatusContent>
                {
                    [20] = new UserStatusContent(
                        "会议中",
                        new EmojiReactionIdentity("calendar", "1f4c5", "unicode_emoji"))
                }))
        };

        using var viewModel = CreateViewModel(session);

        var item = Assert.Single(viewModel.Conversations);
        Assert.True(item.HasPresence);
        Assert.Equal(UserPresenceStatus.Active, item.PresenceStatus);
        Assert.Equal("在线", item.PresenceLabel);
        Assert.Equal("📅", item.UserStatusGlyph);
        Assert.Equal("会议中", item.UserStatusDescription);
        Assert.Equal("在线 · 📅 会议中", viewModel.ConversationSubtitle);
    }

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
    public async Task ShowSaved_WhenOpenedFromAccountMenu_UsesWorkspaceContentAndClosesMenu()
    {
        var session = new FakeSession
        {
            Account = AccountId.Create(RealmEndpoint.Parse("https://zulip.example"), 10)
        };
        using var viewModel = CreateViewModel(session);
        viewModel.UpdateViewport(640);
        viewModel.ToggleAccountMenuCommand.Execute(null);

        await ((IAsyncRelayCommand)viewModel.ShowSavedCommand).ExecuteAsync(null);

        Assert.False(viewModel.IsAccountMenuOpen);
        Assert.True(viewModel.IsSavedSection);
        Assert.True(viewModel.IsConversationWorkspaceSection);
        Assert.False(viewModel.IsConversationPaneVisible);
        Assert.True(viewModel.IsWorkspaceContentPaneVisible);
    }

    [Fact]
    public async Task OpenSavedMessage_WhenAroundPageLoads_QueuesExactMessageAnchor()
    {
        var conversation = new DirectMessage([20]);
        var anchor = new ChatMessage(75, conversation, 20, "saved", DateTimeOffset.UnixEpoch);
        var session = new FakeSession
        {
            Account = AccountId.Create(RealmEndpoint.Parse("https://zulip.example"), 10),
            CurrentUserId = 10,
            StateValue = new ClientState(
                users: new Dictionary<long, UserProfile> { [20] = new UserProfile(20, "Bea") },
                connection: new ConnectionState(ConnectionStatus.Connected))
        };
        session.OpenMessageAction = (openedConversation, messageId, _) =>
        {
            session.Selected = openedConversation;
            session.HistoryState = new ConversationHistoryState(openedConversation, 3, false, false, false, 51, null);
            session.StateValue = session.StateValue with
            {
                Messages = new Dictionary<long, ChatMessage>
                {
                    [74] = new ChatMessage(74, conversation, 20, "before", DateTimeOffset.UnixEpoch),
                    [anchor.Id] = anchor,
                    [76] = new ChatMessage(76, conversation, 20, "after", DateTimeOffset.UnixEpoch)
                }
            };
            session.Publish();
            return Task.CompletedTask;
        };
        using var viewModel = CreateViewModel(session);

        await ((IAsyncRelayCommand<SavedMessageItem?>)viewModel.OpenSavedMessageCommand).ExecuteAsync(
            new SavedMessageItem(anchor.Id, conversation, "Bea", anchor.Content, "13:47"));

        var request = Assert.IsType<MessageScrollRequest>(viewModel.PendingMessageScrollRequest);
        Assert.Equal(anchor.Id, request.TargetMessageId);
        Assert.Equal(MessageScrollReason.MessageAnchor, request.Reason);
        Assert.True(viewModel.IsMessagesSection);
    }

    [Fact]
    public async Task SelectSearchResult_WhenAroundPageLoads_QueuesExactMessageAnchorAndClosesSearch()
    {
        var conversation = new DirectMessage([20]);
        var anchor = new ChatMessage(85, conversation, 20, "matched", DateTimeOffset.UnixEpoch);
        var session = new FakeSession
        {
            Account = AccountId.Create(RealmEndpoint.Parse("https://zulip.example"), 10),
            CurrentUserId = 10,
            StateValue = new ClientState(
                users: new Dictionary<long, UserProfile> { [20] = new UserProfile(20, "Bea") },
                connection: new ConnectionState(ConnectionStatus.Connected))
        };
        session.OpenMessageAction = (openedConversation, messageId, _) =>
        {
            session.Selected = openedConversation;
            session.HistoryState = new ConversationHistoryState(openedConversation, 3, false, false, false, 81, null);
            session.StateValue = session.StateValue with
            {
                Messages = new Dictionary<long, ChatMessage>
                {
                    [84] = new ChatMessage(84, conversation, 20, "before", DateTimeOffset.UnixEpoch),
                    [anchor.Id] = anchor,
                    [86] = new ChatMessage(86, conversation, 20, "after", DateTimeOffset.UnixEpoch)
                }
            };
            session.Publish();
            return Task.CompletedTask;
        };
        using var viewModel = CreateViewModel(session);
        viewModel.OpenSearchCommand.Execute(null);

        await ((IAsyncRelayCommand<SearchResultItem?>)viewModel.SelectSearchResultCommand).ExecuteAsync(
            new SearchResultItem("server:85", "服务器消息", "Bea", anchor.Content, conversation, anchor.Id));

        Assert.Equal((conversation, anchor.Id), Assert.Single(session.OpenedMessages));
        var request = Assert.IsType<MessageScrollRequest>(viewModel.PendingMessageScrollRequest);
        Assert.Equal(anchor.Id, request.TargetMessageId);
        Assert.Equal(MessageScrollReason.MessageAnchor, request.Reason);
        Assert.True(viewModel.IsMessagesSection);
        Assert.False(viewModel.IsSearchOpen);
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
    public async Task OpenRegistrationCommand_WhenRealmIsValid_OpensOfficialSameOriginRegistrationPage()
    {
        var interactions = new FakePlatformInteractionService();
        using var viewModel = CreateViewModel(new FakeSession(), platformInteractions: interactions);
        viewModel.Realm = "https://Chat.Example.Test/";

        await ((IAsyncRelayCommand)viewModel.OpenRegistrationCommand).ExecuteAsync(null);

        Assert.Equal(new Uri("https://chat.example.test/register/"), Assert.Single(interactions.Opened));
        Assert.Null(viewModel.LoginError);
    }

    [Fact]
    public async Task OpenRegistrationCommand_WhenRealmIsInvalid_DoesNotOpenBrowser()
    {
        var interactions = new FakePlatformInteractionService();
        using var viewModel = CreateViewModel(new FakeSession(), platformInteractions: interactions);
        viewModel.Realm = "http://chat.example.test";

        await ((IAsyncRelayCommand)viewModel.OpenRegistrationCommand).ExecuteAsync(null);

        Assert.Empty(interactions.Opened);
        Assert.Equal("请先输入有效的 HTTPS Realm 地址。", viewModel.LoginError);
    }

    [Fact]
    public void SessionStateChanged_WhenSubscribedAndRecentDirectMessage_ProjectsNavigationAndRawMessage()
    {
        var direct = new DirectMessage([8]);
        var channel = new ChannelTopic(4, string.Empty);
        var state = new ClientState(
            messages: new Dictionary<long, ChatMessage>
            {
                [11] = new ChatMessage(11, channel, 7, "**raw markdown**", DateTimeOffset.UnixEpoch, senderDisplayName: "Ada")
            },
            subscriptions: new Dictionary<long, Subscription> { [4] = PrivateGroupSubscription(4, "engineering") },
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
        var channel = new ChannelTopic(4, string.Empty);
        var direct = new DirectMessage([8]);
        var state = new ClientState(
            subscriptions: new Dictionary<long, Subscription>
            {
                [4] = PrivateGroupSubscription(4, "engineering") with { IsMuted = true, IsPinned = true }
            },
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
        Assert.True(viewModel.ShowChannelDetails);
        Assert.Equal("私有群聊", viewModel.DetailsKindLabel);
        Assert.Contains("私有群聊", viewModel.DetailsIdentifierLabel, StringComparison.Ordinal);
        Assert.Contains("未读 5 条", viewModel.DetailsStateLabel, StringComparison.Ordinal);
        Assert.True(viewModel.IsSelectedChannelMuted);
        Assert.True(viewModel.IsSelectedChannelPinned);
        Assert.Empty(viewModel.DetailsUnavailableMessage);
    }

    [Fact]
    public void Projection_WhenOneToOneDirectMessageIsSelected_ShowsUserFacingIdentityOnly()
    {
        var direct = new DirectMessage([8]);
        var state = new ClientState(
            users: new Dictionary<long, UserProfile>
            {
                [7] = new UserProfile(7, "Dal"),
                [8] = new UserProfile(8, "Bea")
            },
            unread: new UnreadState(new Dictionary<string, int> { [direct.CanonicalKey] = 3 }),
            connection: new ConnectionState(RelayCove.Core.ConnectionStatus.Connected));
        var session = new FakeSession { StateValue = state, Recent = [direct], Selected = direct, CurrentUserId = 7 };
        using var viewModel = CreateViewModel(session);

        session.Publish();

        Assert.False(viewModel.ShowChannelDetails);
        Assert.Equal("私信", viewModel.DetailsKindLabel);
        Assert.Equal("Bea", viewModel.DetailsTitle);
        Assert.Equal("与 Bea 的私信", viewModel.DetailsBody);
        Assert.Equal("一对一私信 · 2 位参与者", viewModel.DetailsIdentifierLabel);
        Assert.Empty(viewModel.DetailsStateLabel);
        Assert.Empty(viewModel.DetailsAvailableMessage);
        Assert.Empty(viewModel.DetailsUnavailableMessage);
        AssertDirectMessageDetailsAreUserFacing(viewModel);
    }

    [Fact]
    public void Projection_WhenSelfDirectMessageIsSelected_ShowsSelfIdentityOnly()
    {
        var direct = new DirectMessage([]);
        var state = new ClientState(
            users: new Dictionary<long, UserProfile> { [7] = new UserProfile(7, "Dal") },
            connection: new ConnectionState(RelayCove.Core.ConnectionStatus.Connected));
        var session = new FakeSession { StateValue = state, Recent = [direct], Selected = direct, CurrentUserId = 7 };
        using var viewModel = CreateViewModel(session);

        session.Publish();

        Assert.False(viewModel.ShowChannelDetails);
        Assert.Equal("给自己", viewModel.DetailsKindLabel);
        Assert.Equal("Dal（自己）", viewModel.DetailsTitle);
        Assert.Equal("仅你自己可见", viewModel.DetailsBody);
        Assert.Equal("给自己的私信", viewModel.DetailsIdentifierLabel);
        Assert.Empty(viewModel.DetailsStateLabel);
        Assert.Empty(viewModel.DetailsAvailableMessage);
        Assert.Empty(viewModel.DetailsUnavailableMessage);
        AssertDirectMessageDetailsAreUserFacing(viewModel);
    }

    [Fact]
    public void Projection_WhenGroupDirectMessageIsSelected_HidesUnsupportedConversation()
    {
        var direct = new DirectMessage([8, 9]);
        var state = new ClientState(
            users: new Dictionary<long, UserProfile>
            {
                [7] = new UserProfile(7, "Dal"),
                [8] = new UserProfile(8, "Bea, Jr."),
                [9] = new UserProfile(9, "Cai")
            },
            connection: new ConnectionState(RelayCove.Core.ConnectionStatus.Connected));
        var session = new FakeSession { StateValue = state, Recent = [direct], Selected = direct, CurrentUserId = 7 };
        using var viewModel = CreateViewModel(session);

        session.Publish();

        Assert.False(viewModel.HasSelectedConversation);
        Assert.False(viewModel.ShowChannelDetails);
        Assert.Equal("会话", viewModel.DetailsKindLabel);
        Assert.Equal("会话详情", viewModel.DetailsTitle);
        Assert.DoesNotContain(viewModel.Conversations, item => item.Conversation == direct);
    }

    private static void AssertDirectMessageDetailsAreUserFacing(ShellViewModel viewModel)
    {
        var visibleDetails = $"{viewModel.DetailsKindLabel}\n{viewModel.DetailsTitle}\n{viewModel.DetailsBody}";

        foreach (var technicalTerm in new[] { "可靠参与者", "已接通", "能力边界", "presence", "Realm" })
        {
            Assert.DoesNotContain(technicalTerm, visibleDetails, StringComparison.OrdinalIgnoreCase);
        }
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
    public void NotificationBadge_WhenUnreadProjectionChanges_UsesAuthorityWithoutInventingToast()
    {
        var conversation = new DirectMessage([8]);
        var notifications = new FakeAppNotificationService();
        var session = new FakeSession
        {
            Recent = [conversation],
            StateValue = new ClientState(
                users: new Dictionary<long, UserProfile>
                {
                    [8] = new UserProfile(8, "Bea", avatarUrl: "https://zulip.example/avatar/8")
                },
                unread: new UnreadState(
                    new Dictionary<string, int> { [conversation.CanonicalKey] = 125 },
                    reportedTotal: 125),
                connection: new ConnectionState(ConnectionStatus.Connected))
        };
        using var viewModel = CreateViewModel(session, appNotificationService: notifications);

        session.Publish();

        Assert.Equal((125, false), notifications.BadgeUpdates[^1]);
        Assert.Equal((125, false), notifications.TrayUnreadUpdates[^1]);
        Assert.Empty(notifications.Notifications);
        Assert.Equal(0, notifications.FlashCalls);

        viewModel.TaskbarBadgeEnabled = false;
        Assert.Equal((0, false), notifications.BadgeUpdates[^1]);
        Assert.Equal((125, false), notifications.TrayUnreadUpdates[^1]);
    }

    [Fact]
    public void RealtimeMessage_WhenIncomingConversationIsNotVisible_ShowsToastAndFlashes()
    {
        var conversation = new DirectMessage([8]);
        var notifications = new FakeAppNotificationService();
        var session = new FakeSession
        {
            Account = AccountId.Create(RealmEndpoint.Parse("https://zulip.example"), 7),
            CurrentUserId = 7,
            Recent = [conversation],
            StateValue = new ClientState(
                users: new Dictionary<long, UserProfile>
                {
                    [8] = new UserProfile(8, "Bea", avatarUrl: "https://zulip.example/avatar/8")
                },
                connection: new ConnectionState(ConnectionStatus.Connected))
        };
        using var viewModel = CreateViewModel(session, appNotificationService: notifications);

        session.PublishRealtime(new ChatMessage(
            11,
            conversation,
            8,
            "你好\n第二行",
            DateTimeOffset.UnixEpoch,
            senderDisplayName: "Bea"));

        var notification = Assert.Single(notifications.Notifications);
        Assert.Same(notification, Assert.Single(notifications.TrayPreviews));
        Assert.Equal(conversation.CanonicalKey, notification.ConversationKey);
        Assert.Equal("Bea", notification.Title);
        Assert.Equal("你好 第二行", notification.Body);
        Assert.Equal("https://zulip.example/avatar/8", notification.SenderAvatarUrl);
        Assert.Equal(1, notifications.FlashCalls);

        session.PublishRealtime(new ChatMessage(12, conversation, 7, "自己发送", DateTimeOffset.UnixEpoch));
        viewModel.DoNotDisturb = true;
        session.PublishRealtime(new ChatMessage(13, conversation, 8, "免打扰", DateTimeOffset.UnixEpoch));

        Assert.Single(notifications.Notifications);
        Assert.Equal(1, notifications.FlashCalls);
        Assert.True(notifications.StopTrayFlashCalls > 0);
    }

    [Fact]
    public void RealtimeMessage_WhenSystemToastIsDisabled_StillUpdatesTrayPreview()
    {
        var conversation = new DirectMessage([8]);
        var notifications = new FakeAppNotificationService();
        var session = new FakeSession
        {
            Account = AccountId.Create(RealmEndpoint.Parse("https://zulip.example"), 7),
            CurrentUserId = 7,
            Recent = [conversation],
            StateValue = new ClientState(
                users: new Dictionary<long, UserProfile> { [8] = new UserProfile(8, "Bea") },
                connection: new ConnectionState(ConnectionStatus.Connected))
        };
        using var viewModel = CreateViewModel(session, appNotificationService: notifications);
        viewModel.SystemNotificationsEnabled = false;

        session.PublishRealtime(new ChatMessage(
            11,
            conversation,
            8,
            "托盘摘要",
            DateTimeOffset.UnixEpoch,
            senderDisplayName: "Bea"));

        Assert.Empty(notifications.Notifications);
        var preview = Assert.Single(notifications.TrayPreviews);
        Assert.Equal("Bea", preview.Title);
        Assert.Equal("托盘摘要", preview.Body);
        Assert.Equal(1, notifications.FlashCalls);
    }

    [Fact]
    public void RealtimeMessage_WhenConversationIsMutedOrCurrentlyVisible_SuppressesAttention()
    {
        var conversation = new DirectMessage([8]);
        var accountId = AccountId.Create(RealmEndpoint.Parse("https://zulip.example"), 7);
        var preferences = new InMemoryConversationPreferencesStore();
        preferences.Save(accountId, conversation.CanonicalKey, new ConversationPreference(IsMuted: true));
        var notifications = new FakeAppNotificationService();
        var message = new ChatMessage(11, conversation, 8, "hello", DateTimeOffset.UnixEpoch);
        var session = new FakeSession
        {
            Account = accountId,
            CurrentUserId = 7,
            Selected = conversation,
            Recent = [conversation],
            HistoryState = new ConversationHistoryState(conversation, 1, false, true, false, 11, null),
            StateValue = new ClientState(
                messages: new Dictionary<long, ChatMessage> { [11] = message },
                users: new Dictionary<long, UserProfile> { [8] = new UserProfile(8, "Bea") },
                unread: new UnreadState(new Dictionary<string, int> { [conversation.CanonicalKey] = 1 }),
                connection: new ConnectionState(ConnectionStatus.Connected))
        };
        using var viewModel = CreateViewModel(
            session,
            conversationPreferencesStore: preferences,
            appNotificationService: notifications);

        session.PublishRealtime(message);
        Assert.Empty(notifications.Notifications);
        Assert.Equal((1, false), notifications.BadgeUpdates[^1]);

        preferences.Save(accountId, conversation.CanonicalKey, new ConversationPreference());
        viewModel.SetWindowActive(true);
        viewModel.ReportMessageBottomDistance(200d);
        session.PublishRealtime(message with { Id = 12 });

        Assert.Empty(notifications.Notifications);
        Assert.Equal(0, notifications.FlashCalls);
    }

    [Fact]
    public async Task NotificationActivation_WhenConversationExists_OpensThatConversation()
    {
        var conversation = new DirectMessage([8]);
        var notifications = new FakeAppNotificationService();
        var session = new FakeSession
        {
            Recent = [conversation],
            StateValue = new ClientState(
                users: new Dictionary<long, UserProfile> { [8] = new UserProfile(8, "Bea") },
                connection: new ConnectionState(ConnectionStatus.Connected))
        };
        using var viewModel = CreateViewModel(session, appNotificationService: notifications);

        notifications.Activate(conversation.CanonicalKey);
        await WaitUntilAsync(() => session.SelectedConversation == conversation);

        Assert.True(viewModel.IsMessagesSection);
        Assert.Equal(conversation, session.SelectedConversation);
        Assert.True(notifications.StopFlashCalls > 0);
    }

    [Fact]
    public void NotificationPreferences_WhenChanged_PersistIndependentlyFromAppearance()
    {
        var preferences = new FakeNotificationPreferencesService
        {
            Current = new NotificationPreferences(DoNotDisturb: true)
        };
        using var viewModel = CreateViewModel(
            new FakeSession(),
            notificationPreferencesService: preferences,
            appNotificationService: new FakeAppNotificationService());

        Assert.True(viewModel.DoNotDisturb);
        viewModel.DoNotDisturb = false;
        viewModel.ShowMessagePreview = false;

        Assert.False(preferences.Current.DoNotDisturb);
        Assert.False(preferences.Current.ShowMessagePreview);
        Assert.Equal(2, preferences.Saved.Count);
    }

    [Fact]
    public async Task AutoMarkRead_WhenTaskbarPreviewIsHovered_WaitsForRealForegroundWindow()
    {
        var conversation = new DirectMessage([8]);
        var unread = new ChatMessage(11, conversation, 8, "new", DateTimeOffset.UnixEpoch);
        var window = new FakeWindowShellAdapter { IsForeground = false };
        var session = new FakeSession
        {
            Account = AccountId.Create(RealmEndpoint.Parse("https://zulip.example"), 7),
            CurrentUserId = 7,
            Selected = conversation,
            Recent = [conversation],
            HistoryState = new ConversationHistoryState(conversation, 1, false, true, false, 11, null),
            StateValue = new ClientState(
                messages: new Dictionary<long, ChatMessage> { [11] = unread },
                users: new Dictionary<long, UserProfile> { [8] = new UserProfile(8, "Bea") },
                unread: new UnreadState(new Dictionary<string, int> { [conversation.CanonicalKey] = 1 }),
                connection: new ConnectionState(ConnectionStatus.Connected))
        };
        using var viewModel = CreateViewModel(session, windowShellAdapter: window);
        viewModel.AcknowledgeMessageScrollRequest(Assert.IsType<MessageScrollRequest>(viewModel.PendingMessageScrollRequest));

        viewModel.SetWindowActive(true);
        session.Publish();

        Assert.Empty(session.ExpectedMarkReadConversations);
        Assert.True(viewModel.HasNavigationUnread);

        window.IsForeground = true;
        viewModel.SetWindowActive(true);
        await WaitUntilAsync(() => session.ExpectedMarkReadConversations.Count == 1);

        Assert.Equal(conversation, Assert.Single(session.ExpectedMarkReadConversations));
    }

    [Fact]
    public async Task RealtimeMessage_WhenCurrentConversationWasAtBottom_MarksWithoutWaitingForScrollAcknowledgement()
    {
        var conversation = new DirectMessage([8]);
        var initial = new ChatMessage(10, conversation, 8, "read", DateTimeOffset.UnixEpoch, isRead: true);
        var markGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new FakeSession
        {
            Account = AccountId.Create(RealmEndpoint.Parse("https://zulip.example"), 1),
            CurrentUserId = 1,
            Selected = conversation,
            Recent = [conversation],
            HistoryState = new ConversationHistoryState(conversation, 1, false, true, false, 10, null),
            StateValue = new ClientState(
                messages: new Dictionary<long, ChatMessage> { [10] = initial },
                users: new Dictionary<long, UserProfile> { [8] = new UserProfile(8, "Bea") },
                connection: new ConnectionState(ConnectionStatus.Connected)),
            MarkDisplayedReadAction = async (_, cancellationToken) => await markGate.Task.WaitAsync(cancellationToken)
        };
        using var viewModel = CreateViewModel(session);
        viewModel.AcknowledgeMessageScrollRequest(Assert.IsType<MessageScrollRequest>(viewModel.PendingMessageScrollRequest));
        viewModel.SetWindowActive(true);

        var unread = new ChatMessage(11, conversation, 8, "new", DateTimeOffset.UnixEpoch.AddSeconds(1));
        session.StateValue = session.StateValue with
        {
            Messages = new Dictionary<long, ChatMessage> { [10] = initial, [11] = unread },
            Unread = new UnreadState(new Dictionary<string, int> { [conversation.CanonicalKey] = 1 }, 1)
        };
        session.Publish();

        var followRequest = Assert.IsType<MessageScrollRequest>(viewModel.PendingMessageScrollRequest);
        Assert.Equal(MessageScrollReason.RealtimeFollow, followRequest.Reason);
        await WaitUntilAsync(() => session.ExpectedMarkReadConversations.Count == 1);

        Assert.Equal(conversation, Assert.Single(session.ExpectedMarkReadConversations));
        Assert.True(viewModel.Messages.Single(message => message.MessageId == 11).IsUnread);
        Assert.False(viewModel.Messages.Single(message => message.MessageId == 11).ShowUnreadDivider);
        Assert.True(Assert.Single(viewModel.DirectMessages).HasUnread);
        Assert.Equal(0, viewModel.NewMessageCount);
        Assert.False(viewModel.ShowNewMessagesButton);

        viewModel.AcknowledgeMessageScrollRequest(followRequest);
        Assert.Single(session.ExpectedMarkReadConversations);
        Assert.True(viewModel.Messages.Single(message => message.MessageId == 11).IsUnread);
        Assert.True(Assert.Single(viewModel.DirectMessages).HasUnread);

        // The same state can arrive again through a local-send/event-loop
        // hand-off. It must not schedule another latest-scroll cycle.
        session.Publish();
        Assert.Null(viewModel.PendingMessageScrollRequest);
        Assert.False(viewModel.Messages.Single(message => message.MessageId == 11).ShowUnreadDivider);

        markGate.SetResult();
        session.StateValue = session.StateValue with
        {
            Messages = new Dictionary<long, ChatMessage> { [10] = initial, [11] = unread with { IsRead = true } },
            Unread = new UnreadState()
        };
        session.Publish();
        await WaitUntilAsync(() => !viewModel.Messages.Single(message => message.MessageId == 11).IsUnread);

        Assert.False(Assert.Single(viewModel.DirectMessages).HasUnread);
        Assert.False(viewModel.HasNavigationUnread);
    }

    [Fact]
    public void OwnMessage_WhenCurrentConversationWasAtBottom_DoesNotQueueRealtimeFollow()
    {
        var conversation = new DirectMessage([8]);
        var initial = new ChatMessage(10, conversation, 8, "read", DateTimeOffset.UnixEpoch, isRead: true);
        var own = new ChatMessage(11, conversation, 1, "sent", DateTimeOffset.UnixEpoch.AddSeconds(1), isRead: true);
        var session = new FakeSession
        {
            CurrentUserId = 1,
            Selected = conversation,
            Recent = [conversation],
            HistoryState = new ConversationHistoryState(conversation, 1, false, true, false, 10, null),
            StateValue = new ClientState(
                messages: new Dictionary<long, ChatMessage> { [10] = initial },
                connection: new ConnectionState(ConnectionStatus.Connected))
        };
        using var viewModel = CreateViewModel(session);
        viewModel.AcknowledgeMessageScrollRequest(Assert.IsType<MessageScrollRequest>(viewModel.PendingMessageScrollRequest));

        session.StateValue = session.StateValue with
        {
            Messages = new Dictionary<long, ChatMessage> { [10] = initial, [11] = own }
        };
        session.Publish();

        Assert.Null(viewModel.PendingMessageScrollRequest);
        Assert.Equal(0, viewModel.NewMessageCount);
    }

    [Fact]
    public async Task SendCommand_WhenSameConversationConfirms_ScrollsToLatestWithoutResettingMessages()
    {
        var conversation = new DirectMessage([8]);
        var existing = new ChatMessage(10, conversation, 8, "already here", DateTimeOffset.UnixEpoch, isRead: true);
        var sent = new ChatMessage(11, conversation, 1, "sent", DateTimeOffset.UnixEpoch.AddSeconds(1), isRead: true);
        var session = new FakeSession
        {
            CurrentUserId = 1,
            Selected = conversation,
            Recent = [conversation],
            HistoryState = new ConversationHistoryState(conversation, 1, true, true, false, 10, null),
            StateValue = new ClientState(
                messages: new Dictionary<long, ChatMessage> { [10] = existing },
                connection: new ConnectionState(ConnectionStatus.Connected))
        };
        session.SendAction = (_, _) =>
        {
            session.StateValue = session.StateValue with
            {
                Messages = new Dictionary<long, ChatMessage> { [10] = existing, [11] = sent }
            };
            session.Publish();
            return Task.CompletedTask;
        };
        using var viewModel = CreateViewModel(session);
        var firstRow = Assert.Single(viewModel.Messages);
        var changes = new List<NotifyCollectionChangedAction>();
        viewModel.Messages.CollectionChanged += (_, eventArgs) => changes.Add(eventArgs.Action);
        Assert.IsType<MessageScrollRequest>(viewModel.PendingMessageScrollRequest);
        viewModel.ComposerText = "sent";

        await ((IAsyncRelayCommand)viewModel.SendCommand).ExecuteAsync(null);

        var followRequest = Assert.IsType<MessageScrollRequest>(viewModel.PendingMessageScrollRequest);
        Assert.Equal(MessageScrollReason.RealtimeFollow, followRequest.Reason);
        Assert.Equal(11, followRequest.TargetMessageId);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, changes);
        Assert.Same(firstRow, viewModel.Messages[0]);
        Assert.Collection(
            viewModel.Messages,
            message => Assert.Equal(10, message.MessageId),
            message => Assert.Equal(11, message.MessageId));
    }

    [Fact]
    public async Task ActivateDirectMessage_WhenNoMemoryWindow_DefersPartialPagesUntilLatestHistoryCompletes()
    {
        var first = new DirectMessage([8]);
        var second = new DirectMessage([9]);
        var firstMessage = new ChatMessage(10, first, 8, "first", DateTimeOffset.UnixEpoch, isRead: true);
        var cachedSecondMessage = new ChatMessage(20, second, 9, "cached", DateTimeOffset.UnixEpoch.AddMinutes(1), isRead: true);
        var latestSecondMessage = new ChatMessage(21, second, 9, "latest", DateTimeOffset.UnixEpoch.AddMinutes(2), isRead: true);
        var selectionCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new FakeSession
        {
            Selected = first,
            Recent = [first, second],
            HistoryState = new ConversationHistoryState(first, 1, false, true, false, 10, null),
            StateValue = new ClientState(
                messages: new Dictionary<long, ChatMessage> { [10] = firstMessage },
                users: new Dictionary<long, UserProfile>
                {
                    [8] = new UserProfile(8, "Bea"),
                    [9] = new UserProfile(9, "Chen")
                },
                connection: new ConnectionState(ConnectionStatus.Connected))
        };
        session.SelectAction = (conversation, _) =>
        {
            session.Selected = conversation;
            session.HistoryState = new ConversationHistoryState(conversation, 2, true, false, false, null, null);
            session.StateValue = session.StateValue with { Messages = new Dictionary<long, ChatMessage>() };
            session.Publish();
            return selectionCompleted.Task;
        };
        using var viewModel = CreateViewModel(session);
        var secondNavigation = Assert.Single(viewModel.DirectMessages, item => item.Conversation == second);

        viewModel.ActivateDirectMessage(secondNavigation);
        await WaitUntilAsync(() => viewModel.IsNavigationPending && session.Selected == second);
        session.StateValue = session.StateValue with
        {
            Messages = new Dictionary<long, ChatMessage> { [20] = cachedSecondMessage }
        };
        session.Publish();

        Assert.Empty(viewModel.Messages);

        session.HistoryState = new ConversationHistoryState(second, 2, false, true, false, 20, null);
        session.StateValue = session.StateValue with
        {
            Messages = new Dictionary<long, ChatMessage>
            {
                [20] = cachedSecondMessage,
                [21] = latestSecondMessage
            }
        };
        session.Publish();
        selectionCompleted.SetResult();
        await WaitUntilAsync(() => !viewModel.IsNavigationPending);

        Assert.Collection(
            viewModel.Messages,
            message => Assert.Equal(20, message.MessageId),
            message => Assert.Equal(21, message.MessageId));
        var scrollRequest = Assert.IsType<MessageScrollRequest>(viewModel.PendingMessageScrollRequest);
        Assert.Equal(MessageScrollReason.ConversationActivated, scrollRequest.Reason);
        Assert.Equal(21, scrollRequest.TargetMessageId);
    }

    [Fact]
    public async Task ActivateDirectMessage_WhenMemoryWindowIsAvailable_ProjectsImmediatelyWithoutResettingOnRefresh()
    {
        var first = new DirectMessage([8]);
        var second = new DirectMessage([9]);
        var firstMessage = new ChatMessage(10, first, 8, "first", DateTimeOffset.UnixEpoch, isRead: true);
        var cachedSecondMessage = new ChatMessage(20, second, 9, "cached", DateTimeOffset.UnixEpoch.AddMinutes(1), isRead: true);
        var selectionCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new FakeSession
        {
            Selected = first,
            Recent = [first, second],
            HistoryState = new ConversationHistoryState(first, 1, false, true, false, 10, null),
            StateValue = new ClientState(
                messages: new Dictionary<long, ChatMessage> { [10] = firstMessage },
                users: new Dictionary<long, UserProfile>
                {
                    [8] = new UserProfile(8, "Bea"),
                    [9] = new UserProfile(9, "Chen")
                },
                connection: new ConnectionState(ConnectionStatus.Connected))
        };
        session.SelectAction = (conversation, _) =>
        {
            session.Selected = conversation;
            session.HistoryState = new ConversationHistoryState(conversation, 2, true, false, false, 20, null);
            session.StateValue = session.StateValue with
            {
                Messages = new Dictionary<long, ChatMessage> { [20] = cachedSecondMessage }
            };
            session.Publish();
            return selectionCompleted.Task;
        };
        using var viewModel = CreateViewModel(session);
        var secondNavigation = Assert.Single(viewModel.DirectMessages, item => item.Conversation == second);
        var changes = new List<NotifyCollectionChangedAction>();
        viewModel.Messages.CollectionChanged += (_, eventArgs) => changes.Add(eventArgs.Action);

        viewModel.ActivateDirectMessage(secondNavigation);
        await WaitUntilAsync(() => viewModel.IsNavigationPending && viewModel.Messages.Count == 1);

        Assert.True(viewModel.IsNavigationPending);
        var cachedRow = Assert.Single(viewModel.Messages);
        Assert.Equal(20, cachedRow.MessageId);
        Assert.False(viewModel.ShowConversationLoadingIndicator);

        session.HistoryState = new ConversationHistoryState(second, 2, false, true, false, 20, null);
        session.Publish();
        selectionCompleted.SetResult();
        await WaitUntilAsync(() => !viewModel.IsNavigationPending);

        Assert.Same(cachedRow, Assert.Single(viewModel.Messages));
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, changes);
        var scrollRequest = Assert.IsType<MessageScrollRequest>(viewModel.PendingMessageScrollRequest);
        Assert.Equal(MessageScrollReason.ConversationActivated, scrollRequest.Reason);
        Assert.Equal(20, scrollRequest.TargetMessageId);
    }

    [Fact]
    public async Task ActivateConversation_WhenPrivateGroupMemoryWindowIsAvailable_ProjectsImmediatelyWithoutResetting()
    {
        var direct = new DirectMessage([8]);
        var group = new ChannelTopic(4, string.Empty);
        var directMessage = new ChatMessage(10, direct, 8, "direct", DateTimeOffset.UnixEpoch, isRead: true);
        var cachedGroupMessage = new ChatMessage(20, group, 9, "group cached", DateTimeOffset.UnixEpoch.AddMinutes(1), isRead: true);
        var selectionCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new FakeSession
        {
            Selected = direct,
            Recent = [direct],
            HistoryState = new ConversationHistoryState(direct, 1, false, true, false, 10, null),
            StateValue = new ClientState(
                messages: new Dictionary<long, ChatMessage> { [10] = directMessage },
                subscriptions: new Dictionary<long, Subscription> { [4] = PrivateGroupSubscription() },
                users: new Dictionary<long, UserProfile>
                {
                    [8] = new UserProfile(8, "Bea"),
                    [9] = new UserProfile(9, "Chen")
                },
                connection: new ConnectionState(ConnectionStatus.Connected))
        };
        session.SelectAction = (conversation, _) =>
        {
            session.Selected = conversation;
            session.HistoryState = new ConversationHistoryState(conversation, 2, true, false, false, 20, null);
            session.StateValue = session.StateValue with
            {
                Messages = new Dictionary<long, ChatMessage> { [20] = cachedGroupMessage }
            };
            session.Publish();
            return selectionCompleted.Task;
        };
        using var viewModel = CreateViewModel(session);
        var groupConversation = Assert.Single(viewModel.Conversations, item => item.Conversation == group);
        var changes = new List<NotifyCollectionChangedAction>();
        viewModel.Messages.CollectionChanged += (_, eventArgs) => changes.Add(eventArgs.Action);

        viewModel.ActivateConversation(groupConversation);
        await WaitUntilAsync(() => viewModel.IsNavigationPending && viewModel.Messages.Count == 1);

        var cachedRow = Assert.Single(viewModel.Messages);
        Assert.Equal(20, cachedRow.MessageId);
        Assert.False(viewModel.ShowConversationLoadingIndicator);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, changes);

        session.HistoryState = new ConversationHistoryState(group, 2, false, true, false, 20, null);
        session.Publish();
        selectionCompleted.SetResult();
        await WaitUntilAsync(() => !viewModel.IsNavigationPending);

        Assert.Same(cachedRow, Assert.Single(viewModel.Messages));
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, changes);
    }

    [Fact]
    public async Task ActivateDirectMessage_WhenReenteringSameCachedConversation_RetainsPresentationWithoutRedundantScroll()
    {
        var first = new DirectMessage([8]);
        var second = new DirectMessage([9]);
        var firstMessage = new ChatMessage(10, first, 8, "first", DateTimeOffset.UnixEpoch, isRead: true);
        var secondMessage = new ChatMessage(20, second, 9, "latest", DateTimeOffset.UnixEpoch.AddMinutes(1), isRead: true);
        var session = new FakeSession
        {
            Selected = first,
            Recent = [first, second],
            HistoryState = new ConversationHistoryState(first, 7, false, true, false, 10, null),
            StateValue = new ClientState(
                messages: new Dictionary<long, ChatMessage>
                {
                    [10] = firstMessage,
                    [20] = secondMessage
                },
                users: new Dictionary<long, UserProfile>
                {
                    [8] = new UserProfile(8, "Bea"),
                    [9] = new UserProfile(9, "Chen")
                },
                connection: new ConnectionState(ConnectionStatus.Connected))
        };
        session.SelectAction = (conversation, _) =>
        {
            session.Selected = conversation;
            session.HistoryState = new ConversationHistoryState(
                conversation,
                7,
                false,
                true,
                false,
                conversation == first ? 10 : 20,
                null);
            session.Publish();
            return Task.CompletedTask;
        };
        using var viewModel = CreateViewModel(session);
        var firstNavigation = Assert.Single(viewModel.DirectMessages, item => item.Conversation == first);
        var secondNavigation = Assert.Single(viewModel.DirectMessages, item => item.Conversation == second);

        viewModel.ActivateDirectMessage(secondNavigation);
        await WaitUntilAsync(() => !viewModel.IsNavigationPending && viewModel.CurrentConversationKey == second.CanonicalKey);
        var firstSecondRequest = Assert.IsType<MessageScrollRequest>(viewModel.PendingMessageScrollRequest);
        Assert.Equal(20, firstSecondRequest.TargetMessageId);
        var secondPresentation = Assert.Single(
            viewModel.MessagePresentations,
            item => item.ConversationKey == second.CanonicalKey);
        var secondMessages = secondPresentation.Messages;
        var secondRow = Assert.Single(secondMessages);
        viewModel.AcknowledgeMessageScrollRequest(firstSecondRequest);

        viewModel.ActivateDirectMessage(firstNavigation);
        await WaitUntilAsync(() => !viewModel.IsNavigationPending && viewModel.CurrentConversationKey == first.CanonicalKey);
        Assert.Null(viewModel.PendingMessageScrollRequest);

        viewModel.ActivateDirectMessage(secondNavigation);
        await WaitUntilAsync(() => !viewModel.IsNavigationPending && viewModel.CurrentConversationKey == second.CanonicalKey);

        Assert.Null(viewModel.PendingMessageScrollRequest);
        Assert.Same(
            secondPresentation,
            Assert.Single(viewModel.MessagePresentations, item => item.ConversationKey == second.CanonicalKey));
        Assert.Same(secondMessages, secondPresentation.Messages);
        Assert.Same(secondRow, Assert.Single(secondPresentation.Messages));
        Assert.True(secondPresentation.IsActive);
    }

    [Fact]
    public async Task ActivateConversation_WhenReenteringCachedPrivateGroup_DoesNotNudgeRetainedViewport()
    {
        var direct = new DirectMessage([8]);
        var group = new ChannelTopic(4, string.Empty);
        var directMessage = new ChatMessage(10, direct, 8, "direct", DateTimeOffset.UnixEpoch, isRead: true);
        var groupMessage = new ChatMessage(20, group, 9, "group", DateTimeOffset.UnixEpoch.AddMinutes(1), isRead: true);
        var session = new FakeSession
        {
            Selected = direct,
            Recent = [direct],
            HistoryState = new ConversationHistoryState(direct, 1, false, true, false, 10, null),
            StateValue = new ClientState(
                messages: new Dictionary<long, ChatMessage>
                {
                    [10] = directMessage,
                    [20] = groupMessage
                },
                subscriptions: new Dictionary<long, Subscription> { [4] = PrivateGroupSubscription() },
                users: new Dictionary<long, UserProfile>
                {
                    [8] = new UserProfile(8, "Bea"),
                    [9] = new UserProfile(9, "Chen")
                },
                connection: new ConnectionState(ConnectionStatus.Connected))
        };
        using var viewModel = CreateViewModel(session);
        var directNavigation = Assert.Single(viewModel.DirectMessages, item => item.Conversation == direct);
        var groupNavigation = Assert.Single(viewModel.Conversations, item => item.Conversation == group);

        viewModel.ActivateConversation(groupNavigation);
        await WaitUntilAsync(() => !viewModel.IsNavigationPending && viewModel.CurrentConversationKey == group.CanonicalKey);
        viewModel.AcknowledgeMessageScrollRequest(Assert.IsType<MessageScrollRequest>(viewModel.PendingMessageScrollRequest));
        var groupPresentation = Assert.Single(
            viewModel.MessagePresentations,
            item => item.ConversationKey == group.CanonicalKey);
        var groupRow = Assert.Single(groupPresentation.Messages);

        viewModel.ActivateDirectMessage(directNavigation);
        await WaitUntilAsync(() => !viewModel.IsNavigationPending && viewModel.CurrentConversationKey == direct.CanonicalKey);
        Assert.Null(viewModel.PendingMessageScrollRequest);

        viewModel.ActivateConversation(groupNavigation);
        await WaitUntilAsync(() => !viewModel.IsNavigationPending && viewModel.CurrentConversationKey == group.CanonicalKey);

        Assert.Null(viewModel.PendingMessageScrollRequest);
        Assert.Same(groupPresentation, Assert.Single(
            viewModel.MessagePresentations,
            item => item.ConversationKey == group.CanonicalKey));
        Assert.Same(groupRow, Assert.Single(groupPresentation.Messages));

        viewModel.ActivateDirectMessage(directNavigation);
        await WaitUntilAsync(() => !viewModel.IsNavigationPending && viewModel.CurrentConversationKey == direct.CanonicalKey);
        Assert.Null(viewModel.PendingMessageScrollRequest);
        var newerGroupMessage = new ChatMessage(
            21,
            group,
            9,
            "new group message",
            DateTimeOffset.UnixEpoch.AddMinutes(2),
            isRead: false);
        session.StateValue = session.StateValue with
        {
            Messages = new Dictionary<long, ChatMessage>
            {
                [10] = directMessage,
                [20] = groupMessage,
                [21] = newerGroupMessage
            }
        };

        viewModel.ActivateConversation(groupNavigation);
        await WaitUntilAsync(() => !viewModel.IsNavigationPending && viewModel.CurrentConversationKey == group.CanonicalKey);

        var newMessageRequest = Assert.IsType<MessageScrollRequest>(viewModel.PendingMessageScrollRequest);
        Assert.Equal(21, newMessageRequest.TargetMessageId);
        Assert.Equal(MessageScrollReason.ConversationActivated, newMessageRequest.Reason);
    }

    [Fact]
    public async Task ActivateDirectMessage_WhenAuthoritySelectionIsDelayed_KeepsCachedTargetPresentationVisible()
    {
        var first = new DirectMessage([8]);
        var second = new DirectMessage([9]);
        var firstMessage = new ChatMessage(10, first, 8, "first", DateTimeOffset.UnixEpoch, isRead: true);
        var secondMessage = new ChatMessage(20, second, 9, "second", DateTimeOffset.UnixEpoch.AddMinutes(1), isRead: true);
        var session = new FakeSession
        {
            Selected = first,
            Recent = [first, second],
            HistoryState = new ConversationHistoryState(first, 1, false, true, false, 10, null),
            StateValue = new ClientState(
                messages: new Dictionary<long, ChatMessage>
                {
                    [10] = firstMessage,
                    [20] = secondMessage
                },
                users: new Dictionary<long, UserProfile>
                {
                    [8] = new UserProfile(8, "Bea"),
                    [9] = new UserProfile(9, "Chen")
                },
                connection: new ConnectionState(ConnectionStatus.Connected))
        };
        using var viewModel = CreateViewModel(session);
        var firstNavigation = Assert.Single(viewModel.DirectMessages, item => item.Conversation == first);
        var secondNavigation = Assert.Single(viewModel.DirectMessages, item => item.Conversation == second);

        viewModel.ActivateDirectMessage(secondNavigation);
        await WaitUntilAsync(() => !viewModel.IsNavigationPending && viewModel.CurrentConversationKey == second.CanonicalKey);
        var secondPresentation = Assert.Single(
            viewModel.MessagePresentations,
            item => item.ConversationKey == second.CanonicalKey);
        var cachedMessages = secondPresentation.Messages;
        var cachedRow = Assert.Single(cachedMessages);

        viewModel.ActivateDirectMessage(firstNavigation);
        await WaitUntilAsync(() => !viewModel.IsNavigationPending && viewModel.CurrentConversationKey == first.CanonicalKey);

        var delayedSelection = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.SelectAction = (_, _) => delayedSelection.Task;
        viewModel.ActivateDirectMessage(secondNavigation);
        await WaitUntilAsync(() => viewModel.IsNavigationPending && secondPresentation.IsActive);

        Assert.True(viewModel.IsConversationContentVisible);
        Assert.Same(cachedMessages, viewModel.Messages);
        Assert.Same(cachedRow, Assert.Single(viewModel.Messages));
        Assert.False(Assert.Single(
            viewModel.MessagePresentations,
            item => item.ConversationKey == first.CanonicalKey).IsActive);

        session.Selected = second;
        session.HistoryState = new ConversationHistoryState(second, 2, false, true, false, 20, null);
        session.Publish();
        delayedSelection.SetResult();
        await WaitUntilAsync(() => !viewModel.IsNavigationPending && viewModel.CurrentConversationKey == second.CanonicalKey);

        Assert.Same(cachedMessages, viewModel.Messages);
        Assert.Same(cachedRow, Assert.Single(viewModel.Messages));
    }

    [Fact]
    public async Task RealtimeMessage_WhenViewportIsAwayFromBottom_WaitsForManualJumpAcknowledgement()
    {
        var conversation = new DirectMessage([8]);
        var initial = new ChatMessage(10, conversation, 8, "read", DateTimeOffset.UnixEpoch, isRead: true);
        var session = new FakeSession
        {
            Account = AccountId.Create(RealmEndpoint.Parse("https://zulip.example"), 1),
            CurrentUserId = 1,
            Selected = conversation,
            Recent = [conversation],
            HistoryState = new ConversationHistoryState(conversation, 1, false, true, false, 10, null),
            StateValue = new ClientState(
                messages: new Dictionary<long, ChatMessage> { [10] = initial },
                users: new Dictionary<long, UserProfile> { [8] = new UserProfile(8, "Bea") },
                connection: new ConnectionState(ConnectionStatus.Connected))
        };
        using var viewModel = CreateViewModel(session);
        viewModel.AcknowledgeMessageScrollRequest(Assert.IsType<MessageScrollRequest>(viewModel.PendingMessageScrollRequest));
        viewModel.SetWindowActive(true);
        viewModel.ReportMessageBottomDistance(200d);

        session.StateValue = session.StateValue with
        {
            Messages = new Dictionary<long, ChatMessage>
            {
                [10] = initial,
                [11] = new ChatMessage(11, conversation, 8, "new", DateTimeOffset.UnixEpoch.AddSeconds(1))
            },
            Unread = new UnreadState(new Dictionary<string, int> { [conversation.CanonicalKey] = 1 }, 1)
        };
        session.Publish();

        Assert.Equal(1, viewModel.NewMessageCount);
        Assert.Empty(session.ExpectedMarkReadConversations);

        viewModel.ScrollToLatestCommand.Execute(null);
        var jumpRequest = Assert.IsType<MessageScrollRequest>(viewModel.PendingMessageScrollRequest);
        Assert.Equal(MessageScrollReason.ManualJumpToLatest, jumpRequest.Reason);
        viewModel.AcknowledgeMessageScrollRequest(jumpRequest);
        await WaitUntilAsync(() => session.ExpectedMarkReadConversations.Count == 1);

        Assert.Equal(conversation, Assert.Single(session.ExpectedMarkReadConversations));
    }

    [Fact]
    public async Task ChannelUnsubscribe_WhenConfirmed_UsesSelectedChannelAndClosesDetails()
    {
        var channel = new ChannelTopic(4, string.Empty);
        var state = new ClientState(
            subscriptions: new Dictionary<long, Subscription> { [4] = PrivateGroupSubscription() },
            users: new Dictionary<long, UserProfile>
            {
                [7] = new UserProfile(7, "Ada"),
                [8] = new UserProfile(8, "Bea")
            },
            connection: new ConnectionState(RelayCove.Core.ConnectionStatus.Connected));
        var requested = new List<long>();
        var session = new FakeSession
        {
            Account = AccountId.Create(RealmEndpoint.Parse("https://zulip.example"), 7),
            CurrentUserId = 7,
            StateValue = state,
            Selected = channel,
            LoadChannelDetailsAction = (_, _) => Task.FromResult(PrivateGroupDetails(4, "engineering", string.Empty, 8)),
            ChannelMemberIdsAction = (_, _) => Task.FromResult<IReadOnlyList<long>>([7, 8]),
            RealmUsersAction = _ => Task.FromResult<IReadOnlyList<UserProfile>>([new UserProfile(7, "Ada"), new UserProfile(8, "Bea")])
        };
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
        await ((IAsyncRelayCommand)viewModel.ToggleDetailsCommand).ExecuteAsync(null);

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
    public async Task ActivateChannel_WhenSingleTopicExists_ExpandsAndOpensThatTopic()
    {
        var session = new FakeSession
        {
            StateValue = new ClientState(
                subscriptions: new Dictionary<long, Subscription> { [5] = new Subscription(5, "product-design") },
                connection: new ConnectionState(ConnectionStatus.Connected)),
            LoadTopicsAction = (_, _) => Task.FromResult<IReadOnlyList<TopicSummary>>([new TopicSummary(5, "native-ui", 12)])
        };
        using var viewModel = CreateViewModel(session);
        var channel = Assert.Single(viewModel.Channels);

        viewModel.ActivateChannel(channel);
        await WaitUntilAsync(() => session.SelectedConversation is ChannelTopic { ChannelId: 5, Topic: "native-ui" });

        Assert.True(channel.IsExpanded);
        Assert.True(viewModel.ShowTopicPicker);
        Assert.Equal("native-ui", Assert.Single(viewModel.Topics).Topic);
        Assert.Equal(72d, channel.TreeRowHeight);
        Assert.Same(viewModel.Topics, channel.TreeTopics);
    }

    [Fact]
    public async Task ActivateChannel_WhenNoTopicsExist_KeepsChannelExpandedWithoutChangingConversation()
    {
        var selected = new DirectMessage([8]);
        var loaded = false;
        var session = new FakeSession
        {
            Selected = selected,
            StateValue = new ClientState(
                subscriptions: new Dictionary<long, Subscription> { [5] = new Subscription(5, "product-design") },
                connection: new ConnectionState(ConnectionStatus.Connected)),
            LoadTopicsAction = (_, _) =>
            {
                loaded = true;
                return Task.FromResult<IReadOnlyList<TopicSummary>>([]);
            }
        };
        using var viewModel = CreateViewModel(session);
        var channel = Assert.Single(viewModel.Channels);

        viewModel.ActivateChannel(channel);
        await WaitUntilAsync(() => loaded);

        Assert.True(channel.IsExpanded);
        Assert.Empty(viewModel.Topics);
        Assert.Equal(selected, session.SelectedConversation);
    }

    [Fact]
    public async Task ActivateTopic_WhenNamedTopicIsSelected_FailsClosedForStage25ConversationContent()
    {
        var session = new FakeSession
        {
            StateValue = new ClientState(
                subscriptions: new Dictionary<long, Subscription> { [5] = new Subscription(5, "product-design") },
                connection: new ConnectionState(ConnectionStatus.Connected)),
            LoadTopicsAction = (_, _) => Task.FromResult<IReadOnlyList<TopicSummary>>(
            [
                new TopicSummary(5, "design", 10),
                new TopicSummary(5, "implementation", 12)
            ])
        };
        using var viewModel = CreateViewModel(session);
        var channel = Assert.Single(viewModel.Channels);
        viewModel.ActivateChannel(channel);
        await WaitUntilAsync(() => viewModel.Topics.Count == 2);
        var topic = viewModel.Topics.Single(item => item.Topic == "design");

        viewModel.ActivateTopic(topic);
        await WaitUntilAsync(() => session.SelectedConversation is ChannelTopic { ChannelId: 5, Topic: "design" });

        Assert.Null(viewModel.SelectedTopic);
        Assert.False(viewModel.HasSelectedConversation);
        Assert.False(viewModel.CanCompose);
    }

    [Fact]
    public async Task ActivateChannel_WhenPreviousTopicLoadCompletesLate_DoesNotOverwriteNewChannel()
    {
        var firstLoad = new TaskCompletionSource<IReadOnlyList<TopicSummary>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new FakeSession
        {
            StateValue = new ClientState(
                subscriptions: new Dictionary<long, Subscription>
                {
                    [4] = new Subscription(4, "engineering"),
                    [5] = new Subscription(5, "product-design")
                },
                connection: new ConnectionState(ConnectionStatus.Connected)),
            LoadTopicsAction = (channelId, _) => channelId == 4
                ? firstLoad.Task
                : Task.FromResult<IReadOnlyList<TopicSummary>>([new TopicSummary(5, "native-ui", 12)])
        };
        using var viewModel = CreateViewModel(session);
        var first = viewModel.Channels.Single(channel => channel.ChannelId == 4);
        var second = viewModel.Channels.Single(channel => channel.ChannelId == 5);

        viewModel.ActivateChannel(first);
        viewModel.ActivateChannel(second);
        await WaitUntilAsync(() => session.SelectedConversation is ChannelTopic { ChannelId: 5, Topic: "native-ui" });
        firstLoad.SetResult([new TopicSummary(4, "late", 99)]);
        await Task.Delay(30);

        Assert.False(first.IsExpanded);
        Assert.True(second.IsExpanded);
        Assert.Equal(new ChannelTopic(5, "native-ui"), session.SelectedConversation);
        Assert.Equal(5, Assert.Single(viewModel.Topics).ChannelId);
    }

    [Fact]
    public async Task ActivateChannel_WhenExpandedAgain_CollapsesWithoutChangingCurrentConversation()
    {
        var session = new FakeSession
        {
            StateValue = new ClientState(
                subscriptions: new Dictionary<long, Subscription> { [5] = new Subscription(5, "product-design") },
                connection: new ConnectionState(ConnectionStatus.Connected)),
            LoadTopicsAction = (_, _) => Task.FromResult<IReadOnlyList<TopicSummary>>([new TopicSummary(5, "native-ui", 12)])
        };
        using var viewModel = CreateViewModel(session);
        var channel = Assert.Single(viewModel.Channels);
        viewModel.ActivateChannel(channel);
        await WaitUntilAsync(() => session.SelectedConversation is ChannelTopic { ChannelId: 5, Topic: "native-ui" });
        var selected = session.SelectedConversation;

        viewModel.ActivateChannel(channel);

        Assert.False(channel.IsExpanded);
        Assert.False(viewModel.ShowTopicPicker);
        Assert.Equal(38d, channel.TreeRowHeight);
        Assert.Equal(selected, session.SelectedConversation);
    }

    [Fact]
    public async Task ActivateDirectMessage_WhenChannelIsExpanded_CollapsesTopics()
    {
        var session = new FakeSession
        {
            StateValue = new ClientState(
                subscriptions: new Dictionary<long, Subscription> { [5] = new Subscription(5, "product-design") },
                users: new Dictionary<long, UserProfile> { [8] = new UserProfile(8, "Bea") },
                connection: new ConnectionState(ConnectionStatus.Connected)),
            LoadTopicsAction = (_, _) => Task.FromResult<IReadOnlyList<TopicSummary>>([new TopicSummary(5, "native-ui", 12)])
        };
        using var viewModel = CreateViewModel(session);
        var channel = Assert.Single(viewModel.Channels);
        viewModel.ActivateChannel(channel);
        await WaitUntilAsync(() => channel.IsExpanded);

        viewModel.ActivateDirectMessage(new NavigationItem(new DirectMessage([8]), "Bea"));
        await WaitUntilAsync(() => session.SelectedConversation is DirectMessage);

        Assert.All(viewModel.Channels, item => Assert.False(item.IsExpanded));
    }

    [Fact]
    public void ShowNewChannelConversation_WhenConnected_DoesNotGateOnRegisterPermissionProjection()
    {
        var session = new FakeSession
        {
            StateValue = new ClientState(
                users: new Dictionary<long, UserProfile> { [8] = new UserProfile(8, "Bea") },
                connection: new ConnectionState(ConnectionStatus.Connected))
        };
        using var viewModel = CreateViewModel(session);

        viewModel.OpenNewConversationCommand.Execute(null);
        viewModel.ShowNewChannelConversationCommand.Execute(null);

        Assert.True(viewModel.IsNewConversationOpen);
        Assert.True(viewModel.IsNewChannelConversationMode);
        Assert.True(viewModel.CanCreatePrivateGroup);
        Assert.False(viewModel.ShowPrivateGroupCreateDisabledReason);
        Assert.Null(viewModel.NewConversationError);
    }

    [Fact]
    public void ShowNewChannelConversation_WhenOffline_StaysDisabledWithConnectionReason()
    {
        var session = new FakeSession
        {
            StateValue = new ClientState(
                users: new Dictionary<long, UserProfile> { [8] = new UserProfile(8, "Bea") },
                connection: new ConnectionState(ConnectionStatus.Offline))
        };
        using var viewModel = CreateViewModel(session);

        viewModel.OpenNewConversationCommand.Execute(null);
        viewModel.ShowNewChannelConversationCommand.Execute(null);

        Assert.False(viewModel.IsNewChannelConversationMode);
        Assert.False(viewModel.CanCreatePrivateGroup);
        Assert.Contains("未连接", viewModel.NewConversationError);
    }

    [Fact]
    public async Task StartNewChannelConversation_WhenAuthorized_CreatesPrivateGroupAndOpensEmptyTopic()
    {
        PrivateGroupCreateOptions? requested = null;
        var session = new FakeSession
        {
            Account = AccountId.Create(RealmEndpoint.Parse("https://zulip.example"), 7),
            CurrentUserId = 7,
            CanCreatePrivateGroup = true,
            StateValue = new ClientState(
                users: new Dictionary<long, UserProfile>
                {
                    [7] = new UserProfile(7, "Ada"),
                    [8] = new UserProfile(8, "Bea"),
                    [9] = new UserProfile(9, "Chen")
                },
                connection: new ConnectionState(ConnectionStatus.Connected))
        };
        session.CreatePrivateGroupAction = (options, _) =>
        {
            requested = options;
            session.StateValue = session.StateValue with
            {
                Subscriptions = new Dictionary<long, Subscription>
                {
                    [55] = new Subscription(55, options.Name, isPrivate: true, topicsPolicy: ChannelTopicsPolicy.EmptyTopicOnly, isWebPublic: false)
                }
            };
            return Task.FromResult(new PrivateGroupCreated(55, options.Name, new ChannelTopic(55, string.Empty), 3));
        };
        using var viewModel = CreateViewModel(session);

        viewModel.OpenNewConversationCommand.Execute(null);
        viewModel.ShowNewChannelConversationCommand.Execute(null);
        foreach (var choice in viewModel.NewConversationChoices) choice.IsSelected = true;
        viewModel.NewPrivateGroupName = "产品设计群";
        await ((IAsyncRelayCommand)viewModel.StartNewChannelConversationCommand).ExecuteAsync(null);

        Assert.Equal("产品设计群", requested!.Name);
        Assert.Equal([8L, 9L], requested.OtherMemberIds);
        Assert.Equal(new ChannelTopic(55, string.Empty), session.SelectedConversation);
        Assert.Contains(viewModel.Conversations, item => item.Title == "产品设计群" && item.IsPrivateGroup);
        Assert.False(viewModel.IsNewConversationOpen);
    }

    [Fact]
    public async Task ChannelMenu_WhenOpenedForAnotherChannel_TargetsThatChannelForLabelsAndExit()
    {
        var requested = new List<long>();
        var session = new FakeSession
        {
            Selected = new ChannelTopic(4, "current"),
            StateValue = new ClientState(
                subscriptions: new Dictionary<long, Subscription>
                {
                    [4] = new Subscription(4, "engineering"),
                    [5] = new Subscription(5, "product-design", isMuted: true, isPinned: true)
                },
                connection: new ConnectionState(ConnectionStatus.Connected)),
            UnsubscribeChannelAction = (channelId, _) =>
            {
                requested.Add(channelId);
                return Task.CompletedTask;
            }
        };
        using var viewModel = CreateViewModel(session);
        var target = viewModel.Channels.Single(channel => channel.ChannelId == 5);

        viewModel.OpenChannelMenuAtCommand.Execute(new ChannelMenuRequest(target, 42d, 56d));

        Assert.True(viewModel.IsChannelMenuOpen);
        Assert.Same(target, viewModel.ActiveChannelAction);
        Assert.Equal("取消置顶", viewModel.ActiveChannelPinLabel);
        Assert.Equal("取消静音", viewModel.ActiveChannelMuteLabel);
        Assert.Equal(new ChannelTopic(4, "current"), session.SelectedConversation);

        viewModel.RequestActiveChannelUnsubscribeCommand.Execute(null);
        Assert.True(viewModel.IsChannelUnsubscribeConfirmationOpen);
        Assert.Equal("product-design", viewModel.ChannelUnsubscribeTargetName);
        await ((IAsyncRelayCommand)viewModel.ConfirmChannelUnsubscribeCommand).ExecuteAsync(null);

        Assert.Equal([5], requested);
    }

    [Fact]
    public async Task CopyActiveChannelLink_WhenMenuTargetsChannel_CopiesCanonicalRealmChannelLink()
    {
        var interactions = new FakePlatformInteractionService();
        var session = new FakeSession
        {
            ActiveRealm = RealmEndpoint.Parse("https://zulip.example"),
            StateValue = new ClientState(
                subscriptions: new Dictionary<long, Subscription> { [5] = new Subscription(5, "产品 设计") },
                connection: new ConnectionState(ConnectionStatus.Connected))
        };
        using var viewModel = CreateViewModel(session, platformInteractions: interactions);
        var channel = Assert.Single(viewModel.Channels);
        viewModel.OpenChannelMenuAtCommand.Execute(new ChannelMenuRequest(channel, 42d, 56d));

        await ((IAsyncRelayCommand)viewModel.CopyActiveChannelLinkCommand).ExecuteAsync(null);

        Assert.Equal("https://zulip.example/#narrow/channel/5-%E4%BA%A7%E5%93%81%20%E8%AE%BE%E8%AE%A1", Assert.Single(interactions.Copied));
        Assert.False(viewModel.IsChannelMenuOpen);
        Assert.Equal("已复制频道链接。", viewModel.UnavailableFeatureMessage);
    }

    [Fact]
    public async Task OpenActiveChannelTopicList_WhenMenuTargetsOtherChannel_ExpandsAndOpensItsTopic()
    {
        var session = new FakeSession
        {
            Selected = new ChannelTopic(4, "current"),
            StateValue = new ClientState(
                subscriptions: new Dictionary<long, Subscription>
                {
                    [4] = new Subscription(4, "engineering"),
                    [5] = new Subscription(5, "product-design")
                },
                connection: new ConnectionState(ConnectionStatus.Connected)),
            LoadTopicsAction = (channelId, _) => Task.FromResult<IReadOnlyList<TopicSummary>>(
                channelId == 5 ? [new TopicSummary(5, "native-ui", 12)] : [])
        };
        using var viewModel = CreateViewModel(session);
        var target = viewModel.Channels.Single(channel => channel.ChannelId == 5);
        viewModel.OpenChannelMenuAtCommand.Execute(new ChannelMenuRequest(target, 42d, 56d));

        viewModel.OpenActiveChannelTopicListCommand.Execute(null);
        await WaitUntilAsync(() => session.SelectedConversation is ChannelTopic { ChannelId: 5, Topic: "native-ui" });

        Assert.True(target.IsExpanded);
        Assert.False(viewModel.IsChannelMenuOpen);
    }

    [Fact]
    public void ExplainActiveChannelFeature_WhenProtocolIsNotConnected_ClosesMenuWithoutWriting()
    {
        var session = new FakeSession
        {
            StateValue = new ClientState(
                subscriptions: new Dictionary<long, Subscription> { [5] = new Subscription(5, "product-design") },
                connection: new ConnectionState(ConnectionStatus.Connected))
        };
        using var viewModel = CreateViewModel(session);
        var channel = Assert.Single(viewModel.Channels);
        viewModel.OpenChannelMenuAtCommand.Execute(new ChannelMenuRequest(channel, 42d, 56d));

        viewModel.ExplainActiveChannelFeatureCommand.Execute("频道颜色修改");

        Assert.False(viewModel.IsChannelMenuOpen);
        Assert.Equal("频道颜色修改尚未接通频道级协议；未执行任何 Realm 操作。", viewModel.UnavailableFeatureMessage);
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
    public async Task ActivateChannel_WhenTopicLoadPublishesOldConversation_KeepsBrowsedChannelAndSelectsOnlyTopic()
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

        viewModel.ActivateChannel(viewModel.Channels.Single(item => item.ChannelId == 5));
        await WaitUntilAsync(() => session.Selected is ChannelTopic { ChannelId: 5 });

        Assert.Null(viewModel.SelectedChannel);
        Assert.Equal("native-ui", Assert.Single(viewModel.Topics).Topic);
        Assert.Equal(new ChannelTopic(5, "native-ui"), session.SelectedConversation);
        Assert.False(viewModel.HasSelectedConversation);
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
                UiConversationWidthMode.Wide)
        };
        using var viewModel = CreateViewModel(
            new FakeSession(),
            uiPreferencesService: preferences);

        Assert.True(viewModel.IsCompactDensity);
        Assert.True(viewModel.IsLargeFont);
        Assert.True(viewModel.IsWideConversationWidth);
        Assert.Equal(352d, viewModel.ConversationPaneWidth.Value);

        viewModel.SetNarrowConversationWidthCommand.Execute(null);
        Assert.Equal(UiConversationWidthMode.Narrow, preferences.Current.ConversationWidth);
        Assert.Equal(264d, viewModel.ConversationPaneWidth.Value);

        viewModel.ResetUiPreferencesCommand.Execute(null);
        Assert.True(viewModel.IsComfortableDensity);
        Assert.True(viewModel.IsDefaultFont);
        Assert.True(viewModel.IsStandardConversationWidth);
        Assert.Equal(310d, viewModel.ConversationPaneWidth.Value);
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
    public void ComposerHeight_WhenDraggedBelowVisibleEditorFloor_ClampsToOneHundredTwentyEightDip()
    {
        using var viewModel = CreateViewModel(new FakeSession());

        viewModel.ComposerHeight = 0d;

        Assert.Equal(128d, viewModel.ComposerHeight);
    }

    [Fact]
    public void ComposerHeight_WhenChanged_PersistsAcrossViewModels()
    {
        var preferences = new FakeUiPreferencesService
        {
            Current = new UiPreferences(ComposerHeight: 224d)
        };
        using (var first = CreateViewModel(new FakeSession(), uiPreferencesService: preferences))
        {
            Assert.Equal(224d, first.ComposerHeight);

            first.ComposerHeight = 272d;

            Assert.Equal(272d, preferences.Current.ComposerHeight);

            first.SetCompactDensityCommand.Execute(null);
            Assert.Equal(272d, preferences.Current.ComposerHeight);
        }

        using var restored = CreateViewModel(new FakeSession(), uiPreferencesService: preferences);
        Assert.Equal(272d, restored.ComposerHeight);
    }

    [Fact]
    public void MessageRowMaximumWidth_WhenViewportChanges_MatchesWebResponsiveCaps()
    {
        using var viewModel = CreateViewModel(new FakeSession());

        viewModel.UpdateViewport(1440);
        Assert.Equal(690d, viewModel.MessageRowMaximumWidth, 3);

        viewModel.UpdateViewport(1024);
        Assert.Equal(512.24d, viewModel.MessageRowMaximumWidth, 2);

        viewModel.UpdateViewport(640);
        Assert.Equal(540d, viewModel.MessageRowMaximumWidth, 3);
    }

    [Fact]
    public void MessageRowMaximumWidth_WhenInlineDetailsOpens_UsesRemainingChatWidth()
    {
        using var viewModel = CreateViewModel(new FakeSession());
        viewModel.UpdateViewport(1440);

        viewModel.IsDetailsOpen = true;

        Assert.Equal(612.56d, viewModel.MessageRowMaximumWidth, 2);
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
    public async Task OwnPresence_WhenBusyAndOfflineAreSelected_UsesSessionAndUpdatesMenuState()
    {
        var state = new ClientState(
            users: new Dictionary<long, UserProfile> { [7] = new(7, "Ada") },
            connection: new ConnectionState(ConnectionStatus.Connected),
            presence: new PresenceState(true, new Dictionary<long, UserPresence>
            {
                [7] = new UserPresence(7, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
            }));
        var session = new FakeSession
        {
            StateValue = state,
            Account = AccountId.Create(RealmEndpoint.Parse("https://chat.example.test"), 7),
            CurrentUserId = 7,
            CanSetOwnPresenceValue = true,
            OwnPresenceStatusValue = UserPresenceStatus.Active
        };
        session.SetOwnPresenceAction = (status, _) =>
        {
            session.OwnPresenceStatusValue = status;
            return Task.CompletedTask;
        };
        using var viewModel = CreateViewModel(session);

        await viewModel.SetOwnPresenceIdleCommand.ExecuteAsync(null);
        Assert.Equal(UserPresenceStatus.Idle, session.OwnPresenceStatusValue);
        Assert.Equal("忙碌", viewModel.OwnPresenceLabel);
        Assert.True(viewModel.IsOwnPresenceIdle);
        Assert.True(viewModel.HasOwnPresenceStatus);

        await viewModel.SetOwnPresenceOfflineCommand.ExecuteAsync(null);
        Assert.Equal(UserPresenceStatus.Offline, session.OwnPresenceStatusValue);
        Assert.Equal("离线", viewModel.OwnPresenceLabel);
        Assert.True(viewModel.IsOwnPresenceOffline);
        Assert.True(viewModel.HasOwnPresenceStatus);
        Assert.Null(viewModel.OwnPresenceError);
    }

    [Fact]
    public void OwnPresence_WhenStatusIsKnownButSettingIsUnavailable_StillShowsAvatarStatus()
    {
        var session = new FakeSession
        {
            StateValue = new ClientState(connection: new ConnectionState(ConnectionStatus.Connected)),
            Account = AccountId.Create(RealmEndpoint.Parse("https://chat.example.test"), 7),
            CurrentUserId = 7,
            CanSetOwnPresenceValue = false,
            OwnPresenceStatusValue = UserPresenceStatus.Active
        };
        using var viewModel = CreateViewModel(session);

        Assert.True(viewModel.HasOwnPresenceStatus);
        Assert.False(viewModel.ShowOwnPresenceControls);
        Assert.True(viewModel.IsOwnPresenceOnline);
    }

    [Fact]
    public async Task OwnPresence_WhenCurrentStatusIsSelected_DoesNotRepeatWrite()
    {
        var writes = 0;
        var session = new FakeSession
        {
            StateValue = new ClientState(connection: new ConnectionState(ConnectionStatus.Connected)),
            Account = AccountId.Create(RealmEndpoint.Parse("https://chat.example.test"), 7),
            CurrentUserId = 7,
            CanSetOwnPresenceValue = true,
            OwnPresenceStatusValue = UserPresenceStatus.Active,
            SetOwnPresenceAction = (_, _) =>
            {
                writes++;
                return Task.CompletedTask;
            }
        };
        using var viewModel = CreateViewModel(session);

        await viewModel.SetOwnPresenceOnlineCommand.ExecuteAsync(null);

        Assert.Equal(0, writes);
        Assert.False(viewModel.CanSetOwnPresenceOnline);
        Assert.True(viewModel.CanSetOwnPresenceIdle);
    }

    [Fact]
    public async Task OwnPresence_WhileServerConfirmationIsPending_ShowsImmediateProgress()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new FakeSession
        {
            StateValue = new ClientState(connection: new ConnectionState(ConnectionStatus.Connected)),
            Account = AccountId.Create(RealmEndpoint.Parse("https://chat.example.test"), 7),
            CurrentUserId = 7,
            CanSetOwnPresenceValue = true,
            OwnPresenceStatusValue = UserPresenceStatus.Active
        };
        session.SetOwnPresenceAction = async (status, _) =>
        {
            await completion.Task;
            session.OwnPresenceStatusValue = status;
        };
        using var viewModel = CreateViewModel(session);

        var operation = viewModel.SetOwnPresenceIdleCommand.ExecuteAsync(null);
        Assert.True(viewModel.IsOwnPresenceBusy);
        Assert.Equal(UserPresenceStatus.Idle, viewModel.PendingOwnPresenceStatus);
        Assert.Equal("正在切换为忙碌…", viewModel.OwnPresenceStatusText);
        Assert.False(viewModel.CanSetOwnPresenceIdle);

        completion.SetResult();
        await operation;

        Assert.False(viewModel.IsOwnPresenceBusy);
        Assert.Null(viewModel.PendingOwnPresenceStatus);
        Assert.Equal("在线状态：忙碌", viewModel.OwnPresenceStatusText);
    }

    [Fact]
    public async Task OwnPresence_WhenWriteResultIsUncertain_ShowsUnconfirmedState()
    {
        var session = new FakeSession
        {
            StateValue = new ClientState(connection: new ConnectionState(ConnectionStatus.Connected)),
            Account = AccountId.Create(RealmEndpoint.Parse("https://chat.example.test"), 7),
            CurrentUserId = 7,
            CanSetOwnPresenceValue = true,
            OwnPresenceStatusValue = UserPresenceStatus.Active
        };
        session.SetOwnPresenceAction = (_, _) =>
        {
            session.OwnPresenceStatusValue = null;
            return Task.FromException(
                new GatewayException(GatewayErrorKind.Offline, GatewayErrorCode.NetworkError));
        };
        using var viewModel = CreateViewModel(session);

        await viewModel.SetOwnPresenceOfflineCommand.ExecuteAsync(null);

        Assert.Equal("状态结果未确认", viewModel.OwnPresenceLabel);
        Assert.True(viewModel.ShowOwnPresenceControls);
        Assert.True(viewModel.HasOwnPresenceError);
    }

    [Fact]
    public async Task OwnUserStatus_WhenOfficialPresetAndClearAreSelected_UsesIndependentSessionSetting()
    {
        var session = new FakeSession
        {
            StateValue = new ClientState(connection: new ConnectionState(ConnectionStatus.Connected)),
            Account = AccountId.Create(RealmEndpoint.Parse("https://chat.example.test"), 7),
            CurrentUserId = 7,
            CanSetOwnUserStatusValue = true,
            IsOwnUserStatusConfirmedValue = true
        };
        session.SetOwnUserStatusAction = (status, _) =>
        {
            session.OwnUserStatusValue = status.IsEmpty ? null : status;
            return Task.CompletedTask;
        };
        using var viewModel = CreateViewModel(session);

        await viewModel.SetOwnUserStatusRemoteCommand.ExecuteAsync(null);

        Assert.Equal("远程办公", session.OwnUserStatusValue!.StatusText);
        Assert.Equal("house", session.OwnUserStatusValue.Emoji!.EmojiName);
        Assert.Equal("个人状态：🏠 远程办公", viewModel.OwnUserStatusStatusText);
        Assert.True(viewModel.CanClearOwnUserStatus);

        await viewModel.ClearOwnUserStatusCommand.ExecuteAsync(null);

        Assert.Null(session.OwnUserStatusValue);
        Assert.Equal("个人状态：未设置", viewModel.OwnUserStatusStatusText);
        Assert.False(viewModel.CanClearOwnUserStatus);
    }

    [Fact]
    public void ToggleSettings_WhenInvokedFromProductBar_TogglesBetweenSettingsAndMessages()
    {
        using var viewModel = CreateViewModel(new FakeSession());

        viewModel.ToggleSettingsCommand.Execute(null);

        Assert.True(viewModel.IsSettingsSection);
        Assert.True(viewModel.IsAppearanceSettings);

        viewModel.ToggleSettingsCommand.Execute(null);

        Assert.True(viewModel.IsMessagesSection);
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
    public async Task OpenReactionPickerAtCommand_WhenInvoked_StoresTheQuickActionAnchor()
    {
        var conversation = new DirectMessage([8]);
        var message = new ChatMessage(51, conversation, 8, "hello", DateTimeOffset.UnixEpoch, isRead: true);
        var session = new FakeSession
        {
            Account = AccountId.Create(RealmEndpoint.Parse("https://zulip.example"), 7),
            CurrentUserId = 7,
            Recent = [conversation],
            StateValue = new ClientState(
                messages: new Dictionary<long, ChatMessage> { [message.Id] = message },
                users: new Dictionary<long, UserProfile> { [8] = new UserProfile(8, "Bea") },
                connection: new ConnectionState(ConnectionStatus.Connected))
        };
        using var viewModel = CreateViewModel(session);
        viewModel.ActivateConversation(Assert.Single(viewModel.Conversations));
        await WaitUntilAsync(() => viewModel.CanCompose);
        var item = Assert.Single(viewModel.Messages);

        viewModel.OpenReactionPickerAtCommand.Execute(new ReactionPickerRequest(item, 642.5d, 418d));

        Assert.True(viewModel.IsReactionPickerOpen);
        Assert.Same(item, viewModel.ActiveMessageAction);
        Assert.Equal(642.5d, viewModel.ReactionPickerAnchorX);
        Assert.Equal(418d, viewModel.ReactionPickerAnchorY);
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
    public void EmojiCatalog_WhenLoaded_ContainsTheCompleteZulipUnicodeSet()
    {
        using var viewModel = CreateViewModel(new FakeSession());

        Assert.Equal(1883, viewModel.EmojiChoices.Count);
        Assert.Equal(10, viewModel.EmojiCategories.Count);
        Assert.Equal("常用", viewModel.EmojiCategories[0].Label);
        Assert.True(viewModel.EmojiCategories[0].IsSelected);
        Assert.Equal(24, viewModel.VisibleEmojiChoices.Count);
        Assert.Equal(
            viewModel.EmojiChoices.Count,
            viewModel.EmojiChoices.Select(choice => choice.EmojiCode).Distinct(StringComparer.Ordinal).Count());
        Assert.All(viewModel.EmojiChoices, choice => Assert.Equal("unicode_emoji", choice.ReactionType));
        Assert.Contains(viewModel.EmojiChoices, choice =>
            choice.Emoji == "❤️" &&
            choice.EmojiName == "heart" &&
            choice.EmojiCode == "2764");
        Assert.Contains(viewModel.EmojiChoices, choice =>
            choice.Emoji == "🫠" &&
            choice.EmojiName == "melting_face" &&
            choice.EmojiCode == "1fae0");
        Assert.Contains(viewModel.EmojiChoices, choice =>
            choice.Emoji == "🫡" &&
            choice.EmojiName == "saluting_face" &&
            choice.EmojiCode == "1fae1");
        Assert.Contains(viewModel.EmojiChoices, choice =>
            choice.Emoji == "🇨🇳" &&
            choice.EmojiName == "flag_china" &&
            choice.EmojiCode == "1f1e8-1f1f3");
        Assert.Contains(viewModel.EmojiChoices, choice =>
            choice.Emoji == "👩‍💻" &&
            choice.EmojiName == "woman_technologist" &&
            choice.EmojiCode == "1f469-200d-1f4bb");
    }

    [Fact]
    public void SelectEmojiCategory_WhenChanged_ProjectsOnlyThatCategory()
    {
        using var viewModel = CreateViewModel(new FakeSession());
        var people = viewModel.EmojiCategories.Single(category => category.Key == "people");

        viewModel.SelectEmojiCategoryCommand.Execute(people);

        Assert.True(people.IsSelected);
        Assert.False(viewModel.EmojiCategories[0].IsSelected);
        Assert.Equal(386, viewModel.VisibleEmojiChoices.Count);
        Assert.All(viewModel.VisibleEmojiChoices, choice => Assert.Equal("people", choice.CategoryKey));
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
    public async Task InitializeAsync_WhenNativePreviewIsUsed_ProjectsUnifiedPrivateGroupFixture()
    {
        using var viewModel = CreateViewModel(new NativeShellPreviewSession());

        await viewModel.InitializeAsync();

        Assert.Equal("Acme Workspace", viewModel.WorkspaceDisplayName);
        Assert.True(viewModel.IsNativePreview);
        Assert.False(viewModel.ShowLoadOlderButton);
        Assert.Equal(7, viewModel.Conversations.Count);
        Assert.Contains(viewModel.Conversations, item => item.Title == "产品设计群" && item.IsPrivateGroup);
        Assert.Contains(viewModel.Conversations, item => item.Title == "Windows 客户端群" && item.IsPrivateGroup);
        Assert.DoesNotContain(viewModel.Conversations, item => item.Title is "product" or "release");
        Assert.Contains(viewModel.Conversations, item => item.Title == "Maya Chen" && !item.IsPrivateGroup);
        await WaitUntilAsync(() => viewModel.Conversations.Where(item => item.IsPrivateGroup).All(item => item.AvatarTiles.Count >= 3));
        Assert.Equal(4, viewModel.Messages.Count);
#if DEBUG
        Assert.Equal("4 条未读消息", viewModel.Messages[2].UnreadDividerLabel);
        Assert.True(viewModel.Messages[2].ShowUnreadDivider);
#else
        Assert.Equal("未读消息", viewModel.Messages[2].UnreadDividerLabel);
        Assert.False(viewModel.Messages[2].ShowUnreadDivider);
#endif
    }

    [Fact]
    public async Task ApplyNativePreviewScene_WhenDetailsRequested_LoadsPrivateGroupSettingsFixture()
    {
        using var viewModel = CreateViewModel(new NativeShellPreviewSession());
        await viewModel.InitializeAsync();

        viewModel.ApplyNativePreviewScene("details");

        await WaitUntilAsync(() => viewModel.DetailsMembers.Count == 4);
        Assert.Equal("产品设计群", viewModel.DetailsChannelName);
        Assert.Equal(6, viewModel.DetailsPrivateGroupOwnerId);
        Assert.True(viewModel.CanManagePrivateGroup);
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
    public void ApplyNativePreviewScene_WhenDownloadCenterRequested_ClearsSeededAttention()
    {
        using var viewModel = CreateViewModel(new FakeSession());

        viewModel.ApplyNativePreviewScene("download-center");

        Assert.True(viewModel.IsDownloadCenterOpen);
        Assert.False(viewModel.HasUnseenDownloadFailure);
        Assert.False(viewModel.HasDownloadButtonAttention);
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
    public void OpenImageAttachmentMenuAtCommand_WhenInvoked_StoresImageAndKeepsNormalMessageActions()
    {
        using var viewModel = CreateViewModel(new FakeSession());
        var message = new MessageItem("message-1", 1, 7, "Ada", "![preview](/user_uploads/preview.png)", "10:00");
        var image = new MessageAttachmentItem(
            "image",
            "preview.png",
            "https://chat.example.test/user_uploads/preview.png");

        viewModel.OpenImageAttachmentMenuAtCommand.Execute(
            new ImageAttachmentMenuRequest(message, image, 560d, 320d));

        Assert.True(viewModel.IsMessageMenuOpen);
        Assert.Same(message, viewModel.ActiveMessageAction);
        Assert.Same(image, viewModel.ActiveMessageAttachment);
        Assert.True(viewModel.HasActiveMessageAttachment);
        Assert.Equal(560d, viewModel.MessageMenuAnchorX);
        Assert.Equal(320d, viewModel.MessageMenuAnchorY);

        viewModel.OpenMessageMenuAtCommand.Execute(new MessageMenuRequest(message, 400d, 200d));

        Assert.Null(viewModel.ActiveMessageAttachment);
        Assert.False(viewModel.HasActiveMessageAttachment);
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
        Assert.Equal(string.Empty, viewModel.ComposerText);
        viewModel.ComposerText = "newer input";
        release.SetResult();
        await send;

        Assert.Equal("newer input", viewModel.ComposerText);
        Assert.Equal(["original"], session.SentContents);
    }

    [Fact]
    public async Task SendCommand_WhenServerConfirmationIsPending_ClearsSubmittedTextImmediately()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var conversation = new DirectMessage([8]);
        var session = new FakeSession
        {
            Selected = conversation,
            Recent = [conversation],
            StateValue = new ClientState(connection: new ConnectionState(ConnectionStatus.Connected)),
            SendAction = async (_, cancellationToken) =>
            {
                started.SetResult();
                await release.Task.WaitAsync(cancellationToken);
            }
        };
        using var viewModel = CreateViewModel(session);
        session.Publish();
        viewModel.ComposerText = "send now";

        var send = ((IAsyncRelayCommand)viewModel.SendCommand).ExecuteAsync(null);
        await started.Task;

        Assert.Equal(string.Empty, viewModel.ComposerText);
        Assert.Equal(["send now"], session.SentContents);
        release.SetResult();
        await send;
    }

    [Fact]
    public async Task SendCommand_WhenAttachmentUploadFailsBeforeMessageSend_RestoresSubmittedText()
    {
        var conversation = new DirectMessage([8]);
        var filePicker = new FakeFileSelectionService
        {
            Files =
            [
                new SelectedAttachmentFile(
                    "broken.png",
                    "image/png",
                    3,
                    _ => Task.FromResult<Stream>(new MemoryStream([1, 2, 3])))
            ]
        };
        var session = new FakeSession
        {
            Selected = conversation,
            Recent = [conversation],
            StateValue = new ClientState(connection: new ConnectionState(ConnectionStatus.Connected)),
            UploadAction = (_, _) => Task.FromException<UploadedAttachment>(
                new InvalidOperationException("read failed"))
        };
        using var viewModel = CreateViewModel(session, fileSelectionService: filePicker);
        session.Publish();
        await ((IAsyncRelayCommand)viewModel.PickAttachmentsCommand).ExecuteAsync(null);
        viewModel.ComposerText = "keep this caption";

        await ((IAsyncRelayCommand)viewModel.SendCommand).ExecuteAsync(null);

        Assert.Equal("keep this caption", viewModel.ComposerText);
        Assert.Single(viewModel.Attachments);
        Assert.Empty(session.SentContents);
    }

    [Fact]
    public async Task SendCommand_WhenAttachmentUploadDisconnects_ShowsAttachmentErrorWithoutGlobalServerBanner()
    {
        var conversation = new DirectMessage([8]);
        var filePicker = new FakeFileSelectionService
        {
            Files =
            [
                new SelectedAttachmentFile(
                    "large.bin",
                    "application/octet-stream",
                    3,
                    _ => Task.FromResult<Stream>(new MemoryStream([1, 2, 3])))
            ]
        };
        var session = new FakeSession
        {
            Selected = conversation,
            Recent = [conversation],
            StateValue = new ClientState(connection: new ConnectionState(ConnectionStatus.Connected)),
            UploadAction = (_, _) => Task.FromException<UploadedAttachment>(
                new GatewayException(GatewayErrorKind.Offline, GatewayErrorCode.NetworkError))
        };
        using var viewModel = CreateViewModel(session, fileSelectionService: filePicker);
        session.Publish();
        await ((IAsyncRelayCommand)viewModel.PickAttachmentsCommand).ExecuteAsync(null);
        viewModel.ComposerText = "keep this caption";

        await ((IAsyncRelayCommand)viewModel.SendCommand).ExecuteAsync(null);

        Assert.Equal("keep this caption", viewModel.ComposerText);
        Assert.Contains("附件上传结果未知", viewModel.AttachmentError, StringComparison.Ordinal);
        Assert.Null(viewModel.LoginError);
        Assert.Equal(AttachmentUploadStatus.Uncertain, Assert.Single(viewModel.Attachments).Status);
        Assert.Empty(session.SentContents);
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
    public async Task SendCommand_WhenSessionPublishesDuringSuccessfulSend_ClearsUnchangedDraft()
    {
        var conversation = new DirectMessage([8]);
        var session = new FakeSession
        {
            Selected = conversation,
            Recent = [conversation],
            StateValue = new ClientState(connection: new ConnectionState(RelayCove.Core.ConnectionStatus.Connected))
        };
        session.SendAction = (_, _) =>
        {
            session.Publish();
            return Task.CompletedTask;
        };
        using var viewModel = CreateViewModel(session);
        session.Publish();
        viewModel.ComposerText = "confirmed while state updates";

        await ((IAsyncRelayCommand)viewModel.SendCommand).ExecuteAsync(null);

        Assert.Equal(string.Empty, viewModel.ComposerText);
        Assert.Equal(["confirmed while state updates"], session.SentContents);
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
    public void SessionStateChanged_WhenLatestHistoryRefreshes_DoesNotShowOlderLoadingState()
    {
        var conversation = new DirectMessage([8]);
        var session = new FakeSession
        {
            Selected = conversation,
            HistoryState = new ConversationHistoryState(conversation, 1, true, false, true, 50, null),
            StateValue = new ClientState(connection: new ConnectionState(ConnectionStatus.Connected))
        };
        using var viewModel = CreateViewModel(session);

        Assert.False(viewModel.IsLoadingOlder);
        Assert.True(viewModel.ShowLoadOlderButton);

        session.HistoryState = new ConversationHistoryState(conversation, 1, false, true, false, 1, "history_failed");
        session.Publish();

        Assert.False(viewModel.IsLoadingOlder);
        Assert.True(viewModel.HasReachedOldestMessage);
        Assert.False(viewModel.ShowLoadOlderButton);
        Assert.Equal("无法加载更早消息，请稍后重试。", viewModel.MessageLoadError);
    }

    [Fact]
    public async Task LoadOlder_WhenRequestIsInFlight_ShowsOnlyOlderLoadingState()
    {
        var conversation = new DirectMessage([8]);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new FakeSession
        {
            Selected = conversation,
            HistoryState = new ConversationHistoryState(conversation, 1, false, false, true, 50, null),
            StateValue = new ClientState(connection: new ConnectionState(ConnectionStatus.Connected)),
            LoadOlderAction = async cancellationToken =>
            {
                await completion.Task.WaitAsync(cancellationToken);
            }
        };
        using var viewModel = CreateViewModel(session);

        var loadTask = ((IAsyncRelayCommand)viewModel.LoadOlderCommand).ExecuteAsync(null);
        await WaitUntilAsync(() => viewModel.IsLoadingOlder);

        Assert.True(viewModel.IsLoadingOlder);
        completion.SetResult();
        await loadTask;

        Assert.False(viewModel.IsLoadingOlder);
    }

    [Fact]
    public async Task MessageViewport_WhenNearTop_WaitsForExplicitUpwardTopInput()
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

        Assert.Equal(0, session.LoadOlderCalls);

        await viewModel.RequestOlderFromTopInputAsync(
            1_400,
            conversation.CanonicalKey,
            session.HistoryState.Generation);

        Assert.Equal(1, session.LoadOlderCalls);
    }

    [Fact]
    public async Task MessageViewport_WhenAlreadyAtTopAndWheelContinues_LoadsOlderWithoutScrollMovement()
    {
        var conversation = new DirectMessage([8]);
        var session = new FakeSession
        {
            Selected = conversation,
            HistoryState = new ConversationHistoryState(conversation, 1, false, false, true, 50, null),
            StateValue = new ClientState(connection: new ConnectionState(ConnectionStatus.Connected))
        };
        using var viewModel = CreateViewModel(session);

        await viewModel.RequestOlderFromTopInputAsync(
            1_000,
            conversation.CanonicalKey,
            session.HistoryState.Generation);
        await viewModel.RequestOlderFromTopInputAsync(
            1_100,
            conversation.CanonicalKey,
            session.HistoryState.Generation);
        await viewModel.RequestOlderFromTopInputAsync(
            1_400,
            conversation.CanonicalKey,
            session.HistoryState.Generation);

        Assert.Equal(2, session.LoadOlderCalls);
    }

    [Fact]
    public async Task LoadOlder_WhenActivationHasError_ManualCommandStillWorksWhileInlineButtonIsHidden()
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
        Assert.False(viewModel.ShowLoadOlderButton);
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
        var initialScrollRequest = Assert.IsType<MessageScrollRequest>(viewModel.PendingMessageScrollRequest);
        await viewModel.ReportMessageViewportAsync(1, 1, 120, 1_000);

        session.StateValue = session.StateValue with
        {
            Messages = new Dictionary<long, ChatMessage>(messages)
            {
                [7] = new ChatMessage(7, conversation, 8, "message 7", DateTimeOffset.UnixEpoch.AddMinutes(7), senderDisplayName: "Bea")
            }
        };
        session.Publish();

        Assert.Equal(initialScrollRequest, viewModel.PendingMessageScrollRequest);
        Assert.Equal(1, viewModel.NewMessageCount);
        Assert.True(viewModel.ShowNewMessagesButton);

        viewModel.ScrollToLatestCommand.Execute(null);

        var manualJump = Assert.IsType<MessageScrollRequest>(viewModel.PendingMessageScrollRequest);
        Assert.True(manualJump.Sequence > initialScrollRequest.Sequence);
        Assert.Equal(MessageScrollReason.ManualJumpToLatest, manualJump.Reason);
        Assert.Equal(1, viewModel.NewMessageCount);

        viewModel.AcknowledgeMessageScrollRequest(manualJump);

        Assert.Equal(0, viewModel.NewMessageCount);
    }

    [Fact]
    public async Task MessageViewport_WhenMoreThanTwoPagesFromLatest_ShowsJumpButtonUntilAcknowledged()
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
        viewModel.AcknowledgeMessageScrollRequest(
            Assert.IsType<MessageScrollRequest>(viewModel.PendingMessageScrollRequest));

        await viewModel.ReportMessageViewportAsync(
            firstVisibleItemIndex: 5,
            lastVisibleItemIndex: 5,
            verticalOffset: 1000d,
            timestampMilliseconds: 1_000,
            bottomDistanceDip: 1000.1d,
            viewportHeightDip: 500d);

        Assert.Equal(0, viewModel.NewMessageCount);
        Assert.True(viewModel.ShowNewMessagesButton);
        Assert.Equal("跳转到最新消息", viewModel.NewMessagesButtonText);

        viewModel.ScrollToLatestCommand.Execute(null);
        var jumpRequest = Assert.IsType<MessageScrollRequest>(viewModel.PendingMessageScrollRequest);
        Assert.Equal(MessageScrollReason.ManualJumpToLatest, jumpRequest.Reason);

        viewModel.AcknowledgeMessageScrollRequest(jumpRequest);

        Assert.False(viewModel.ShowNewMessagesButton);
    }

    [Fact]
    public async Task MessageViewport_WhenOldConversationReportsAfterSwitch_DoesNotShowJumpButton()
    {
        var first = new DirectMessage([8]);
        var second = new DirectMessage([9]);
        var firstMessage = new ChatMessage(1, first, 8, "first", DateTimeOffset.UnixEpoch, senderDisplayName: "Bea");
        var session = new FakeSession
        {
            Selected = first,
            HistoryState = new ConversationHistoryState(first, 1, false, true, false, 1, null),
            StateValue = new ClientState(
                messages: new Dictionary<long, ChatMessage> { [1] = firstMessage },
                connection: new ConnectionState(ConnectionStatus.Connected))
        };
        using var viewModel = CreateViewModel(session);
        viewModel.AcknowledgeMessageScrollRequest(
            Assert.IsType<MessageScrollRequest>(viewModel.PendingMessageScrollRequest));

        await viewModel.ReportMessageViewportAsync(
            0,
            0,
            1000d,
            bottomDistanceDip: 1000.1d,
            viewportHeightDip: 500d,
            expectedConversationKey: first.CanonicalKey,
            expectedHistoryGeneration: 1);
        Assert.True(viewModel.ShowNewMessagesButton);

        session.Selected = second;
        session.HistoryState = new ConversationHistoryState(second, 2, false, true, false, null, null);
        session.StateValue = new ClientState(connection: new ConnectionState(ConnectionStatus.Connected));
        session.Publish();
        Assert.False(viewModel.ShowNewMessagesButton);

        await viewModel.ReportMessageViewportAsync(
            0,
            0,
            1000d,
            bottomDistanceDip: 1000.1d,
            viewportHeightDip: 500d,
            expectedConversationKey: first.CanonicalKey,
            expectedHistoryGeneration: 1);

        Assert.False(viewModel.ShowNewMessagesButton);
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
    public void SessionStateChanged_WhenOutboxIsInitiallyHidden_ProjectsAnimatedOptimisticMessageImmediately()
    {
        var conversation = new DirectMessage([8]);
        var outbox = new OutboxEntry(
            "10",
            conversation,
            "send immediately",
            DateTimeOffset.UnixEpoch,
            OutboxState.Hidden);
        var session = new FakeSession
        {
            CurrentUserId = 1,
            Selected = conversation,
            StateValue = new ClientState(
                outbox: new Dictionary<string, OutboxEntry> { [outbox.LocalId] = outbox },
                users: new Dictionary<long, UserProfile>
                {
                    [1] = new UserProfile(1, "Current user", avatarUrl: "https://example.test/avatar.png")
                },
                connection: new ConnectionState(ConnectionStatus.Connected))
        };
        using var viewModel = CreateViewModel(session);

        var message = Assert.Single(viewModel.Messages);

        Assert.Equal("local-10", message.Id);
        Assert.Null(message.MessageId);
        Assert.True(message.IsOwn);
        Assert.Equal("Current user", message.Sender);
        Assert.Equal("https://example.test/avatar.png", message.SenderAvatarUrl);
        Assert.Equal("send immediately", message.Content);
        Assert.False(message.HasDeliveryState);
        Assert.False(message.CanRecover);
        Assert.True(message.IsInsertionAnimationPending);
    }

    [Fact]
    public void SessionStateChanged_WhenOptimisticMessageIsConfirmed_UpdatesSameRowWithoutCollectionReplacement()
    {
        var conversation = new DirectMessage([8]);
        var outbox = new OutboxEntry(
            "11",
            conversation,
            "send immediately",
            DateTimeOffset.UnixEpoch,
            OutboxState.Hidden);
        var session = new FakeSession
        {
            CurrentUserId = 1,
            Selected = conversation,
            StateValue = new ClientState(
                outbox: new Dictionary<string, OutboxEntry> { [outbox.LocalId] = outbox },
                connection: new ConnectionState(ConnectionStatus.Connected))
        };
        using var viewModel = CreateViewModel(session);
        var optimistic = Assert.Single(viewModel.Messages);
        var changes = new List<NotifyCollectionChangedAction>();
        viewModel.Messages.CollectionChanged += (_, eventArgs) => changes.Add(eventArgs.Action);

        var confirmed = new ChatMessage(
            501,
            conversation,
            1,
            "send immediately",
            DateTimeOffset.UnixEpoch,
            isRead: true,
            clientLocalId: outbox.LocalId);
        session.StateValue = new ClientState(
            messages: new Dictionary<long, ChatMessage> { [confirmed.Id] = confirmed },
            connection: new ConnectionState(ConnectionStatus.Connected));
        session.Publish();

        var message = Assert.Single(viewModel.Messages);
        Assert.Same(optimistic, message);
        Assert.Equal(501, message.MessageId);
        Assert.Empty(changes);
    }

    [Fact]
    public void MessageItem_InsertionAnimation_IsConsumedOnlyOnce()
    {
        var message = new MessageItem(
            "local-12",
            null,
            1,
            "你",
            "hello",
            "10:00",
            isOwn: true,
            animateInsertion: true);

        Assert.True(message.TryConsumeInsertionAnimation());
        Assert.False(message.TryConsumeInsertionAnimation());
        Assert.False(message.IsInsertionAnimationPending);
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
        var conversation = new ChannelTopic(4, string.Empty);
        var session = new FakeSession
        {
            CurrentUserId = 7,
            StateValue = new ClientState(
                subscriptions: new Dictionary<long, Subscription> { [4] = PrivateGroupSubscription() },
                users: new Dictionary<long, UserProfile> { [8] = new UserProfile(8, "Bea") },
                topics: new Dictionary<string, TopicSummary> { [conversation.CanonicalKey] = new TopicSummary(4, string.Empty, 5) },
                messages: new Dictionary<long, ChatMessage> { [5] = new ChatMessage(5, conversation, 8, "native search", DateTimeOffset.UnixEpoch, senderDisplayName: "Bea") },
                connection: new ConnectionState(ConnectionStatus.Connected))
        };
        using var viewModel = CreateViewModel(session);
        session.Publish();

        viewModel.SearchQuery = "native search";

        Assert.Collection(
            viewModel.SearchResults,
            conversationResult =>
            {
                Assert.Equal("群聊", conversationResult.Kind);
                Assert.Equal(conversation, conversationResult.Conversation);
            },
            messageResult =>
            {
                Assert.Equal("已加载消息", messageResult.Kind);
                Assert.Equal(conversation, messageResult.Conversation);
            });
        Assert.DoesNotContain(viewModel.SearchResults, item => item.Subtitle.Contains("在线", StringComparison.Ordinal));
    }

    [Fact]
    public void SearchCategory_WhenSelected_FiltersLoadedMessagesByContentType()
    {
        var conversation = new DirectMessage([8]);
        var session = new FakeSession
        {
            Account = AccountId.Create(RealmEndpoint.Parse("https://zulip.example"), 7),
            ActiveRealm = RealmEndpoint.Parse("https://zulip.example"),
            CurrentUserId = 7,
            Recent = [conversation],
            StateValue = new ClientState(
                messages: new Dictionary<long, ChatMessage>
                {
                    [1] = new ChatMessage(1, conversation, 8, "plain", DateTimeOffset.UnixEpoch),
                    [2] = new ChatMessage(2, conversation, 8, "[notes](/user_uploads/1/notes.pdf)", DateTimeOffset.UnixEpoch),
                    [3] = new ChatMessage(3, conversation, 8, "![shot](/user_uploads/1/shot.png)", DateTimeOffset.UnixEpoch),
                    [4] = new ChatMessage(4, conversation, 8, "[clip](/user_uploads/1/clip.mp4)", DateTimeOffset.UnixEpoch),
                    [5] = new ChatMessage(5, conversation, 8, "https://example.test/page", DateTimeOffset.UnixEpoch)
                },
                users: new Dictionary<long, UserProfile> { [8] = new UserProfile(8, "Bea") },
                connection: new ConnectionState(ConnectionStatus.Connected))
        };
        using var viewModel = CreateViewModel(session);
        session.Publish();

        viewModel.SelectSearchCategoryCommand.Execute(viewModel.SearchCategories.Single(item => item.Filter == MessageSearchFilter.Images));
        var image = Assert.Single(viewModel.SearchResults);
        Assert.Equal("message:3", image.Id);
        Assert.Equal("图片", image.Kind);

        viewModel.SelectSearchCategoryCommand.Execute(viewModel.SearchCategories.Single(item => item.Filter == MessageSearchFilter.Videos));
        var video = Assert.Single(viewModel.SearchResults);
        Assert.Equal("message:4", video.Id);
        Assert.Equal("视频", video.Kind);

        viewModel.SelectSearchCategoryCommand.Execute(viewModel.SearchCategories.Single(item => item.Filter == MessageSearchFilter.Files));
        var file = Assert.Single(viewModel.SearchResults);
        Assert.Equal("message:2", file.Id);
        Assert.Equal("文件", file.Kind);

        viewModel.SelectSearchCategoryCommand.Execute(viewModel.SearchCategories.Single(item => item.Filter == MessageSearchFilter.Links));
        var link = Assert.Single(viewModel.SearchResults);
        Assert.Equal("message:5", link.Id);
        Assert.Equal("链接", link.Kind);

        viewModel.SelectSearchCategoryCommand.Execute(viewModel.SearchCategories.Single(item => item.Filter == MessageSearchFilter.Messages));
        Assert.All(
            viewModel.SearchResults.Where(item => item.MessageId is not null),
            item => Assert.Equal("已加载消息", item.Kind));
    }

    [Fact]
    public async Task SearchCategory_WhenMediaFilterHasNoKeyword_RequestsServerFilter()
    {
        MessageSearchFilter? requestedFilter = null;
        var session = new FakeSession
        {
            Account = AccountId.Create(RealmEndpoint.Parse("https://zulip.example"), 7),
            SearchMessagesWithFilterAction = (_, _, _, filter, _) =>
            {
                requestedFilter = filter;
                return Task.FromResult(new MessageQueryPage([], true, true, true));
            }
        };
        using var viewModel = CreateViewModel(session);
        viewModel.OpenSearchCommand.Execute(null);
        viewModel.SelectSearchCategoryCommand.Execute(viewModel.SearchCategories.Single(item => item.Filter == MessageSearchFilter.Images));

        await ((IAsyncRelayCommand)viewModel.SearchNowCommand).ExecuteAsync(null);

        Assert.Equal(MessageSearchFilter.Images, requestedFilter);
    }

    [Fact]
    public void UnifiedConversations_WhenMixedZulipConversationsExist_FiltersAndSortsOnlySupportedRows()
    {
        var account = AccountId.Create(RealmEndpoint.Parse("https://zulip.example"), 7);
        var group = new ChannelTopic(4, string.Empty);
        var publicChannel = new ChannelTopic(5, string.Empty);
        var webPublic = new ChannelTopic(6, string.Empty);
        var legacyTopic = new ChannelTopic(7, "release");
        var direct = new DirectMessage([8]);
        var self = new DirectMessage([]);
        var groupDirect = new DirectMessage([8, 9]);
        var preferences = new InMemoryConversationPreferencesStore();
        var session = new FakeSession
        {
            Account = account,
            CurrentUserId = 7,
            Recent = [direct, self, groupDirect],
            StateValue = new ClientState(
                messages: new Dictionary<long, ChatMessage>
                {
                    [1] = new ChatMessage(1, group, 8, "visible group", DateTimeOffset.UnixEpoch.AddSeconds(10)),
                    [2] = new ChatMessage(2, direct, 8, "visible direct", DateTimeOffset.UnixEpoch.AddSeconds(30)),
                    [3] = new ChatMessage(3, self, 7, "visible self", DateTimeOffset.UnixEpoch.AddSeconds(20)),
                    [4] = new ChatMessage(4, publicChannel, 8, "hidden public", DateTimeOffset.UnixEpoch.AddSeconds(40)),
                    [5] = new ChatMessage(5, webPublic, 8, "hidden web", DateTimeOffset.UnixEpoch.AddSeconds(50)),
                    [6] = new ChatMessage(6, legacyTopic, 8, "hidden topic", DateTimeOffset.UnixEpoch.AddSeconds(60)),
                    [7] = new ChatMessage(7, groupDirect, 8, "hidden group dm", DateTimeOffset.UnixEpoch.AddSeconds(70))
                },
                subscriptions: new Dictionary<long, Subscription>
                {
                    [4] = PrivateGroupSubscription(4, "产品设计群") with { IsPinned = true },
                    [5] = new Subscription(5, "public", isPrivate: false, topicsPolicy: ChannelTopicsPolicy.EmptyTopicOnly, isWebPublic: false),
                    [6] = new Subscription(6, "web", isPrivate: true, topicsPolicy: ChannelTopicsPolicy.EmptyTopicOnly, isWebPublic: true),
                    [7] = new Subscription(7, "legacy", isPrivate: true, topicsPolicy: ChannelTopicsPolicy.Inherit, isWebPublic: false)
                },
                users: new Dictionary<long, UserProfile>
                {
                    [7] = new UserProfile(7, "Ada"),
                    [8] = new UserProfile(8, "Bea"),
                    [9] = new UserProfile(9, "Chen")
                },
                connection: new ConnectionState(ConnectionStatus.Connected))
        };
        using var viewModel = CreateViewModel(session, conversationPreferencesStore: preferences);
        session.Publish();

        Assert.Equal(
            [group.CanonicalKey, direct.CanonicalKey, self.CanonicalKey],
            viewModel.Conversations.Select(item => item.Conversation.CanonicalKey));
        Assert.DoesNotContain(viewModel.Conversations, item => item.Conversation == groupDirect);
        viewModel.ConversationFilterQuery = "产品设计";
        Assert.Equal(group, Assert.Single(viewModel.FilteredConversations).Conversation);
        viewModel.SearchQuery = "hidden";
        Assert.Empty(viewModel.SearchResults);
    }

    [Fact]
    public void ConversationFilter_WhenPartialTextMatchesOlderCachedMessage_ReturnsMatchedConversation()
    {
        var direct = new DirectMessage([8]);
        var firstMatch = new ChatMessage(1, direct, 8, "historical fragment one", DateTimeOffset.UnixEpoch);
        var secondMatch = new ChatMessage(2, direct, 8, "historical fragment two", DateTimeOffset.UnixEpoch.AddSeconds(1));
        var latest = new ChatMessage(3, direct, 8, "latest unrelated", DateTimeOffset.UnixEpoch.AddSeconds(2));
        var session = new FakeSession
        {
            CurrentUserId = 7,
            Recent = [direct],
            StateValue = new ClientState(
                messages: new Dictionary<long, ChatMessage>
                {
                    [firstMatch.Id] = firstMatch,
                    [secondMatch.Id] = secondMatch,
                    [latest.Id] = latest
                },
                users: new Dictionary<long, UserProfile> { [8] = new UserProfile(8, "Bea") },
                connection: new ConnectionState(ConnectionStatus.Connected))
        };
        using var viewModel = CreateViewModel(session);

        Assert.True(viewModel.ShowConversationSearchIcon);

        viewModel.ConversationFilterQuery = "torical frag";

        Assert.False(viewModel.ShowConversationSearchIcon);
        Assert.Equal(2, viewModel.FilteredConversations.Count);
        Assert.All(viewModel.FilteredConversations, result => Assert.Equal(direct, result.Conversation));
        Assert.Equal([secondMatch.Id, firstMatch.Id], viewModel.FilteredConversations.Select(result => result.SearchTargetMessageId));
        Assert.All(viewModel.FilteredConversations, result => Assert.True(result.IsSearchMessageMatch));
        Assert.Equal(
            ["historical fragment two", "historical fragment one"],
            viewModel.FilteredConversations.Select(result => result.Detail));
    }

    [Fact]
    public async Task ConversationFilter_WhenServerFindsHistoricalConversation_AddsAndOpensMatchedMessage()
    {
        var direct = new DirectMessage([8]);
        var olderDirect = new DirectMessage([9]);
        var match = new ChatMessage(41, direct, 8, "remote archive fragment", DateTimeOffset.UnixEpoch);
        var sameConversationMatch = new ChatMessage(40, direct, 8, "another archive fragment", DateTimeOffset.UnixEpoch);
        var olderMatch = new ChatMessage(20, olderDirect, 9, "older archive fragment", DateTimeOffset.UnixEpoch);
        var searchCalls = 0;
        var session = new FakeSession
        {
            Account = AccountId.Create(RealmEndpoint.Parse("https://zulip.example"), 7),
            CurrentUserId = 7,
            StateValue = new ClientState(
                users: new Dictionary<long, UserProfile>
                {
                    [8] = new UserProfile(8, "Bea"),
                    [9] = new UserProfile(9, "Chen")
                },
                connection: new ConnectionState(ConnectionStatus.Connected)),
            SearchMessagesAction = (query, beforeMessageId, limit, _) =>
            {
                Assert.Equal("archive frag", query);
                Assert.Equal(50, limit);
                searchCalls++;
                return beforeMessageId is null
                    ? Task.FromResult(new MessageQueryPage([match, sameConversationMatch], false, true, true))
                    : Task.FromResult(new MessageQueryPage([olderMatch], true, true, true));
            }
        };
        session.OpenMessageAction = (openedConversation, messageId, _) =>
        {
            session.Selected = openedConversation;
            session.HistoryState = new ConversationHistoryState(openedConversation, 2, false, false, false, 40, null);
            session.StateValue = session.StateValue with
            {
                Messages = new Dictionary<long, ChatMessage>
                {
                    [sameConversationMatch.Id] = sameConversationMatch,
                    [match.Id] = match
                }
            };
            session.Publish();
            return Task.CompletedTask;
        };
        using var viewModel = CreateViewModel(session);

        viewModel.ConversationFilterQuery = "archive frag";
        await WaitUntilAsync(() => !viewModel.IsConversationFilterBusy);

        Assert.Equal(2, viewModel.FilteredConversations.Count);
        var result = viewModel.FilteredConversations[0];
        Assert.Equal(direct, result.Conversation);
        Assert.Equal(match.Id, result.SearchTargetMessageId);
        Assert.True(viewModel.HasMoreConversationFilterResults);
        Assert.True(viewModel.ShowMoreConversationFilterResults);

        viewModel.ClearConversationFilter();

        Assert.False(viewModel.HasMoreConversationFilterResults);
        Assert.False(viewModel.ShowMoreConversationFilterResults);

        viewModel.ConversationFilterQuery = "archive frag";
        await WaitUntilAsync(() => !viewModel.IsConversationFilterBusy);

        await ((IAsyncRelayCommand)viewModel.LoadMoreConversationFilterCommand).ExecuteAsync(null);

        Assert.Equal(3, viewModel.FilteredConversations.Count);
        Assert.False(viewModel.HasMoreConversationFilterResults);
        Assert.False(viewModel.ShowMoreConversationFilterResults);
        Assert.Equal(3, searchCalls);

        viewModel.ActivateConversation(result);
        await WaitUntilAsync(() => session.OpenedMessages.Count == 1);

        Assert.Equal((direct, match.Id), Assert.Single(session.OpenedMessages));
        var request = Assert.IsType<MessageScrollRequest>(viewModel.PendingMessageScrollRequest);
        Assert.Equal(match.Id, request.TargetMessageId);
        Assert.Equal(MessageScrollReason.MessageAnchor, request.Reason);
    }

    [Fact]
    public async Task ConversationFilter_WhenQueryChanges_DiscardsSupersededServerResults()
    {
        var firstDirect = new DirectMessage([8]);
        var secondDirect = new DirectMessage([9]);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstPage = new TaskCompletionSource<MessageQueryPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondMessage = new ChatMessage(52, secondDirect, 9, "second result", DateTimeOffset.UnixEpoch);
        var session = new FakeSession
        {
            Account = AccountId.Create(RealmEndpoint.Parse("https://zulip.example"), 7),
            CurrentUserId = 7,
            StateValue = new ClientState(
                users: new Dictionary<long, UserProfile>
                {
                    [8] = new UserProfile(8, "Bea"),
                    [9] = new UserProfile(9, "Chen")
                },
                connection: new ConnectionState(ConnectionStatus.Connected)),
            SearchMessagesAction = async (query, _, _, cancellationToken) =>
            {
                if (query == "first")
                {
                    firstStarted.SetResult();
                    return await firstPage.Task.WaitAsync(cancellationToken);
                }
                return new MessageQueryPage([secondMessage], true, true, true);
            }
        };
        using var viewModel = CreateViewModel(session);

        viewModel.ConversationFilterQuery = "first";
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        viewModel.ConversationFilterQuery = "second";
        await WaitUntilAsync(() => !viewModel.IsConversationFilterBusy);
        firstPage.TrySetResult(new MessageQueryPage(
            [new ChatMessage(51, firstDirect, 8, "first result", DateTimeOffset.UnixEpoch)],
            true,
            true,
            true));
        await Task.Delay(50);

        Assert.Equal(secondDirect, Assert.Single(viewModel.FilteredConversations).Conversation);
    }

    [Fact]
    public async Task SessionStateChanged_WhenSelectedGroupLosesEligibility_ClearsMessagesAndDisablesComposer()
    {
        var group = new ChannelTopic(4, string.Empty);
        var message = new ChatMessage(1, group, 8, "visible", DateTimeOffset.UnixEpoch, isRead: true);
        var session = new FakeSession
        {
            Account = AccountId.Create(RealmEndpoint.Parse("https://zulip.example"), 7),
            CurrentUserId = 7,
            StateValue = new ClientState(
                messages: new Dictionary<long, ChatMessage> { [message.Id] = message },
                subscriptions: new Dictionary<long, Subscription> { [4] = PrivateGroupSubscription() },
                users: new Dictionary<long, UserProfile> { [8] = new UserProfile(8, "Bea") },
                connection: new ConnectionState(ConnectionStatus.Connected))
        };
        using var viewModel = CreateViewModel(session);

        viewModel.ActivateConversation(Assert.Single(viewModel.Conversations));
        await WaitUntilAsync(() => viewModel.CanCompose);
        Assert.Single(viewModel.Messages);

        session.StateValue = session.StateValue with
        {
            Subscriptions = new Dictionary<long, Subscription>
            {
                [4] = PrivateGroupSubscription() with { TopicsPolicy = ChannelTopicsPolicy.Inherit }
            }
        };
        session.Publish();

        Assert.False(viewModel.HasSelectedConversation);
        Assert.False(viewModel.IsConversationContentVisible);
        Assert.False(viewModel.CanCompose);
        Assert.Empty(viewModel.Messages);
        Assert.Empty(viewModel.Conversations);
    }

    [Fact]
    public async Task ServerSearchAndSaved_WhenResultsContainHiddenConversations_FilterAndRejectDirectOpen()
    {
        var group = new ChannelTopic(4, string.Empty);
        var publicChannel = new ChannelTopic(5, string.Empty);
        var direct = new DirectMessage([8]);
        var groupDirect = new DirectMessage([8, 9]);
        var messages = new[]
        {
            new ChatMessage(1, group, 8, "group", DateTimeOffset.UnixEpoch),
            new ChatMessage(2, direct, 8, "direct", DateTimeOffset.UnixEpoch),
            new ChatMessage(3, publicChannel, 8, "public", DateTimeOffset.UnixEpoch),
            new ChatMessage(4, groupDirect, 8, "group dm", DateTimeOffset.UnixEpoch)
        };
        var session = new FakeSession
        {
            Account = AccountId.Create(RealmEndpoint.Parse("https://zulip.example"), 7),
            CurrentUserId = 7,
            StateValue = new ClientState(
                subscriptions: new Dictionary<long, Subscription>
                {
                    [4] = PrivateGroupSubscription(),
                    [5] = new Subscription(5, "public", isPrivate: false, topicsPolicy: ChannelTopicsPolicy.EmptyTopicOnly, isWebPublic: false)
                },
                users: new Dictionary<long, UserProfile> { [8] = new UserProfile(8, "Bea"), [9] = new UserProfile(9, "Chen") },
                connection: new ConnectionState(ConnectionStatus.Connected)),
            SearchMessagesAction = (_, _, _, _) => Task.FromResult(new MessageQueryPage(messages, true, true, true)),
            SavedMessagesAction = (_, _, _) => Task.FromResult(new MessageQueryPage(messages, true, true, true))
        };
        using var viewModel = CreateViewModel(session);

        viewModel.OpenSearchCommand.Execute(null);
        viewModel.SearchQuery = "server";
        await ((IAsyncRelayCommand)viewModel.SearchNowCommand).ExecuteAsync(null);
        Assert.Equal([direct, group], viewModel.SearchResults.Select(item => item.Conversation));

        await ((IAsyncRelayCommand)viewModel.RefreshSavedCommand).ExecuteAsync(null);
        Assert.Equal([direct, group], viewModel.SavedMessages.Select(item => item.Conversation));

        await ((IAsyncRelayCommand<SearchResultItem?>)viewModel.SelectSearchResultCommand).ExecuteAsync(
            new SearchResultItem("hidden", "消息", "hidden", "hidden", publicChannel, 3));
        await ((IAsyncRelayCommand<SavedMessageItem?>)viewModel.OpenSavedMessageCommand).ExecuteAsync(
            new SavedMessageItem(4, groupDirect, "Bea", "hidden", string.Empty));
        Assert.Empty(session.OpenedMessages);
    }

    [Fact]
    public async Task StartNewConversationCommand_WhenMultipleContactsAreClicked_KeepsOnlyOneDirectRecipient()
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
        Assert.Equal([9L], direct.OtherUserIds);
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
        Assert.Equal("caption\n![design\\[1\\].png](https://example.test/user_uploads/design[1].png)", Assert.Single(session.SentContents));
        Assert.Empty(viewModel.Attachments);
        Assert.Equal(string.Empty, viewModel.ComposerText);
    }

    [Fact]
    public async Task SendCommand_WhenAttachmentUploadIsInProgress_ProjectsPerFilePercentage()
    {
        var conversation = new DirectMessage([8]);
        var uploadReported = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseUpload = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var filePicker = new FakeFileSelectionService
        {
            Files =
            [
                new SelectedAttachmentFile(
                    "archive.zip",
                    "application/zip",
                    4,
                    _ => Task.FromResult<Stream>(new MemoryStream([1, 2, 3, 4])))
            ]
        };
        var session = new FakeSession
        {
            Selected = conversation,
            StateValue = new ClientState(connection: new ConnectionState(ConnectionStatus.Connected)),
            UploadAction = async (upload, cancellationToken) =>
            {
                upload.Progress?.Report(new RealmMediaTransferProgress(2, 4));
                uploadReported.SetResult(true);
                await releaseUpload.Task.WaitAsync(cancellationToken);
                upload.Progress?.Report(new RealmMediaTransferProgress(4, 4));
                return new UploadedAttachment(upload.FileName, "https://example.test/user_uploads/archive.zip");
            }
        };
        using var viewModel = CreateViewModel(session, fileSelectionService: filePicker);
        session.Publish();
        await ((IAsyncRelayCommand)viewModel.PickAttachmentsCommand).ExecuteAsync(null);

        var send = ((IAsyncRelayCommand)viewModel.SendCommand).ExecuteAsync(null);
        await uploadReported.Task;
        var attachment = Assert.Single(viewModel.Attachments);

        Assert.Equal(AttachmentUploadStatus.Uploading, attachment.Status);
        Assert.Equal(0.5d, attachment.UploadProgress);
        Assert.Equal("正在上传 50%", attachment.StatusLabel);

        releaseUpload.SetResult(true);
        await send;
        Assert.Empty(viewModel.Attachments);
    }

    [Fact]
    public void AddPastedImage_WhenClipboardProvidesPng_UsesValidatedAttachmentDraftPath()
    {
        using var viewModel = CreateViewModel(new FakeSession());
        var screenshot = new SelectedAttachmentFile(
            "screenshot-20260826-120000.png",
            "image/png",
            3,
            _ => Task.FromResult<Stream>(new MemoryStream([1, 2, 3])),
            openPreviewStream: () => new MemoryStream([1, 2, 3]));

        viewModel.AddPastedImageCommand.Execute(screenshot);

        var draft = Assert.Single(viewModel.Attachments);
        Assert.True(draft.IsImage);
        Assert.True(draft.HasPreview);
        Assert.Equal("screenshot-20260826-120000.png", draft.FileName);
        Assert.True(viewModel.HasAttachments);
    }

    [Fact]
    public void AddPastedImage_WhenClipboardImageCouldNotBeRead_ShowsSafeError()
    {
        using var viewModel = CreateViewModel(new FakeSession());

        viewModel.AddPastedImageCommand.Execute(null);

        Assert.Equal("无法读取剪贴板中的截图。", viewModel.AttachmentError);
        Assert.Empty(viewModel.Attachments);
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
        Assert.All(
            session.SentContents,
            sent => Assert.Equal("[notes.txt](https://example.test/user_uploads/notes.txt)", sent));
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
        Assert.Equal("已保存 guide.pdf", viewModel.MediaActionStatus);
        Assert.True(viewModel.HasKnownMediaDownloadLength);
        Assert.Equal(1d, viewModel.MediaDownloadProgress);
        Assert.Contains("3 B / 3 B", viewModel.MediaDownloadProgressText);
    }

    [Fact]
    public async Task DownloadAttachmentCommand_WhenSavePickerIsCancelled_DoesNotStartNetworkRead()
    {
        var media = new FakeRealmMediaService();
        var save = new FakeFileSaveService { Result = false };
        using var viewModel = CreateViewModel(
            new FakeSession(),
            realmMediaService: media,
            fileSaveService: save);
        var attachment = new MessageAttachmentItem("file", "guide.pdf", "/user_uploads/guide.pdf");

        await ((IAsyncRelayCommand)viewModel.DownloadAttachmentCommand).ExecuteAsync(attachment);

        Assert.Equal(0, media.FileCalls);
        Assert.Equal("已取消保存", viewModel.MediaActionStatus);
    }

    [Fact]
    public async Task DownloadAttachmentCommand_WhenDownloadFails_ExposesRetryAndRetrySucceeds()
    {
        var media = new FakeRealmMediaService
        {
            DownloadFailure = new GatewayException(GatewayErrorKind.Offline, GatewayErrorCode.NetworkError)
        };
        using var viewModel = CreateViewModel(
            new FakeSession(),
            realmMediaService: media,
            fileSaveService: new FakeFileSaveService());
        var attachment = new MessageAttachmentItem("file", "guide.pdf", "/user_uploads/guide.pdf");

        await ((IAsyncRelayCommand)viewModel.DownloadAttachmentCommand).ExecuteAsync(attachment);

        Assert.True(viewModel.CanRetryMediaDownload);
        media.DownloadFailure = null;
        viewModel.RetryMediaDownloadCommand.Execute(null);
        await WaitUntilAsync(() => media.FileCalls == 2 && !viewModel.IsMediaActionBusy);

        Assert.Equal(2, media.FileCalls);
        Assert.False(viewModel.CanRetryMediaDownload);
        Assert.Equal("已保存 guide.pdf", viewModel.MediaActionStatus);
    }

    [Fact]
    public async Task DownloadCenter_WhenFailedDownloadIsRemoved_ClearsFailureAttention()
    {
        var media = new FakeRealmMediaService
        {
            DownloadFailure = new GatewayException(GatewayErrorKind.Offline, GatewayErrorCode.NetworkError)
        };
        using var viewModel = CreateViewModel(
            new FakeSession(),
            realmMediaService: media,
            fileSaveService: new FakeFileSaveService());

        await ((IAsyncRelayCommand)viewModel.DownloadAttachmentCommand).ExecuteAsync(
            new MessageAttachmentItem("file", "guide.pdf", "/user_uploads/guide.pdf"));
        Assert.True(viewModel.HasDownloadFailure);
        Assert.True(viewModel.HasUnseenDownloadFailure);

        viewModel.ToggleDownloadCenterCommand.Execute(null);

        Assert.True(viewModel.HasDownloadFailure);
        Assert.False(viewModel.HasUnseenDownloadFailure);
        Assert.False(viewModel.HasDownloadButtonAttention);

        viewModel.DismissFailedMediaDownloadCommand.Execute(null);

        Assert.False(viewModel.HasDownloadFailure);
        Assert.False(viewModel.HasUnseenDownloadFailure);
        Assert.False(viewModel.HasDownloadButtonAttention);
        Assert.Null(viewModel.MediaDownloadFileName);
    }

    [Fact]
    public async Task DownloadSettingsCommands_UpdateFolderAndOpenIt()
    {
        var save = new FakeFileSaveService();
        using var viewModel = CreateViewModel(new FakeSession(), fileSaveService: save);

        await ((IAsyncRelayCommand)viewModel.ChangeDownloadFolderCommand).ExecuteAsync(null);
        await ((IAsyncRelayCommand)viewModel.OpenDownloadFolderCommand).ExecuteAsync(null);
        viewModel.AskWhereToSaveDownloads = true;

        Assert.Equal(@"D:\RelayCove", viewModel.DownloadFolderPath);
        Assert.Equal(1, save.ChooseFolderCalls);
        Assert.Equal(1, save.OpenFolderCalls);
        Assert.True(save.AskWhereToSave);
    }

    [Fact]
    public async Task DownloadCenter_WhenDownloadCompletes_PersistsOpensRevealsAndRemovesRecord()
    {
        var accountId = AccountId.Create(RealmEndpoint.Parse("https://zulip.example"), 7);
        var session = new FakeSession
        {
            Account = accountId,
            StateValue = new ClientState(connection: new ConnectionState(ConnectionStatus.Connected))
        };
        var history = new InMemoryDownloadHistoryStore();
        var save = new FakeFileSaveService();
        using var viewModel = CreateViewModel(
            session,
            realmMediaService: new FakeRealmMediaService
            {
                FileResult = new RealmMediaResult([1, 2, 3], "application/pdf")
            },
            fileSaveService: save,
            downloadHistoryStore: history);
        session.Publish();
        var attachment = new MessageAttachmentItem("file", "guide.pdf", "/user_uploads/guide.pdf");

        await ((IAsyncRelayCommand)viewModel.DownloadAttachmentCommand).ExecuteAsync(attachment);

        var item = Assert.Single(viewModel.RecentDownloads);
        Assert.Equal("guide.pdf", item.FileName);
        Assert.False(item.IsMissing);
        Assert.True(viewModel.HasUnseenCompletedDownloads);
        Assert.Single(history.Load(accountId));

        viewModel.ToggleDownloadCenterCommand.Execute(null);
        Assert.True(viewModel.IsDownloadCenterOpen);
        Assert.False(viewModel.HasUnseenCompletedDownloads);
        await ((IAsyncRelayCommand)viewModel.OpenRecentDownloadCommand).ExecuteAsync(item);
        Assert.Equal([item.FilePath], save.OpenedFiles);
        Assert.False(viewModel.IsDownloadCenterOpen);

        viewModel.ToggleDownloadCenterCommand.Execute(null);
        await ((IAsyncRelayCommand)viewModel.ShowRecentDownloadInFolderCommand).ExecuteAsync(item);
        Assert.Equal([item.FilePath], save.RevealedFiles);

        viewModel.RemoveRecentDownloadCommand.Execute(item);
        Assert.Empty(viewModel.RecentDownloads);
        Assert.Empty(history.Load(accountId));
    }

    [Fact]
    public void DownloadCenter_WhenAccountChanges_LoadsOnlyThatAccountsRecentFiles()
    {
        var realm = RealmEndpoint.Parse("https://zulip.example");
        var firstAccount = AccountId.Create(realm, 7);
        var secondAccount = AccountId.Create(realm, 8);
        var firstPath = @"C:\Downloads\RelayCove\first.pdf";
        var secondPath = @"C:\Downloads\RelayCove\second.pdf";
        var history = new InMemoryDownloadHistoryStore();
        history.Save(firstAccount, [new DownloadHistoryEntry(Guid.NewGuid(), "first.pdf", firstPath, 10, DateTimeOffset.Now)]);
        history.Save(secondAccount, [new DownloadHistoryEntry(Guid.NewGuid(), "second.pdf", secondPath, 20, DateTimeOffset.Now)]);
        var save = new FakeFileSaveService();
        save.ExistingFiles.UnionWith([firstPath, secondPath]);
        var session = new FakeSession
        {
            Account = firstAccount,
            StateValue = new ClientState(connection: new ConnectionState(ConnectionStatus.Connected))
        };
        using var viewModel = CreateViewModel(
            session,
            fileSaveService: save,
            downloadHistoryStore: history);

        Assert.Equal("first.pdf", Assert.Single(viewModel.RecentDownloads).FileName);
        Assert.False(viewModel.HasUnseenCompletedDownloads);
        Assert.False(viewModel.HasDownloadButtonAttention);

        session.Account = secondAccount;
        session.Publish();

        Assert.Equal("second.pdf", Assert.Single(viewModel.RecentDownloads).FileName);
        Assert.False(viewModel.HasUnseenCompletedDownloads);
        Assert.False(viewModel.HasDownloadButtonAttention);
        viewModel.ClearDownloadHistoryCommand.Execute(null);
        Assert.Empty(history.Load(secondAccount));
        Assert.Single(history.Load(firstAccount));
    }

    [Fact]
    public async Task DownloadCenter_WhenRecordedFileIsMissing_MarksItemWithoutRemovingHistory()
    {
        var accountId = AccountId.Create(RealmEndpoint.Parse("https://zulip.example"), 7);
        var path = @"C:\Downloads\RelayCove\missing.pdf";
        var history = new InMemoryDownloadHistoryStore();
        history.Save(accountId, [new DownloadHistoryEntry(Guid.NewGuid(), "missing.pdf", path, 10, DateTimeOffset.Now)]);
        var session = new FakeSession { Account = accountId };
        using var viewModel = CreateViewModel(
            session,
            fileSaveService: new FakeFileSaveService(),
            downloadHistoryStore: history);
        var item = Assert.Single(viewModel.RecentDownloads);

        await ((IAsyncRelayCommand)viewModel.OpenRecentDownloadCommand).ExecuteAsync(item);

        Assert.True(item.IsMissing);
        Assert.Single(history.Load(accountId));
    }

    [Fact]
    public void DownloadCenter_WhenHistoryIsLarge_ShowsOnlyNewestTwentyEntries()
    {
        var accountId = AccountId.Create(RealmEndpoint.Parse("https://zulip.example"), 7);
        var history = new InMemoryDownloadHistoryStore();
        var now = DateTimeOffset.Now;
        history.Save(accountId, Enumerable.Range(0, 25)
            .Select(index => new DownloadHistoryEntry(
                Guid.NewGuid(),
                $"file-{index}.bin",
                $@"C:\Downloads\RelayCove\file-{index}.bin",
                index,
                now.AddMinutes(index)))
            .ToArray());
        var session = new FakeSession { Account = accountId };

        using var viewModel = CreateViewModel(
            session,
            fileSaveService: new FakeFileSaveService(),
            downloadHistoryStore: history);

        Assert.Equal(20, viewModel.RecentDownloads.Count);
        Assert.Equal("file-24.bin", viewModel.RecentDownloads[0].FileName);
    }

    [Fact]
    public async Task DownloadAttachmentCommand_WhenStartedFromImageMenu_ClosesMenuBeforeSaving()
    {
        var media = new FakeRealmMediaService
        {
            FileResult = new RealmMediaResult([1, 2, 3], "image/png")
        };
        using var viewModel = CreateViewModel(
            new FakeSession(),
            realmMediaService: media,
            fileSaveService: new FakeFileSaveService());
        var message = new MessageItem("message-1", 1, 7, "Ada", "image", "10:00");
        var image = new MessageAttachmentItem(
            "image",
            "preview.png",
            "https://chat.example.test/user_uploads/preview.png");
        viewModel.OpenImageAttachmentMenuAtCommand.Execute(
            new ImageAttachmentMenuRequest(message, image, 560d, 320d));

        await ((IAsyncRelayCommand)viewModel.DownloadAttachmentCommand).ExecuteAsync(image);

        Assert.False(viewModel.IsMessageMenuOpen);
        Assert.Null(viewModel.ActiveMessageAttachment);
        Assert.Equal(1, media.FileCalls);
    }

    [Fact]
    public async Task DownloadAttachmentCommand_WhenStartedFromImageViewer_ClosesViewerForGlobalProgress()
    {
        var image = new MessageAttachmentItem("image", "preview.png", "/user_uploads/preview.png");
        using var viewModel = CreateViewModel(
            new FakeSession(),
            realmMediaService: new FakeRealmMediaService(),
            fileSaveService: new FakeFileSaveService());
        viewModel.OpenImageViewerCommand.Execute(image);

        await ((IAsyncRelayCommand)viewModel.DownloadAttachmentCommand).ExecuteAsync(image);

        Assert.False(viewModel.IsImageViewerOpen);
        Assert.Null(viewModel.ActiveImageAttachment);
        Assert.True(viewModel.IsMediaDownloadStatusVisible);
    }

    [Fact]
    public async Task ToggleDetails_WhenChannelSelected_LoadsAuthoritativeNameAnnouncementAndAllMembers()
    {
        var selected = new ChannelTopic(4, string.Empty);
        var session = new FakeSession
        {
            Account = AccountId.Create(RealmEndpoint.Parse("https://zulip.example"), 7),
            CurrentUserId = 7,
            Selected = selected,
            StateValue = new ClientState(
                subscriptions: new Dictionary<long, Subscription> { [4] = PrivateGroupSubscription() },
                connection: new ConnectionState(ConnectionStatus.Connected)),
            LoadChannelDetailsAction = (channelId, _) => Task.FromResult(
                PrivateGroupDetails(channelId, "product", "本周五发布", 7)),
            ChannelMemberIdsAction = (_, _) => Task.FromResult<IReadOnlyList<long>>([8, 7]),
            RealmUsersAction = _ => Task.FromResult<IReadOnlyList<UserProfile>>([
                new UserProfile(7, "Ada", avatarUrl: "https://zulip.example/avatar/7"),
                new UserProfile(8, "Bea", avatarUrl: "https://zulip.example/avatar/8")
            ])
        };
        using var viewModel = CreateViewModel(session);

        Assert.False(viewModel.IsDetailsOpen);
        await ((IAsyncRelayCommand)viewModel.ToggleDetailsCommand).ExecuteAsync(null);

        Assert.True(viewModel.IsDetailsOpen);
        Assert.True(viewModel.ShowChannelDetails);
        Assert.False(viewModel.ShowDirectMessageSettings);
        Assert.Equal("product", viewModel.DetailsChannelName);
        Assert.Equal("本周五发布", viewModel.DetailsChannelAnnouncement);
        Assert.Equal(["Ada", "Bea"], viewModel.DetailsMembers.Select(member => member.Name));
        Assert.Equal("2 位成员", viewModel.DetailsMemberCountLabel);
        Assert.True(viewModel.IsCurrentUserPrivateGroupOwner);
    }

    [Fact]
    public async Task DirectMessageSettings_WhenChanged_PersistLocallyAndPinNavigationItem()
    {
        var accountId = AccountId.Create(RealmEndpoint.Parse("https://zulip.example"), 7);
        var selected = new DirectMessage([8]);
        var other = new DirectMessage([9]);
        var preferences = new InMemoryConversationPreferencesStore();
        var session = new FakeSession
        {
            Account = accountId,
            CurrentUserId = 7,
            Selected = selected,
            Recent = [other, selected],
            StateValue = new ClientState(users: new Dictionary<long, UserProfile>
            {
                [8] = new UserProfile(8, "Bea", avatarUrl: "https://zulip.example/avatar/8"),
                [9] = new UserProfile(9, "Cy")
            })
        };
        using var viewModel = CreateViewModel(session, conversationPreferencesStore: preferences);

        await ((IAsyncRelayCommand)viewModel.ToggleDetailsCommand).ExecuteAsync(null);
        viewModel.ToggleDirectMessageMutedCommand.Execute(null);
        viewModel.ToggleDirectMessagePinnedCommand.Execute(null);

        Assert.True(viewModel.ShowDirectMessageSettings);
        Assert.False(viewModel.ShowChannelDetails);
        Assert.Equal("https://zulip.example/avatar/8", viewModel.DetailsAvatarUrl);
        Assert.True(viewModel.IsSelectedDirectMessageMuted);
        Assert.True(viewModel.IsSelectedDirectMessagePinned);
        Assert.Equal(selected.CanonicalKey, viewModel.DirectMessages.First().Conversation.CanonicalKey);
        Assert.True(viewModel.DirectMessages.First().IsMuted);
        Assert.True(preferences.Get(accountId, selected.CanonicalKey).IsPinned);
    }

    [Fact]
    public async Task ClearConversationCache_WhenConfirmed_ClearsOnlySelectedCanonicalConversation()
    {
        var selected = new ChannelTopic(4, string.Empty);
        ConversationKey? cleared = null;
        var session = new FakeSession
        {
            Selected = selected,
            StateValue = new ClientState(
                subscriptions: new Dictionary<long, Subscription> { [4] = PrivateGroupSubscription() },
                connection: new ConnectionState(RelayCove.Core.ConnectionStatus.Connected)),
            ClearConversationCacheAction = (conversation, _) =>
            {
                cleared = conversation;
                return Task.CompletedTask;
            }
        };
        using var viewModel = CreateViewModel(session);

        viewModel.RequestClearConversationCacheCommand.Execute(null);
        await ((IAsyncRelayCommand)viewModel.ConfirmClearConversationCacheCommand).ExecuteAsync(null);

        Assert.Equal(selected, cleared);
        Assert.False(viewModel.ClearConversationCacheConfirmationVisible);
        Assert.Contains("当前账号下此群聊", viewModel.ClearConversationCacheDescription);
        Assert.Contains("不删除服务器消息", viewModel.ClearConversationCacheDescription);
    }

    [Fact]
    public async Task ToggleDetails_WhenConversationIsNotOneToOneOrChannel_DoesNotOpenEmptySettings()
    {
        var groupDirectMessage = new DirectMessage([8, 9]);
        var session = new FakeSession
        {
            Selected = groupDirectMessage,
            StateValue = new ClientState(users: new Dictionary<long, UserProfile>
            {
                [8] = new UserProfile(8, "Bea"),
                [9] = new UserProfile(9, "Cy")
            })
        };
        using var viewModel = CreateViewModel(session);

        await ((IAsyncRelayCommand)viewModel.ToggleDetailsCommand).ExecuteAsync(null);

        Assert.False(viewModel.CanOpenConversationSettings);
        Assert.False(viewModel.IsDetailsOpen);
        Assert.False(viewModel.ShowDirectMessageSettings);
        Assert.False(viewModel.ShowChannelDetails);
    }

    [Fact]
    public async Task ToggleDetails_WhenChannelMemberMappingIsIncomplete_FailsClosedWithoutStaleAnnouncement()
    {
        var selected = new ChannelTopic(4, string.Empty);
        var session = new FakeSession
        {
            Account = AccountId.Create(RealmEndpoint.Parse("https://zulip.example"), 7),
            Selected = selected,
            StateValue = new ClientState(subscriptions: new Dictionary<long, Subscription>
            {
                [4] = PrivateGroupSubscription()
            }),
            LoadChannelDetailsAction = (channelId, _) => Task.FromResult(
                PrivateGroupDetails(channelId, "product", "本周五发布", 7)),
            ChannelMemberIdsAction = (_, _) => Task.FromResult<IReadOnlyList<long>>([7, 8]),
            RealmUsersAction = _ => Task.FromResult<IReadOnlyList<UserProfile>>([new UserProfile(7, "Ada")])
        };
        using var viewModel = CreateViewModel(session);

        await ((IAsyncRelayCommand)viewModel.ToggleDetailsCommand).ExecuteAsync(null);

        Assert.True(viewModel.HasDetailsLoadError);
        Assert.Empty(viewModel.DetailsMembers);
        Assert.Equal("本周五发布", viewModel.DetailsChannelAnnouncement);
        Assert.True(viewModel.IsPrivateGroupAuthorityLoaded);
    }

    [Fact]
    public async Task ToggleDetails_WhenMemberRosterFails_AllowsConfirmedOrdinaryMemberToExit()
    {
        var selected = new ChannelTopic(4, string.Empty);
        var session = new FakeSession
        {
            Account = AccountId.Create(RealmEndpoint.Parse("https://zulip.example"), 7),
            CurrentUserId = 7,
            Selected = selected,
            StateValue = new ClientState(
                subscriptions: new Dictionary<long, Subscription> { [4] = PrivateGroupSubscription() },
                connection: new ConnectionState(RelayCove.Core.ConnectionStatus.Connected)),
            LoadChannelDetailsAction = (channelId, _) => Task.FromResult(
                PrivateGroupDetails(channelId, "product", "公告", 8)),
            ChannelMemberIdsAction = (_, _) => Task.FromException<IReadOnlyList<long>>(
                new InvalidOperationException("temporary roster failure"))
        };
        using var viewModel = CreateViewModel(session);

        await ((IAsyncRelayCommand)viewModel.ToggleDetailsCommand).ExecuteAsync(null);

        Assert.True(viewModel.IsPrivateGroupAuthorityLoaded);
        Assert.False(viewModel.IsCurrentUserPrivateGroupOwner);
        Assert.True(viewModel.HasDetailsLoadError);
        Assert.True(viewModel.CanExitPrivateGroup);
    }

    [Fact]
    public async Task ToggleDetails_WhenAuthorityCannotBeLoaded_DisablesExit()
    {
        var selected = new ChannelTopic(4, string.Empty);
        var session = new FakeSession
        {
            Account = AccountId.Create(RealmEndpoint.Parse("https://zulip.example"), 7),
            CurrentUserId = 7,
            Selected = selected,
            StateValue = new ClientState(
                subscriptions: new Dictionary<long, Subscription> { [4] = PrivateGroupSubscription() },
                connection: new ConnectionState(RelayCove.Core.ConnectionStatus.Connected)),
            LoadChannelDetailsAction = (_, _) => Task.FromException<ChannelDetails>(
                new InvalidOperationException("temporary details failure"))
        };
        using var viewModel = CreateViewModel(session);

        await ((IAsyncRelayCommand)viewModel.ToggleDetailsCommand).ExecuteAsync(null);

        Assert.False(viewModel.IsPrivateGroupAuthorityLoaded);
        Assert.True(viewModel.HasDetailsLoadError);
        Assert.False(viewModel.CanExitPrivateGroup);
    }

    [Fact]
    public async Task ChannelSettingsLoad_WhenConversationChanges_DoesNotReopenOrProjectLateMembers()
    {
        var channel = new ChannelTopic(4, string.Empty);
        var directMessage = new DirectMessage([8]);
        var detailsGate = new TaskCompletionSource<ChannelDetails>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new FakeSession
        {
            Account = AccountId.Create(RealmEndpoint.Parse("https://zulip.example"), 7),
            CurrentUserId = 7,
            Selected = channel,
            StateValue = new ClientState(
                subscriptions: new Dictionary<long, Subscription> { [4] = PrivateGroupSubscription() },
                users: new Dictionary<long, UserProfile> { [8] = new UserProfile(8, "Bea") }),
            LoadChannelDetailsAction = (_, _) => detailsGate.Task,
            ChannelMemberIdsAction = (_, _) => Task.FromResult<IReadOnlyList<long>>([7, 8]),
            RealmUsersAction = _ => Task.FromResult<IReadOnlyList<UserProfile>>([
                new UserProfile(7, "Ada"),
                new UserProfile(8, "Bea")
            ])
        };
        using var viewModel = CreateViewModel(session);

        var open = ((IAsyncRelayCommand)viewModel.ToggleDetailsCommand).ExecuteAsync(null);
        await WaitUntilAsync(() => viewModel.IsDetailsLoading);
        viewModel.ActivateDirectMessage(new NavigationItem(directMessage, "Bea"));
        await WaitUntilAsync(() => session.SelectedConversation == directMessage);
        detailsGate.SetResult(PrivateGroupDetails(4, "late-product", "late-announcement", 7));
        await open;

        Assert.False(viewModel.IsDetailsOpen);
        Assert.True(viewModel.ShowDirectMessageSettings);
        Assert.Empty(viewModel.DetailsMembers);
        Assert.NotEqual("late-product", viewModel.DetailsChannelName);
    }

    private static ShellViewModel CreateViewModel(
        IClientSession session,
        FakeLastRealmStore? lastRealmStore = null,
        FakeAppearanceService? appearanceService = null,
        FakeUiPreferencesService? uiPreferencesService = null,
        FakePlatformInteractionService? platformInteractions = null,
        FakeFileSelectionService? fileSelectionService = null,
        FakeRealmMediaService? realmMediaService = null,
        FakeFileSaveService? fileSaveService = null,
        IConversationPreferencesStore? conversationPreferencesStore = null,
        INotificationPreferencesService? notificationPreferencesService = null,
        IAppNotificationService? appNotificationService = null,
        IWindowShellAdapter? windowShellAdapter = null,
        IDownloadHistoryStore? downloadHistoryStore = null) =>
        new(
            session,
            lastRealmStore ?? new FakeLastRealmStore(),
            new InlineDispatcher(),
            appearanceService ?? new FakeAppearanceService(),
            uiPreferencesService ?? new FakeUiPreferencesService(),
            platformInteractions ?? new FakePlatformInteractionService(),
            fileSelectionService ?? new FakeFileSelectionService(),
            realmMediaService ?? new FakeRealmMediaService(),
            fileSaveService ?? new FakeFileSaveService(),
            conversationPreferencesStore,
            notificationPreferencesService,
            appNotificationService,
            windowShellAdapter,
            null,
            downloadHistoryStore);

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

    private sealed class FakeNotificationPreferencesService : INotificationPreferencesService
    {
        public NotificationPreferences Current { get; set; } = new();
        public List<NotificationPreferences> Saved { get; } = [];

        public void Save(NotificationPreferences preferences)
        {
            Current = preferences;
            Saved.Add(preferences);
        }
    }

    private sealed class FakeAppNotificationService : IAppNotificationService
    {
        public event EventHandler? StateChanged;
        public event EventHandler<AppNotificationActivatedEventArgs>? NotificationActivated;
        public bool IsSystemNotificationSupported { get; set; } = true;
        public string SystemNotificationStatus { get; set; } = "ready";
        public string TaskbarBadgeStatus { get; set; } = "badge ready";
        public List<AppMessageNotification> Notifications { get; } = [];
        public List<AppMessageNotification> TrayPreviews { get; } = [];
        public List<(int Count, bool IsTruncated)> BadgeUpdates { get; } = [];
        public List<(int Count, bool IsTruncated)> TrayUnreadUpdates { get; } = [];
        public int FlashCalls { get; private set; }
        public int StopFlashCalls { get; private set; }
        public int StopTrayFlashCalls { get; private set; }

        public void Attach(Window window) { }
        public void ShowMessageNotification(AppMessageNotification notification) => Notifications.Add(notification);
        public void UpdateTrayPreview(AppMessageNotification notification) => TrayPreviews.Add(notification);
        public void UpdateTrayUnread(int count, bool isTruncated) => TrayUnreadUpdates.Add((count, isTruncated));
        public void UpdateUnreadBadge(int count, bool isTruncated) => BadgeUpdates.Add((count, isTruncated));
        public void FlashTaskbar() => FlashCalls++;
        public void StopTaskbarFlash() => StopFlashCalls++;
        public void StopTrayFlash() => StopTrayFlashCalls++;
        public void Dispose() { }
        public void PublishStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);
        public void Activate(string conversationKey) =>
            NotificationActivated?.Invoke(this, new AppNotificationActivatedEventArgs(conversationKey));
    }

    private sealed class FakeWindowShellAdapter : IWindowShellAdapter
    {
        public event EventHandler? StateChanged;
        public bool IsPinned { get; set; }
        public bool IsForeground { get; set; }
        public void Attach(Window window) { }
        public void TogglePinned() => IsPinned = !IsPinned;
        public void RequestExit() { }
        public void PublishStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class FakeRealmMediaService : IRealmMediaService
    {
        public RealmMediaResult FileResult { get; set; } = new([1, 2, 3], "application/octet-stream");
        public Exception? DownloadFailure { get; set; }
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

        public async Task<RealmMediaDownloadResult> DownloadFileAsync(
            string sourceUrl,
            Stream destination,
            IProgress<RealmMediaTransferProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            FileCalls++;
            if (DownloadFailure is not null) throw DownloadFailure;
            progress?.Report(new RealmMediaTransferProgress(0, FileResult.Content.LongLength));
            await destination.WriteAsync(FileResult.Content, cancellationToken);
            progress?.Report(new RealmMediaTransferProgress(FileResult.Content.LongLength, FileResult.Content.LongLength));
            return new RealmMediaDownloadResult(FileResult.Content.LongLength, FileResult.ContentType);
        }
    }

    private sealed class FakeFileSaveService : IFileSaveService
    {
        public bool Result { get; set; } = true;
        public string? FileName { get; private set; }
        public byte[]? Content { get; private set; }
        public string DownloadFolderPath { get; set; } = @"C:\Downloads\RelayCove";
        public bool AskWhereToSave { get; set; }
        public int ChooseFolderCalls { get; private set; }
        public int OpenFolderCalls { get; private set; }
        public HashSet<string> ExistingFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> OpenedFiles { get; } = [];
        public List<string> RevealedFiles { get; } = [];

        public Task<bool> ChooseDownloadFolderAsync(CancellationToken cancellationToken = default)
        {
            ChooseFolderCalls++;
            DownloadFolderPath = @"D:\RelayCove";
            return Task.FromResult(Result);
        }

        public Task OpenDownloadFolderAsync(CancellationToken cancellationToken = default)
        {
            OpenFolderCalls++;
            return Task.CompletedTask;
        }

        public bool DownloadedFileExists(string filePath) => ExistingFiles.Contains(filePath);

        public Task OpenDownloadedFileAsync(string filePath, CancellationToken cancellationToken = default)
        {
            if (!DownloadedFileExists(filePath)) throw new FileNotFoundException();
            OpenedFiles.Add(filePath);
            return Task.CompletedTask;
        }

        public Task ShowDownloadedFileInFolderAsync(string filePath, CancellationToken cancellationToken = default)
        {
            if (!DownloadedFileExists(filePath)) throw new FileNotFoundException();
            RevealedFiles.Add(filePath);
            return Task.CompletedTask;
        }

        public async Task<DownloadSaveResult> SaveDownloadAsync(
            string fileName,
            Func<Stream, CancellationToken, Task> writeAsync,
            CancellationToken cancellationToken = default)
        {
            FileName = fileName;
            if (!Result) return DownloadSaveResult.Cancelled;
            await using var destination = new MemoryStream();
            await writeAsync(destination, cancellationToken);
            Content = destination.ToArray();
            var path = Path.Combine(DownloadFolderPath, fileName);
            ExistingFiles.Add(path);
            return new DownloadSaveResult(true, path);
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

    [Fact]
    public async Task TopicMenu_WhenTargeted_UsesCapturedTopicWithoutActivatingConversation()
    {
        ChannelTopic? readTarget = null;
        var state = new ClientState(
            subscriptions: new Dictionary<long, Subscription> { [4] = PrivateGroupSubscription(4, "engineering") },
            connection: new ConnectionState(ConnectionStatus.Connected));
        var session = new FakeSession
        {
            CurrentUserId = 7,
            StateValue = state,
            Selected = new ChannelTopic(4, "current"),
            LoadTopicsAction = (_, _) => Task.FromResult<IReadOnlyList<TopicSummary>>([new TopicSummary(4, "review", 20, TopicVisibilityPolicy.Followed)]),
            MarkTopicReadAction = (topic, _) => { readTarget = topic; return Task.CompletedTask; }
        };
        using var viewModel = CreateViewModel(session);
        session.Publish();
        viewModel.ActivateChannel(viewModel.Channels.Single());
        await WaitUntilAsync(() => viewModel.Topics.Count == 1);
        var topic = viewModel.Topics.Single();
        viewModel.ActivateTopic(topic);
        await WaitUntilAsync(() => session.SelectedConversation is ChannelTopic selected && selected.CanonicalKey == topic.CanonicalKey);
        Assert.False(viewModel.HasSelectedTopic);
        var selectedBeforeMenu = session.SelectedConversation;
        var rowFocusRequestBeforeMenu = viewModel.TopicMenuFocusRequest;
        viewModel.OpenTopicMenuAtCommand.Execute(new TopicMenuRequest(topic, 20, 20));

        await ((IAsyncRelayCommand)viewModel.MarkActiveTopicReadCommand).ExecuteAsync(null);

        Assert.Equal(new ChannelTopic(4, "review"), readTarget);
        Assert.Equal(selectedBeforeMenu, session.SelectedConversation);
        Assert.False(viewModel.IsTopicMenuOpen);
        Assert.Equal(rowFocusRequestBeforeMenu + 1, viewModel.TopicMenuFocusRequest);
    }

    [Fact]
    public async Task TopicDelete_WhenPartialResult_DoesNotRetryAndShowsStatus()
    {
        var calls = 0;
        var state = new ClientState(
            subscriptions: new Dictionary<long, Subscription> { [4] = PrivateGroupSubscription(4, "engineering") },
            connection: new ConnectionState(ConnectionStatus.Connected));
        var session = new FakeSession
        {
            IsOrganizationAdministrator = true,
            StateValue = state,
            LoadTopicsAction = (_, _) => Task.FromResult<IReadOnlyList<TopicSummary>>([new TopicSummary(4, "review", 20)]),
            DeleteTopicAction = (_, _) => { calls++; return Task.FromResult(new TopicDeleteResult(false)); }
        };
        using var viewModel = CreateViewModel(session);
        session.Publish();
        viewModel.ActivateChannel(viewModel.Channels.Single());
        await WaitUntilAsync(() => viewModel.Topics.Count == 1);
        viewModel.OpenTopicMenuAtCommand.Execute(new TopicMenuRequest(viewModel.Topics.Single(), 20, 20));
        viewModel.RequestTopicDeleteCommand.Execute(null);

        await ((IAsyncRelayCommand)viewModel.ConfirmTopicDeleteCommand).ExecuteAsync(null);

        Assert.Equal(1, calls);
        Assert.Contains("部分删除", viewModel.TopicActionStatus);
    }

    [Fact]
    public async Task TopicMove_WhenSameSourceAndDestination_DisablesWriteThenUsesExplicitDestination()
    {
        (ChannelTopic Source, ChannelTopic Destination)? moved = null;
        var state = new ClientState(
            subscriptions: new Dictionary<long, Subscription> { [4] = new Subscription(4, "engineering"), [5] = new Subscription(5, "design") },
            connection: new ConnectionState(ConnectionStatus.Connected));
        var session = new FakeSession
        {
            IsOrganizationAdministrator = true,
            StateValue = state,
            LoadTopicsAction = (_, _) => Task.FromResult<IReadOnlyList<TopicSummary>>([new TopicSummary(4, "review", 20)]),
            MoveTopicAction = (source, destination, _) => { moved = (source, destination); return Task.CompletedTask; }
        };
        using var viewModel = CreateViewModel(session);
        session.Publish();
        viewModel.ActivateChannel(viewModel.Channels.Single(channel => channel.ChannelId == 4));
        await WaitUntilAsync(() => viewModel.Topics.Count == 1);
        viewModel.OpenTopicMenuAtCommand.Execute(new TopicMenuRequest(viewModel.Topics.Single(), 20, 20));
        viewModel.OpenTopicMoveDialogCommand.Execute(null);

        Assert.False(viewModel.CanConfirmTopicMove);
        viewModel.TopicMoveDestinationChannel = viewModel.Channels.Single(channel => channel.ChannelId == 5);
        viewModel.TopicMoveDestinationName = "implementation";
        await ((IAsyncRelayCommand)viewModel.ConfirmTopicMoveCommand).ExecuteAsync(null);

        Assert.Equal((new ChannelTopic(4, "review"), new ChannelTopic(5, "implementation")), moved);
    }

    [Fact]
    public async Task TopicPolicyAndResolution_WhenMenuTargeted_UseCapturedTarget()
    {
        (ChannelTopic Topic, TopicVisibilityPolicy Policy)? policy = null;
        (ChannelTopic Topic, bool Resolved)? resolution = null;
        var state = new ClientState(
            subscriptions: new Dictionary<long, Subscription> { [4] = new Subscription(4, "engineering") },
            connection: new ConnectionState(ConnectionStatus.Connected));
        var session = new FakeSession
        {
            CurrentUserId = 7,
            IsOrganizationAdministrator = true,
            StateValue = state,
            LoadTopicsAction = (_, _) => Task.FromResult<IReadOnlyList<TopicSummary>>([new TopicSummary(4, "review", 20)]),
            SetTopicVisibilityAction = (topic, visibility, _) => { policy = (topic, visibility); return Task.CompletedTask; },
            ResolveTopicAction = (topic, resolved, _) => { resolution = (topic, resolved); return Task.CompletedTask; }
        };
        using var viewModel = CreateViewModel(session);
        session.Publish();
        viewModel.ActivateChannel(viewModel.Channels.Single());
        await WaitUntilAsync(() => viewModel.Topics.Count == 1);
        var topic = viewModel.Topics.Single();
        var changed = new List<string?>();
        topic.PropertyChanged += (_, eventArgs) => changed.Add(eventArgs.PropertyName);
        viewModel.OpenTopicMenuAtCommand.Execute(new TopicMenuRequest(topic, 20, 20));
        await ((IAsyncRelayCommand)viewModel.SetActiveTopicVisibilityPolicyCommand).ExecuteAsync("Followed");
        Assert.Equal((new ChannelTopic(4, "review"), TopicVisibilityPolicy.Followed), policy);
        Assert.Equal("★", topic.VisibilityGlyph);
        Assert.Contains(nameof(TopicItem.VisibilityGlyph), changed);

        viewModel.OpenTopicMenuAtCommand.Execute(new TopicMenuRequest(topic, 20, 20));
        viewModel.RequestActiveTopicResolutionCommand.Execute(null);
        await ((IAsyncRelayCommand)viewModel.ConfirmTopicResolutionCommand).ExecuteAsync(null);
        Assert.Equal((new ChannelTopic(4, "review"), true), resolution);
    }

    [Theory]
    [InlineData(null, true, true, false, false)]
    [InlineData(20, false, true, true, false)]
    [InlineData(20, true, true, true, true)]
    [InlineData(20, true, false, true, false)]
    public async Task TopicMenu_WhenMessagesOrAuthorityAreUnavailable_FailsClosed(
        int? maxMessageId,
        bool isAdministrator,
        bool isSubscriptionActive,
        bool expectedHasMessages,
        bool expectedCanAdminister)
    {
        var session = new FakeSession
        {
            CurrentUserId = 7,
            IsOrganizationAdministrator = isAdministrator,
            StateValue = new ClientState(
                subscriptions: new Dictionary<long, Subscription> { [4] = new Subscription(4, "engineering") },
                connection: new ConnectionState(ConnectionStatus.Connected)),
            LoadTopicsAction = (_, _) => Task.FromResult<IReadOnlyList<TopicSummary>>([
                new TopicSummary(4, "review", maxMessageId is null ? null : (long?)maxMessageId.Value)
            ])
        };
        using var viewModel = CreateViewModel(session);
        session.Publish();
        viewModel.ActivateChannel(viewModel.Channels.Single());
        await WaitUntilAsync(() => viewModel.Topics.Count == 1);
        viewModel.OpenTopicMenuAtCommand.Execute(new TopicMenuRequest(viewModel.Topics.Single(), 20, 20));
        if (!isSubscriptionActive)
        {
            session.StateValue = session.StateValue with
            {
                Subscriptions = new Dictionary<long, Subscription> { [4] = new Subscription(4, "engineering", isActive: false) }
            };
            session.Publish();
        }

        Assert.Equal(expectedHasMessages, viewModel.ActiveTopicHasMessages);
        Assert.Equal(!expectedHasMessages, viewModel.ActiveTopicIsEmpty);
        Assert.Equal(expectedCanAdminister, viewModel.CanAdministerActiveTopicOperations);
        Assert.Equal(expectedCanAdminister, viewModel.CanMoveActiveTopic);
        Assert.Equal(expectedCanAdminister, viewModel.CanDeleteActiveTopic);
        Assert.Equal(expectedHasMessages && isSubscriptionActive, viewModel.CanMarkActiveTopicRead);
        Assert.Equal(isSubscriptionActive, viewModel.CanSetActiveTopicVisibility);
    }

    [Fact]
    public async Task TopicProjection_WhenRefreshed_PreservesStableRowAndTransientMenuState()
    {
        IReadOnlyList<TopicSummary> loaded = [new TopicSummary(4, "review", 20, TopicVisibilityPolicy.Muted)];
        var session = new FakeSession
        {
            StateValue = new ClientState(subscriptions: new Dictionary<long, Subscription> { [4] = new Subscription(4, "engineering") }, connection: new ConnectionState(ConnectionStatus.Connected)),
            LoadTopicsAction = (_, _) => Task.FromResult(loaded)
        };
        using var viewModel = CreateViewModel(session);
        session.Publish();
        var channel = viewModel.Channels.Single();
        viewModel.ActivateChannel(channel);
        await WaitUntilAsync(() => viewModel.Topics.Count == 1);
        var original = viewModel.Topics.Single();
        original.IsPointerOver = true;
        loaded = [new TopicSummary(4, "review", 21, TopicVisibilityPolicy.Followed)];
        viewModel.ActivateChannel(channel);
        viewModel.ActivateChannel(channel);
        await WaitUntilAsync(() => viewModel.Topics.Single().MaxMessageId == 21);

        Assert.Same(original, viewModel.Topics.Single());
        Assert.True(original.IsPointerOver);
        Assert.Equal(TopicVisibilityPolicy.Followed, original.VisibilityPolicy);
    }

    private static Subscription PrivateGroupSubscription(long channelId = 4, string name = "engineering") =>
        new(channelId, name, isPrivate: true, topicsPolicy: ChannelTopicsPolicy.EmptyTopicOnly, isWebPublic: false);

    private static ChannelDetails PrivateGroupDetails(
        long channelId,
        string name,
        string description,
        long ownerId)
    {
        var owner = PrivateGroupPolicy.OwnerGroup(ownerId);
        return new ChannelDetails(
            channelId,
            name,
            description,
            false,
            true,
            false,
            null,
            null,
            null,
            ownerId,
            null,
            owner,
            owner,
            HistoryPublicToSubscribers: true,
            TopicsPolicy: ChannelTopicsPolicy.EmptyTopicOnly,
            CanRemoveSubscribersGroup: owner);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Condition was not reached.");
            await Task.Delay(10);
        }
    }

    private sealed class FakeSession : IClientSession, IRealtimeMessageObserver
    {
        public ClientState StateValue { get; set; } = ClientState.Empty;
        public ConversationKey? Selected { get; set; }
        public IReadOnlyList<ConversationKey> Recent { get; set; } = [];
        public AccountId? Account { get; set; }
        public Func<string, string, string, CancellationToken, Task>? LoginAction { get; set; }
        public Func<CancellationToken, Task>? LogoutAction { get; set; }
        public Func<ConversationKey, CancellationToken, Task>? SelectAction { get; set; }
        public Func<string, CancellationToken, Task>? SendAction { get; set; }
        public Func<AttachmentUpload, CancellationToken, Task<UploadedAttachment>>? UploadAction { get; set; }
        public Func<long, CancellationToken, Task>? UnsubscribeChannelAction { get; set; }
        public Func<long, CancellationToken, Task<IReadOnlyList<TopicSummary>>>? LoadTopicsAction { get; set; }
        public Func<CancellationToken, Task>? LoadOlderAction { get; set; }
        public Func<long?, int, CancellationToken, Task<MessageQueryPage>>? SavedMessagesAction { get; set; }
        public Func<string, long?, int, CancellationToken, Task<MessageQueryPage>>? SearchMessagesAction { get; set; }
        public Func<string, long?, int, MessageSearchFilter, CancellationToken, Task<MessageQueryPage>>? SearchMessagesWithFilterAction { get; set; }
        public Func<ConversationKey, long, CancellationToken, Task>? OpenMessageAction { get; set; }
        public Func<CancellationToken, Task<IReadOnlyList<ChannelSummary>>>? AvailableChannelsAction { get; set; }
        public Func<UserPresenceStatus, CancellationToken, Task>? SetOwnPresenceAction { get; set; }
        public Func<UserStatusContent, CancellationToken, Task>? SetOwnUserStatusAction { get; set; }
        public Func<long, CancellationToken, Task<ChannelDetails>>? LoadChannelDetailsAction { get; set; }
        public Func<long, CancellationToken, Task<IReadOnlyList<long>>>? ChannelMemberIdsAction { get; set; }
        public Func<CancellationToken, Task<IReadOnlyList<UserProfile>>>? RealmUsersAction { get; set; }
        public Func<PrivateGroupCreateOptions, CancellationToken, Task<PrivateGroupCreated>>? CreatePrivateGroupAction { get; set; }
        public Func<long, long, CancellationToken, Task<PrivateGroupTransferResult>>? TransferPrivateGroupAction { get; set; }
        public Func<long, CancellationToken, Task<PrivateGroupDissolveResult>>? DissolvePrivateGroupAction { get; set; }
        public Func<long, IReadOnlyList<long>, bool, CancellationToken, Task>? AddChannelMembersAction { get; set; }
        public Func<long, IReadOnlyList<long>, CancellationToken, Task>? RemoveChannelMembersAction { get; set; }
        public Func<long, string?, string?, long?, CancellationToken, Task>? UpdateChannelAction { get; set; }
        public Func<ConversationKey, CancellationToken, Task>? ClearConversationCacheAction { get; set; }
        public Func<ConversationKey, CancellationToken, Task>? MarkDisplayedReadAction { get; set; }
        public Func<ChannelTopic, TopicVisibilityPolicy, CancellationToken, Task>? SetTopicVisibilityAction { get; set; }
        public Func<ChannelTopic, CancellationToken, Task>? MarkTopicReadAction { get; set; }
        public Func<ChannelTopic, ChannelTopic, CancellationToken, Task>? MoveTopicAction { get; set; }
        public Func<ChannelTopic, bool, CancellationToken, Task>? ResolveTopicAction { get; set; }
        public Func<ChannelTopic, CancellationToken, Task<TopicDeleteResult>>? DeleteTopicAction { get; set; }
        public int LoginCalls { get; private set; }
        public List<string> SentContents { get; } = [];
        public int UploadCalls { get; private set; }
        public int LoadOlderCalls { get; private set; }
        public List<ConversationKey> ExpectedMarkReadConversations { get; } = [];
        public List<(ConversationKey Conversation, long MessageId)> OpenedMessages { get; } = [];

        public AccountId? AccountId => Account;
        public RealmEndpoint? ActiveRealm { get; set; }
        public long? CurrentUserId { get; set; }
        public bool IsOrganizationAdministrator { get; set; }
        public bool CanCreatePrivateGroup { get; set; }
        public bool CanSetOwnPresenceValue { get; set; }
        public UserPresenceStatus? OwnPresenceStatusValue { get; set; }
        public bool CanSetOwnPresence => CanSetOwnPresenceValue;
        public UserPresenceStatus? OwnPresenceStatus => OwnPresenceStatusValue;
        public bool CanSetOwnUserStatusValue { get; set; }
        public UserStatusContent? OwnUserStatusValue { get; set; }
        public bool IsOwnUserStatusConfirmedValue { get; set; }
        public bool CanSetOwnUserStatus => CanSetOwnUserStatusValue;
        public UserStatusContent? OwnUserStatus => OwnUserStatusValue;
        public bool IsOwnUserStatusConfirmed => IsOwnUserStatusConfirmedValue;
        public long MaxFileUploadBytes { get; set; } = 10L * 1024 * 1024;
        public ClientState State => StateValue;
        public ConversationKey? SelectedConversation => Selected;
        public ConversationHistoryState HistoryState { get; set; } = ConversationHistoryState.Empty;
        public IReadOnlyList<ConversationKey> RecentDirectMessages => Recent;
        public event EventHandler<ClientStateChangedEventArgs>? StateChanged;
        public event EventHandler<RealtimeMessageReceivedEventArgs>? RealtimeMessageReceived;

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
            if (SelectAction is not null) return SelectAction(conversation, cancellationToken);
            Selected = conversation;
            HistoryState = new ConversationHistoryState(
                conversation,
                HistoryState.Generation + 1,
                false,
                true,
                false,
                StateValue.Messages.Values
                    .Where(message => message.Conversation == conversation)
                    .Select(message => (long?)message.Id)
                    .DefaultIfEmpty()
                    .Min(),
                null);
            return Task.CompletedTask;
        }

        public Task LoadOlderAsync(CancellationToken cancellationToken = default)
        {
            LoadOlderCalls++;
            return LoadOlderAction?.Invoke(cancellationToken) ?? Task.CompletedTask;
        }
        public Task<IReadOnlyList<TopicSummary>> LoadTopicsAsync(long channelId, CancellationToken cancellationToken = default) =>
            LoadTopicsAction?.Invoke(channelId, cancellationToken) ?? Task.FromResult<IReadOnlyList<TopicSummary>>([]);
        public Task SetTopicVisibilityPolicyAsync(ChannelTopic topic, TopicVisibilityPolicy policy, CancellationToken cancellationToken = default) => SetTopicVisibilityAction?.Invoke(topic, policy, cancellationToken) ?? Task.CompletedTask;
        public Task MarkTopicReadAsync(ChannelTopic topic, CancellationToken cancellationToken = default) => MarkTopicReadAction?.Invoke(topic, cancellationToken) ?? Task.CompletedTask;
        public Task MoveTopicAsync(ChannelTopic source, ChannelTopic destination, CancellationToken cancellationToken = default) => MoveTopicAction?.Invoke(source, destination, cancellationToken) ?? Task.CompletedTask;
        public Task SetTopicResolvedAsync(ChannelTopic topic, bool isResolved, CancellationToken cancellationToken = default) => ResolveTopicAction?.Invoke(topic, isResolved, cancellationToken) ?? Task.CompletedTask;
        public Task<TopicDeleteResult> DeleteTopicAsync(ChannelTopic topic, CancellationToken cancellationToken = default) => DeleteTopicAction?.Invoke(topic, cancellationToken) ?? Task.FromResult(new TopicDeleteResult(true));
        public Task<MessageQueryPage> LoadSavedMessagesAsync(long? beforeMessageId, int limit, CancellationToken cancellationToken = default) =>
            SavedMessagesAction?.Invoke(beforeMessageId, limit, cancellationToken) ?? Task.FromResult(new MessageQueryPage([], false, true, true));
        public Task<MessageQueryPage> SearchMessagesAsync(
            string query,
            long? beforeMessageId,
            int limit,
            CancellationToken cancellationToken = default,
            MessageSearchFilter filter = MessageSearchFilter.Messages) =>
            SearchMessagesWithFilterAction?.Invoke(query, beforeMessageId, limit, filter, cancellationToken) ??
            SearchMessagesAction?.Invoke(query, beforeMessageId, limit, cancellationToken) ??
            Task.FromResult(new MessageQueryPage([], false, true, true));
        public Task OpenMessageAsync(ConversationKey conversation, long messageId, CancellationToken cancellationToken = default)
        {
            OpenedMessages.Add((conversation, messageId));
            return OpenMessageAction?.Invoke(conversation, messageId, cancellationToken) ?? Task.CompletedTask;
        }
        public Task<IReadOnlyList<ChannelSummary>> GetAvailableChannelsAsync(CancellationToken cancellationToken = default) =>
            AvailableChannelsAction?.Invoke(cancellationToken) ?? Task.FromResult<IReadOnlyList<ChannelSummary>>([]);
        public Task SetOwnPresenceAsync(UserPresenceStatus status, CancellationToken cancellationToken = default) =>
            SetOwnPresenceAction?.Invoke(status, cancellationToken) ?? Task.CompletedTask;
        public Task SetOwnUserStatusAsync(UserStatusContent status, CancellationToken cancellationToken = default) =>
            SetOwnUserStatusAction?.Invoke(status, cancellationToken) ?? Task.CompletedTask;
        public Task<ChannelDetails> LoadChannelDetailsAsync(long channelId, CancellationToken cancellationToken = default) =>
            LoadChannelDetailsAction?.Invoke(channelId, cancellationToken) ?? Task.FromException<ChannelDetails>(new NotSupportedException());
        public Task<IReadOnlyList<long>> GetChannelMemberIdsAsync(long channelId, CancellationToken cancellationToken = default) =>
            ChannelMemberIdsAction?.Invoke(channelId, cancellationToken) ?? Task.FromResult<IReadOnlyList<long>>([]);
        public Task<IReadOnlyList<UserProfile>> GetRealmUsersAsync(CancellationToken cancellationToken = default) =>
            RealmUsersAction?.Invoke(cancellationToken) ?? Task.FromResult<IReadOnlyList<UserProfile>>([]);
        public Task<PrivateGroupCreated> CreatePrivateGroupAsync(PrivateGroupCreateOptions options, CancellationToken cancellationToken = default) =>
            CreatePrivateGroupAction?.Invoke(options, cancellationToken) ?? Task.FromException<PrivateGroupCreated>(new NotSupportedException());
        public Task<PrivateGroupTransferResult> TransferPrivateGroupOwnershipAsync(long channelId, long newOwnerId, CancellationToken cancellationToken = default) =>
            TransferPrivateGroupAction?.Invoke(channelId, newOwnerId, cancellationToken) ?? Task.FromException<PrivateGroupTransferResult>(new NotSupportedException());
        public Task<PrivateGroupDissolveResult> DissolvePrivateGroupAsync(long channelId, CancellationToken cancellationToken = default) =>
            DissolvePrivateGroupAction?.Invoke(channelId, cancellationToken) ?? Task.FromException<PrivateGroupDissolveResult>(new NotSupportedException());
        public Task AddChannelMembersAsync(long channelId, IReadOnlyList<long> principalIds, bool sendNewSubscriptionMessages, CancellationToken cancellationToken = default) =>
            AddChannelMembersAction?.Invoke(channelId, principalIds, sendNewSubscriptionMessages, cancellationToken) ?? Task.CompletedTask;
        public Task RemoveChannelMembersAsync(long channelId, IReadOnlyList<long> principalIds, CancellationToken cancellationToken = default) =>
            RemoveChannelMembersAction?.Invoke(channelId, principalIds, cancellationToken) ?? Task.CompletedTask;
        public Task UpdateChannelAsync(long channelId, string? name, string? description, long? folderId, bool clearFolder = false, CancellationToken cancellationToken = default) =>
            UpdateChannelAction?.Invoke(channelId, name, description, folderId, cancellationToken) ?? Task.CompletedTask;

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
        public Task MarkDisplayedReadAsync(ConversationKey expectedConversation, CancellationToken cancellationToken = default)
        {
            ExpectedMarkReadConversations.Add(expectedConversation);
            return MarkDisplayedReadAction?.Invoke(expectedConversation, cancellationToken) ?? Task.CompletedTask;
        }
        public Task ClearLocalCacheAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ClearConversationCacheAsync(ConversationKey expectedConversation, CancellationToken cancellationToken = default) =>
            ClearConversationCacheAction?.Invoke(expectedConversation, cancellationToken) ?? Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void Publish() => StateChanged?.Invoke(this, new ClientStateChangedEventArgs(StateValue));

        public void PublishRealtime(ChatMessage message) =>
            RealtimeMessageReceived?.Invoke(this, new RealtimeMessageReceivedEventArgs(message));
    }
}
