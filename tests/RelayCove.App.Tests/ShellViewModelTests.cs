using System.Collections.Specialized;
using CommunityToolkit.Mvvm.Input;
using RelayCove.App.Services;
using RelayCove.App.ViewModels;
using RelayCove.Core;

namespace RelayCove.App.Tests;

public sealed partial class ShellViewModelTests
{
    [Theory]
    [InlineData(7)]
    [InlineData(8)]
    public void Messages_WhenEditMetadataChanges_UpdatesExistingRowWithoutChangingContent(long senderId)
    {
        var conversation = new DirectMessage([8]);
        var original = new ChatMessage(1, conversation, senderId, "正文", DateTimeOffset.UnixEpoch, isEdited: true);
        var session = new FakeSession
        {
            CurrentUserId = 7,
            Selected = conversation,
            Recent = [conversation],
            StateValue = new ClientState(messages: new Dictionary<long, ChatMessage> { [1] = original })
        };
        using var viewModel = CreateViewModel(session);
        var item = Assert.Single(viewModel.Messages);
        Assert.True(item.IsEdited);
        Assert.Contains("已编辑", item.AccessibleLabel);
        var changed = new List<string?>();
        item.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        foreach (var isEdited in new[] { false, true })
        {
            session.StateValue = session.StateValue with
            {
                Messages = new Dictionary<long, ChatMessage> { [1] = original with { IsEdited = isEdited } }
            };
            session.Publish();

            Assert.Same(item, Assert.Single(viewModel.Messages));
            Assert.Equal(isEdited, item.IsEdited);
            Assert.Equal("正文", item.Content);
            Assert.Equal("正文", item.Body);
            Assert.Contains(nameof(MessageItem.IsEdited), changed);
            Assert.Contains(nameof(MessageItem.AccessibleLabel), changed);
            changed.Clear();
        }
    }

    [Fact]
    public void StartupSettings_WhenOpenedAndReopened_ReadsStateWithoutWriting()
    {
        var startup = new FakeStartupService();
        using var viewModel = CreateViewModel(new FakeSession(), startupService: startup);

        Assert.False(viewModel.StartWithWindows);
        Assert.True(viewModel.CanChangeStartup);
        Assert.False(viewModel.HasStartupSettingsStatus);
        viewModel.ShowGeneralSettingsCommand.Execute(null);
        Assert.True(viewModel.IsGeneralSettings);
        Assert.Empty(startup.Writes);

        startup.State = StartupState.Enabled;
        viewModel.ShowMessagesCommand.Execute(null);
        viewModel.ShowGeneralSettingsCommand.Execute(null);
        Assert.True(viewModel.StartWithWindows);
        Assert.Empty(startup.Writes);
    }

    [Fact]
    public void StartupSettings_WhenAnotherCopyIsRegistered_ShowsWarningAndCanDisableIt()
    {
        var startup = new FakeStartupService { State = StartupState.DifferentExecutable };
        using var viewModel = CreateViewModel(new FakeSession(), startupService: startup);

        Assert.True(viewModel.StartWithWindows);
        Assert.Equal("开机启动指向其他位置，如需启动当前版本，请关闭后重新开启。", viewModel.StartupSettingsStatus);
        Assert.Empty(startup.Writes);

        viewModel.StartWithWindows = false;
        Assert.False(viewModel.StartWithWindows);
        Assert.Equal([false], startup.Writes);
    }

    [Fact]
    public void StartWithWindows_WhenToggled_PersistsAndRestoresOnNextViewModel()
    {
        var startup = new FakeStartupService();
        using var viewModel = CreateViewModel(new FakeSession(), startupService: startup);

        viewModel.StartWithWindows = true;
        viewModel.StartWithWindows = true;
        Assert.True(viewModel.StartWithWindows);
        Assert.Equal([true], startup.Writes);

        using var reopened = CreateViewModel(new FakeSession(), startupService: startup);
        Assert.True(reopened.StartWithWindows);
        reopened.StartWithWindows = false;
        Assert.False(reopened.StartWithWindows);
        Assert.Equal([true, false], startup.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StartWithWindows_WhenWriteFails_RestoresActualStateAndShowsSafeError(bool initialEnabled)
    {
        var startup = new FakeStartupService
        {
            State = initialEnabled ? StartupState.Enabled : StartupState.Disabled,
            ThrowOnWrite = true
        };
        using var viewModel = CreateViewModel(new FakeSession(), startupService: startup);
        var changes = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changes.Add(args.PropertyName);

        viewModel.StartWithWindows = !initialEnabled;

        Assert.Equal(initialEnabled, viewModel.StartWithWindows);
        Assert.Equal("无法更改开机启动，请稍后重试。", viewModel.StartupSettingsStatus);
        Assert.Contains(nameof(ShellViewModel.StartWithWindows), changes);
        Assert.Equal([!initialEnabled], startup.Writes);
    }

    [Fact]
    public void StartupSettings_WhenReadFails_DisablesSwitchAndRecoversOnReopen()
    {
        var startup = new FakeStartupService { ThrowOnRead = true };
        using var viewModel = CreateViewModel(new FakeSession(), startupService: startup);

        Assert.False(viewModel.CanChangeStartup);
        Assert.Equal("无法读取开机启动状态，请重新打开通用设置重试。", viewModel.StartupSettingsStatus);
        viewModel.StartWithWindows = true;
        Assert.Empty(startup.Writes);

        startup.ThrowOnRead = false;
        viewModel.ShowGeneralSettingsCommand.Execute(null);
        Assert.True(viewModel.CanChangeStartup);
        Assert.False(viewModel.StartWithWindows);
        Assert.False(viewModel.HasStartupSettingsStatus);
    }

    [Theory]
    [InlineData(StartupState.DisabledByWindows)]
    [InlineData(StartupState.UnknownWindowsApproval)]
    public void StartupSettings_WhenSystemBlocksEntry_ShowsWarningAndAllowsRemovingEntry(StartupState state)
    {
        var startup = new FakeStartupService { State = state };
        using var viewModel = CreateViewModel(new FakeSession(), startupService: startup);

        Assert.True(viewModel.StartWithWindows);
        Assert.True(viewModel.HasStartupSettingsStatus);
        Assert.Contains("Windows", viewModel.StartupSettingsStatus);
        Assert.Empty(startup.Writes);

        viewModel.StartWithWindows = false;
        Assert.False(viewModel.StartWithWindows);
        Assert.False(viewModel.HasStartupSettingsStatus);
        Assert.Equal([false], startup.Writes);
    }

    [Theory]
    [InlineData(GatewayErrorCode.NetworkError, null, "无法连接服务器")]
    [InlineData(GatewayErrorCode.RequestTimedOut, null, "连接超时")]
    [InlineData(GatewayErrorCode.InvalidResponse, 200, "服务器响应异常")]
    [InlineData(GatewayErrorCode.ServerError, 503, "服务器错误（503）")]
    [InlineData(GatewayErrorCode.RequestFailed, 408, "连接超时")]
    [InlineData(GatewayErrorCode.BadEventQueueId, 400, "连接已失效")]
    [InlineData(GatewayErrorCode.RateLimited, 429, "服务器繁忙")]
    public void ConnectionStatus_WhenRetryFailsQuickly_ShowsCauseAndAdvancingAttempt(
        GatewayErrorCode code, int? status, string reason)
    {
        var connection = new ConnectionState(
            code == GatewayErrorCode.RateLimited ? ConnectionStatus.RateLimited : ConnectionStatus.Reconnecting, "retry_wait")
        {
            RetryAttempt = 1,
            RetryDelay = TimeSpan.FromSeconds(20),
            FailureCode = code,
            FailureStatusCode = status
        };
        var session = new FakeSession
        {
            Account = AccountId.Create(RealmEndpoint.Parse("https://chat.example.test"), 7),
            StateValue = new ClientState(connection: connection)
        };
        using var viewModel = CreateViewModel(session);
        Assert.Equal($"{reason}，等待第 1 次重连，间隔 20 秒", viewModel.ConnectionStatus);

        session.StateValue = session.StateValue with { Connection = connection with { RetryAttempt = 2, RetryDelay = TimeSpan.FromSeconds(60) } };
        session.Publish();
        Assert.Equal($"{reason}，等待第 2 次重连，间隔 60 秒", viewModel.ConnectionStatus);

        session.StateValue = session.StateValue with
        {
            Connection = connection with { Status = ConnectionStatus.Reconnecting, Detail = "retrying", RetryAttempt = 2, RetryDelay = null }
        };
        session.Publish();
        Assert.Equal("正在重新连接（第 2 次）", viewModel.ConnectionStatus);
        Assert.True(viewModel.ShowConnectionStatus);

        session.StateValue = session.StateValue with { Connection = new ConnectionState(ConnectionStatus.Connected) };
        session.Publish();
        Assert.False(viewModel.ShowConnectionStatus);
    }

    [Theory]
    [InlineData(ConnectionStatus.Offline, "cache_first", false, "正在连接")]
    [InlineData(ConnectionStatus.Connecting, null, false, "正在连接")]
    [InlineData(ConnectionStatus.Connected, null, false, "已连接")]
    [InlineData(ConnectionStatus.Offline, null, true, "连接已中断")]
    [InlineData(ConnectionStatus.Reconnecting, "retry_wait", true, "等待重新连接")]
    [InlineData(ConnectionStatus.Reconnecting, "retrying", true, "正在重新连接")]
    [InlineData(ConnectionStatus.RateLimited, "retry_wait", true, "服务器繁忙，等待重新连接")]
    [InlineData(ConnectionStatus.Faulted, null, true, "连接故障")]
    public void ConnectionStatus_WhenProjected_ShowsOnlyFailuresAndRecovery(
        ConnectionStatus status, string? detail, bool visible, string label)
    {
        var session = new FakeSession
        {
            Account = AccountId.Create(RealmEndpoint.Parse("https://chat.example.test"), 7),
            StateValue = new ClientState(connection: new ConnectionState(status, detail))
        };
        using var viewModel = CreateViewModel(session);
        Assert.Equal(visible, viewModel.ShowConnectionStatus);
        Assert.Equal(label, viewModel.ConnectionStatus);
    }

    [Fact]
    public void ConnectionStatus_WhenCacheLoadsThenConnectionFailsAndRecovers_KeepsMessagesAndUpdatesBanner()
    {
        var conversation = new DirectMessage([8]);
        var cached = new ChatMessage(10, conversation, 8, "cached", DateTimeOffset.UnixEpoch, isRead: true);
        var session = new FakeSession
        {
            Selected = conversation,
            StateValue = new ClientState(connection: new ConnectionState(ConnectionStatus.Offline, "cache_first"),
                messages: new Dictionary<long, ChatMessage> { [10] = cached })
        };
        using var viewModel = CreateViewModel(session);
        var row = Assert.Single(viewModel.Messages);
        Assert.False(viewModel.ShowConnectionStatus);
        Assert.DoesNotContain("离线", viewModel.MessageEmptyTitle);

        foreach (var detail in new[] { "retry_wait", "retrying" })
        {
            session.StateValue = session.StateValue with { Connection = new ConnectionState(ConnectionStatus.Reconnecting, detail) };
            session.Publish();
            Assert.True(viewModel.ShowConnectionStatus);
            Assert.Same(row, Assert.Single(viewModel.Messages));
        }
        session.StateValue = session.StateValue with { Connection = new ConnectionState(ConnectionStatus.Connected) };
        session.Publish();
        Assert.False(viewModel.ShowConnectionStatus);
        Assert.Same(row, Assert.Single(viewModel.Messages));
    }

    [Fact]
    public async Task UploadAvatarCommand_WhenLogoutIsConfirmed_CancelsUploadBeforeLoggingOut()
    {
        var session = AvatarSession();
        CancellationToken uploadToken = default;
        session.UploadAvatarAction = (_, _, token) =>
        {
            uploadToken = token;
            return Task.Delay(Timeout.InfiniteTimeSpan, token);
        };
        var loggedOut = false;
        session.LogoutAction = _ =>
        {
            Assert.True(uploadToken.IsCancellationRequested);
            loggedOut = true;
            return Task.CompletedTask;
        };
        using var viewModel = CreateViewModel(session, fileSelectionService: new FakeFileSelectionService { Files = [AvatarFile()] });
        var upload = viewModel.UploadAvatarCommand.ExecuteAsync(null);
        viewModel.RequestLogoutCommand.Execute(null);
        await viewModel.ConfirmLogoutCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(5));
        await upload.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(loggedOut);
        Assert.False(viewModel.IsAvatarUploadBusy);
        Assert.Equal(1, session.AvatarUploadCalls);
    }

    [Fact]
    public async Task UploadAvatarCommand_WhenAccepted_UpdatesAvatarAndKeepsComposerUntouched()
    {
        var response = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = AvatarSession();
        session.UploadAvatarAction = async (accountId, upload, _) =>
        {
            Assert.Equal(session.AccountId, accountId);
            Assert.Equal("avatar.png", upload.FileName);
            await response.Task;
            session.StateValue = DomainReducer.Apply(session.StateValue,
                new UserPatchedEvent(7, null, null, null, Source: DomainEventSource.Local,
                    HasAvatar: true, AvatarUrl: "/user_avatars/1/updated.png?x=2", AvatarSource: UserAvatarSource.Uploaded));
            session.Publish();
        };
        using var viewModel = CreateViewModel(session, fileSelectionService: new FakeFileSelectionService { Files = [AvatarFile()] });
        viewModel.ComposerText = "unfinished message";
        viewModel.ToggleAccountMenuCommand.Execute(null);

        var pending = viewModel.UploadAvatarCommand.ExecuteAsync(null);
        Assert.True(viewModel.IsAvatarUploadBusy);
        Assert.False(viewModel.UploadAvatarCommand.CanExecute(null));
        Assert.Null(viewModel.CurrentUserAvatarUrl);
        response.SetResult(true);
        await pending;

        Assert.Equal(1, session.AvatarUploadCalls);
        Assert.Equal(0, session.UploadCalls);
        Assert.Equal("/user_avatars/1/updated.png?x=2", viewModel.CurrentUserAvatarUrl);
        Assert.Equal("头像已更新。", viewModel.AvatarUploadStatus);
        Assert.False(viewModel.IsAvatarUploadBusy);
        Assert.True(viewModel.IsAccountMenuOpen);
        Assert.Equal("unfinished message", viewModel.ComposerText);
        Assert.Empty(viewModel.Attachments);
        Assert.Empty(session.SentContents);
    }

    [Fact]
    public async Task UploadAvatarCommand_WhenPickerCancelled_DoesNotUpload()
    {
        var session = AvatarSession();
        using var viewModel = CreateViewModel(session);
        await viewModel.UploadAvatarCommand.ExecuteAsync(null);
        Assert.Equal(0, session.AvatarUploadCalls);
        Assert.False(viewModel.IsAvatarUploadBusy);
        Assert.False(viewModel.HasAvatarUploadStatus);
    }

    [Theory]
    [InlineData("note.txt", 3)]
    [InlineData("empty.png", 0)]
    [InlineData("large.png", 6 * 1024 * 1024)]
    public async Task UploadAvatarCommand_WhenImageIsInvalid_RejectsBeforeReadingFile(string name, long length)
    {
        var session = AvatarSession();
        var opened = false;
        var file = new SelectedAttachmentFile(name, "image/png", length, _ =>
        {
            opened = true;
            return Task.FromResult<Stream>(new MemoryStream([1, 2, 3]));
        });
        using var viewModel = CreateViewModel(session, fileSelectionService: new FakeFileSelectionService { Files = [file] });
        await viewModel.UploadAvatarCommand.ExecuteAsync(null);
        Assert.Equal(0, session.AvatarUploadCalls);
        Assert.False(opened);
        Assert.True(viewModel.HasAvatarUploadStatus);
    }

    [Fact]
    public async Task UploadAvatarCommand_WhenUploadFails_ShowsFailureAndAllowsExplicitRetry()
    {
        var session = AvatarSession();
        session.UploadAvatarAction = (_, _, _) => Task.FromException(new GatewayException(GatewayErrorKind.Server, GatewayErrorCode.NetworkError));
        using var viewModel = CreateViewModel(session, fileSelectionService: new FakeFileSelectionService { Files = [AvatarFile()] });
        await viewModel.UploadAvatarCommand.ExecuteAsync(null);
        Assert.Equal(1, session.AvatarUploadCalls);
        Assert.Contains("无法确认", viewModel.AvatarUploadStatus);
        Assert.True(viewModel.UploadAvatarCommand.CanExecute(null));
        Assert.Null(viewModel.CurrentUserAvatarUrl);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UploadAvatarCommand_WhenAccountChangesDuringSelection_DoesNotUploadToNewAccount(bool duringFileOpen)
    {
        var session = AvatarSession();
        var picker = new FakeFileSelectionService();
        using var viewModel = CreateViewModel(session, fileSelectionService: picker);
        void SwitchAccount()
        {
            session.Account = AccountId.Create(RealmEndpoint.Parse("https://chat.example.test"), 8);
            session.Publish();
        }
        picker.PickAvatarAction = _ =>
        {
            if (!duringFileOpen) SwitchAccount();
            return Task.FromResult<SelectedAttachmentFile?>(new SelectedAttachmentFile("avatar.png", "image/png", 3, _ =>
            {
                if (duringFileOpen) SwitchAccount();
                return Task.FromResult<Stream>(new MemoryStream([1, 2, 3]));
            }));
        };
        await viewModel.UploadAvatarCommand.ExecuteAsync(null);
        Assert.Equal(0, session.AvatarUploadCalls);
        Assert.False(viewModel.HasAvatarUploadStatus);
        Assert.False(viewModel.IsAvatarUploadBusy);
    }

    [Fact]
    public async Task UploadAvatarCommand_WhenOldAccountResponseArrives_DoesNotShowSuccessForNewAccount()
    {
        var response = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = AvatarSession();
        session.UploadAvatarAction = (_, _, _) => response.Task;
        using var viewModel = CreateViewModel(session, fileSelectionService: new FakeFileSelectionService { Files = [AvatarFile()] });
        var pending = viewModel.UploadAvatarCommand.ExecuteAsync(null);
        session.Account = AccountId.Create(RealmEndpoint.Parse("https://chat.example.test"), 8);
        session.Publish();
        response.SetResult(true);
        await pending;
        Assert.False(viewModel.HasAvatarUploadStatus);
        Assert.False(viewModel.IsAvatarUploadBusy);
    }

    [Fact]
    public void EditOwnNameCommand_WhenOpenedAndCancelled_PrefillsAndDoesNotWrite()
    {
        var session = AvatarSession();
        using var viewModel = CreateViewModel(session);
        viewModel.ToggleAccountMenuCommand.Execute(null);
        viewModel.EditOwnNameCommand.Execute(null);
        Assert.True(viewModel.IsOwnNameEditing);
        Assert.Equal("Me", viewModel.OwnNameDraft);
        Assert.False(viewModel.SaveOwnNameCommand.CanExecute(null));
        viewModel.OwnNameDraft = "   ";
        Assert.False(viewModel.SaveOwnNameCommand.CanExecute(null));
        viewModel.OwnNameDraft = "New name";
        Assert.True(viewModel.SaveOwnNameCommand.CanExecute(null));
        viewModel.CancelOwnNameEditCommand.Execute(null);
        Assert.False(viewModel.IsOwnNameEditing);
        Assert.Equal(0, session.NameUpdateCalls);
        Assert.Equal("Me", viewModel.CurrentUserDisplayName);
    }

    [Fact]
    public async Task SaveOwnNameCommand_WhenConfirmed_UpdatesNameWithoutChangingComposer()
    {
        var response = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = AvatarSession();
        session.UpdateOwnNameAction = async (accountId, name, _) =>
        {
            Assert.Equal(session.AccountId, accountId);
            Assert.Equal("新名字", name);
            await response.Task;
            session.StateValue = DomainReducer.Apply(session.StateValue,
                new UserPatchedEvent(7, name, null, null, Source: DomainEventSource.Local));
            session.Publish();
            return true;
        };
        using var viewModel = CreateViewModel(session);
        viewModel.ComposerText = "unfinished message";
        viewModel.ToggleAccountMenuCommand.Execute(null);
        viewModel.EditOwnNameCommand.Execute(null);
        viewModel.OwnNameDraft = " 新名字 ";
        var pending = viewModel.SaveOwnNameCommand.ExecuteAsync(null);
        Assert.True(viewModel.IsOwnNameSaveBusy);
        Assert.False(viewModel.SaveOwnNameCommand.CanExecute(null));
        Assert.False(viewModel.EditOwnNameCommand.CanExecute(null));
        Assert.False(viewModel.CancelOwnNameEditCommand.CanExecute(null));
        Assert.Equal("Me", viewModel.CurrentUserDisplayName);
        response.SetResult(true);
        await pending;
        Assert.Equal(1, session.NameUpdateCalls);
        Assert.Equal("新名字", viewModel.CurrentUserDisplayName);
        Assert.Equal("名字已更新。", viewModel.OwnNameEditStatus);
        Assert.False(viewModel.IsOwnNameEditing);
        Assert.False(viewModel.IsOwnNameSaveBusy);
        Assert.True(viewModel.IsAccountMenuOpen);
        Assert.Equal("unfinished message", viewModel.ComposerText);
        Assert.Empty(session.SentContents);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveOwnNameCommand_WhenNotConfirmed_KeepsDraftAndAllowsManualRetry(bool throws)
    {
        var session = AvatarSession();
        session.UpdateOwnNameAction = (_, _, _) => throws
            ? Task.FromException<bool>(new GatewayException(GatewayErrorKind.Server, GatewayErrorCode.NetworkError))
            : Task.FromResult(false);
        using var viewModel = CreateViewModel(session);
        viewModel.EditOwnNameCommand.Execute(null);
        viewModel.OwnNameDraft = "New name";
        await viewModel.SaveOwnNameCommand.ExecuteAsync(null);
        Assert.Equal(1, session.NameUpdateCalls);
        Assert.Equal("Me", viewModel.CurrentUserDisplayName);
        Assert.Equal("New name", viewModel.OwnNameDraft);
        Assert.Contains(throws ? "无法确认" : "名字未更新", viewModel.OwnNameEditStatus);
        Assert.True(viewModel.IsOwnNameEditing);
        Assert.True(viewModel.SaveOwnNameCommand.CanExecute(null));
    }

    [Fact]
    public async Task SaveOwnNameCommand_WhenAccountChanges_DiscardsDraftAndLateResponse()
    {
        var response = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = AvatarSession();
        session.UpdateOwnNameAction = (_, _, _) => response.Task;
        using var viewModel = CreateViewModel(session);
        viewModel.EditOwnNameCommand.Execute(null);
        viewModel.OwnNameDraft = "New name";
        var pending = viewModel.SaveOwnNameCommand.ExecuteAsync(null);
        session.Account = AccountId.Create(RealmEndpoint.Parse("https://chat.example.test"), 8);
        session.Publish();
        response.SetResult(true);
        await pending;
        Assert.False(viewModel.HasOwnNameEditStatus);
        Assert.False(viewModel.IsOwnNameEditing);
        Assert.False(viewModel.IsOwnNameSaveBusy);
        Assert.Empty(viewModel.OwnNameDraft);
    }

    [Fact]
    public async Task SaveOwnNameCommand_WhenLogoutConfirmed_CancelsBeforeLogout()
    {
        var session = AvatarSession();
        CancellationToken saveToken = default;
        session.UpdateOwnNameAction = async (_, _, token) =>
        {
            saveToken = token;
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return true;
        };
        session.LogoutAction = _ =>
        {
            Assert.True(saveToken.IsCancellationRequested);
            return Task.CompletedTask;
        };
        using var viewModel = CreateViewModel(session);
        viewModel.EditOwnNameCommand.Execute(null);
        viewModel.OwnNameDraft = "New name";
        var pending = viewModel.SaveOwnNameCommand.ExecuteAsync(null);
        viewModel.RequestLogoutCommand.Execute(null);
        await viewModel.ConfirmLogoutCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(5));
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(viewModel.IsOwnNameSaveBusy);
        Assert.False(viewModel.HasOwnNameEditStatus);
    }

    [Fact]
    public void EditOwnNameCommand_WhenMenuCloses_DiscardsUnsavedDraft()
    {
        var session = AvatarSession();
        using var viewModel = CreateViewModel(session);
        viewModel.ToggleAccountMenuCommand.Execute(null);
        viewModel.EditOwnNameCommand.Execute(null);
        viewModel.OwnNameDraft = "Unsaved name";
        viewModel.CloseAccountMenuCommand.Execute(null);
        Assert.False(viewModel.IsOwnNameEditing);
        viewModel.ToggleAccountMenuCommand.Execute(null);
        viewModel.EditOwnNameCommand.Execute(null);
        Assert.Equal("Me", viewModel.OwnNameDraft);
        Assert.Equal(0, session.NameUpdateCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConversationMenu_WhenOtherDirectMessageIsTargeted_ChangesOnlyTargetPreference(bool pin)
    {
        var session = ConversationMenuSession();
        var preferences = new InMemoryConversationPreferencesStore();
        using var viewModel = CreateViewModel(session, conversationPreferencesStore: preferences);
        viewModel.ComposerText = "keep my draft";
        var target = viewModel.CreateConversationMenuTarget(viewModel.Conversations.Single(item => item.Conversation == new DirectMessage([9])))!;
        Assert.NotNull(target);
        Assert.Equal(new DirectMessage([8]), session.Selected);
        if (pin) await viewModel.ToggleConversationPinnedCommand.ExecuteAsync(target);
        else await viewModel.ToggleConversationMutedCommand.ExecuteAsync(target);
        var saved = preferences.Get(target.AccountId, target.Conversation.CanonicalKey);
        Assert.Equal(pin, saved.IsPinned);
        Assert.Equal(!pin, saved.IsMuted);
        Assert.Equal(new ConversationPreference(), preferences.Get(target.AccountId, new DirectMessage([8]).CanonicalKey));
        Assert.Equal(new DirectMessage([8]), session.Selected);
        Assert.Equal("keep my draft", viewModel.ComposerText);
        Assert.Equal(0, session.SubscriptionPreferenceCalls);
        if (pin) Assert.Equal(target.Conversation, viewModel.Conversations.First().Conversation);
        var updated = viewModel.CreateConversationMenuTarget(viewModel.Conversations.Single(item => item.Conversation == target.Conversation))!;
        if (pin) await viewModel.ToggleConversationPinnedCommand.ExecuteAsync(updated);
        else await viewModel.ToggleConversationMutedCommand.ExecuteAsync(updated);
        Assert.Equal(new ConversationPreference(), preferences.Get(target.AccountId, target.Conversation.CanonicalKey));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConversationMenu_WhenGroupPreferencePending_UsesTargetAndWaitsForConfirmation(bool pin)
    {
        var response = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = ConversationMenuSession();
        session.SubscriptionPreferenceAction = async (id, preference, value, _) =>
        {
            Assert.Equal(4, id);
            Assert.Equal(pin ? SubscriptionPreference.Pinned : SubscriptionPreference.Muted, preference);
            Assert.True(value);
            await response.Task;
            session.StateValue = DomainReducer.Apply(session.StateValue,
                new SubscriptionPreferenceChangedEvent(id, preference, value, Source: DomainEventSource.Local));
            session.Publish();
        };
        using var viewModel = CreateViewModel(session);
        var row = viewModel.Conversations.Single(item => item.Conversation == new ChannelTopic(4, string.Empty));
        var target = viewModel.CreateConversationMenuTarget(row)!;
        var pending = pin ? viewModel.ToggleConversationPinnedCommand.ExecuteAsync(target) : viewModel.ToggleConversationMutedCommand.ExecuteAsync(target);
        Assert.True(viewModel.IsConversationMenuActionBusy);
        Assert.False(viewModel.ToggleConversationPinnedCommand.CanExecute(target));
        Assert.False(row.IsPinned);
        Assert.False(row.IsMuted);
        Assert.Equal(new DirectMessage([8]), session.Selected);
        response.SetResult(true);
        await pending;
        Assert.Equal(pin, row.IsPinned);
        Assert.Equal(!pin, row.IsMuted);
        Assert.Equal(1, session.SubscriptionPreferenceCalls);
    }

    [Fact]
    public async Task ConversationMenu_WhenAccountChanges_RejectsOldTargetAndDiscardsLateError()
    {
        var response = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = ConversationMenuSession();
        session.SubscriptionPreferenceAction = async (_, _, _, _) =>
        {
            await response.Task;
            throw new GatewayException(GatewayErrorKind.Server, GatewayErrorCode.ServerError);
        };
        using var viewModel = CreateViewModel(session);
        var target = viewModel.CreateConversationMenuTarget(viewModel.Conversations.Single(item => item.IsPrivateGroup))!;
        var pending = viewModel.ToggleConversationPinnedCommand.ExecuteAsync(target);
        session.Account = AccountId.Create(RealmEndpoint.Parse("https://chat.example.test"), 20);
        session.Publish();
        response.SetResult(true);
        await pending;
        Assert.Null(viewModel.LoginError);
        Assert.False(viewModel.DeleteConversationCommand.CanExecute(target));
        await viewModel.DeleteConversationCommand.ExecuteAsync(target);
        Assert.Equal(0, session.CloseConversationCalls);
        Assert.Equal(1, session.SubscriptionPreferenceCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteConversation_WhenRemoved_HidesPersistentlyWithoutDeletingHistory(bool selected)
    {
        var session = ConversationMenuSession();
        var preferences = new InMemoryConversationPreferencesStore();
        var conversation = new DirectMessage([selected ? 8 : 9]);
        using (var viewModel = CreateViewModel(session, conversationPreferencesStore: preferences))
        {
            viewModel.ComposerText = "keep draft";
            var originalState = session.StateValue;
            var target = viewModel.CreateConversationMenuTarget(viewModel.Conversations.Single(item => item.Conversation == conversation))!;
            await viewModel.DeleteConversationCommand.ExecuteAsync(target);
            Assert.DoesNotContain(viewModel.Conversations, item => item.Conversation == conversation);
            Assert.Same(originalState, session.StateValue);
            Assert.Equal(selected ? null : new DirectMessage([8]), session.Selected);
            Assert.Equal(selected ? 1 : 0, session.CloseConversationCalls);
            Assert.Equal(0, session.SubscriptionPreferenceCalls);
            if (selected)
            {
                Assert.False(viewModel.HasSelectedConversation);
                Assert.Empty(viewModel.Messages);
            }
            else Assert.Equal("keep draft", viewModel.ComposerText);
        }
        using var reopened = CreateViewModel(session, conversationPreferencesStore: preferences);
        Assert.DoesNotContain(reopened.Conversations, item => item.Conversation == conversation);
        var original = session.StateValue.Messages.Values.Single(message => message.Conversation == conversation);
        session.StateValue = DomainReducer.Apply(session.StateValue,
            new MessageUpsertEvent(original with { Content = "edited old message" }, Source: DomainEventSource.Local));
        session.Publish();
        Assert.DoesNotContain(reopened.Conversations, item => item.Conversation == conversation);
        session.StateValue = DomainReducer.Apply(session.StateValue,
            new MessageUpsertEvent(new ChatMessage(99, conversation, 9, "new message", DateTimeOffset.UtcNow), Source: DomainEventSource.Local));
        session.Publish();
        Assert.Contains(reopened.Conversations, item => item.Conversation == conversation);
        Assert.Null(preferences.Get(session.Account!.Value, conversation.CanonicalKey).HiddenThroughMessageId);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("search")]
    [InlineData("saved")]
    public async Task DeleteConversation_WhenReopenedFromSearch_RestoresRowAndDraft(string entryPoint)
    {
        var session = ConversationMenuSession();
        session.OpenMessageAction = (conversation, _, _) =>
        {
            session.Selected = conversation;
            session.Publish();
            return Task.CompletedTask;
        };
        using var viewModel = CreateViewModel(session);
        viewModel.ComposerText = "keep draft";
        var target = viewModel.CreateConversationMenuTarget(viewModel.Conversations.Single(item => item.Conversation == session.Selected))!;
        await viewModel.DeleteConversationCommand.ExecuteAsync(target);
        if (entryPoint == "list")
        {
            viewModel.ConversationFilterQuery = "first message";
            var match = viewModel.FilteredConversations.First(item => item.Conversation == target.Conversation);
            viewModel.ActivateConversation(match);
        }
        else if (entryPoint == "search")
            await viewModel.SelectSearchResultCommand.ExecuteAsync(new SearchResultItem("10", "消息", "Bea", "first message", target.Conversation, 10));
        else
            await viewModel.OpenSavedMessageCommand.ExecuteAsync(new SavedMessageItem(10, target.Conversation, "Bea", "first message", ""));
        await WaitUntilAsync(() => session.Selected == target.Conversation);
        Assert.Contains(viewModel.Conversations, item => item.Conversation == target.Conversation);
        Assert.Equal("keep draft", viewModel.ComposerText);
    }

    [Fact]
    public void ConversationMenu_WhenAccountChangesBeforeProjection_DoesNotTargetOldRowWithNewAccount()
    {
        var session = ConversationMenuSession();
        using var viewModel = CreateViewModel(session);
        var oldRow = viewModel.Conversations.First();
        session.Account = AccountId.Create(RealmEndpoint.Parse("https://chat.example.test"), 20);
        Assert.Null(viewModel.CreateConversationMenuTarget(oldRow));
    }

    [Fact]
    public async Task DeleteConversation_WhenPreferencesChangeWhileClosing_PreservesLatestPreferences()
    {
        var response = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = ConversationMenuSession();
        session.CloseConversationAction = (_, _, _) => response.Task;
        var preferences = new InMemoryConversationPreferencesStore();
        using var viewModel = CreateViewModel(session, conversationPreferencesStore: preferences);
        var target = viewModel.CreateConversationMenuTarget(viewModel.Conversations.Single(item => item.Conversation == session.Selected))!;
        var pending = viewModel.DeleteConversationCommand.ExecuteAsync(target);
        var changed = new ConversationPreference(IsMuted: true, IsPinned: true, Remark: "latest remark");
        preferences.Save(target.AccountId, target.Conversation.CanonicalKey, changed);
        response.SetResult(true);
        await pending;
        Assert.Equal(changed with { HiddenThroughMessageId = 10 }, preferences.Get(target.AccountId, target.Conversation.CanonicalKey));
    }

    [Fact]
    public void ConversationMenu_WhenOffline_AllowsLocalActionsAndDisablesGroupWrites()
    {
        var session = ConversationMenuSession();
        session.StateValue = session.StateValue with { Connection = new ConnectionState(ConnectionStatus.Offline) };
        using var viewModel = CreateViewModel(session);
        var group = viewModel.CreateConversationMenuTarget(viewModel.Conversations.Single(item => item.IsPrivateGroup))!;
        var direct = viewModel.CreateConversationMenuTarget(viewModel.Conversations.First(item => !item.IsPrivateGroup))!;
        Assert.False(viewModel.ToggleConversationPinnedCommand.CanExecute(group));
        Assert.False(viewModel.ToggleConversationMutedCommand.CanExecute(group));
        Assert.True(viewModel.DeleteConversationCommand.CanExecute(group));
        Assert.True(viewModel.ToggleConversationPinnedCommand.CanExecute(direct));
    }

    private static FakeSession ConversationMenuSession() => new()
    {
        Account = AccountId.Create(RealmEndpoint.Parse("https://chat.example.test"), 7),
        CurrentUserId = 7,
        Selected = new DirectMessage([8]),
        Recent = [new DirectMessage([8]), new DirectMessage([9])],
        StateValue = new ClientState(
            users: new Dictionary<long, UserProfile> { [7] = new(7, "Me"), [8] = new(8, "Bea"), [9] = new(9, "Cy") },
            subscriptions: new Dictionary<long, Subscription> { [4] = PrivateGroupSubscription(4, "Group") },
            messages: new Dictionary<long, ChatMessage>
            {
                [10] = new(10, new DirectMessage([8]), 8, "first message", DateTimeOffset.UnixEpoch.AddSeconds(10)),
                [11] = new(11, new DirectMessage([9]), 9, "second message", DateTimeOffset.UnixEpoch.AddSeconds(11))
            },
            connection: new ConnectionState(ConnectionStatus.Connected))
    };

    private static FakeSession AvatarSession() => new()
    {
        Account = AccountId.Create(RealmEndpoint.Parse("https://chat.example.test"), 7),
        CurrentUserId = 7,
        StateValue = new ClientState(
            users: new Dictionary<long, UserProfile> { [7] = new(7, "Me", avatarSource: UserAvatarSource.Generated) },
            connection: new ConnectionState(ConnectionStatus.Connected))
    };

    private static SelectedAttachmentFile AvatarFile() => new("avatar.png", "image/png", 3,
        _ => Task.FromResult<Stream>(new MemoryStream([1, 2, 3])));

    [Theory]
    [InlineData(UserAvatarSource.Generated, null)]
    [InlineData(UserAvatarSource.Uploaded, "/user_avatars/current.png")]
    public async Task Avatar_WhenSourceIsKnown_ProjectsConsistentlyAndUpdatesOpenViews(UserAvatarSource source, string? expected)
    {
        var direct = new DirectMessage([8]);
        var session = new FakeSession
        {
            Account = AccountId.Create(RealmEndpoint.Parse("https://zulip.example"), 7),
            CurrentUserId = 7, Selected = direct, Recent = [direct],
            StateValue = new ClientState(
                users: new Dictionary<long, UserProfile> { [7] = new UserProfile(7, "Ada", avatarUrl: "/user_avatars/current.png", avatarVersion: 2, avatarSource: source), [8] = new UserProfile(8, "Bea", avatarUrl: "/user_avatars/current.png", avatarVersion: 2, avatarSource: source) },
                messages: new Dictionary<long, ChatMessage> { [1] = new ChatMessage(1, direct, 7, "hello", DateTimeOffset.UnixEpoch, senderAvatarUrl: "/user_avatars/old.png") },
                connection: new ConnectionState(ConnectionStatus.Connected))
        };
        using var viewModel = CreateViewModel(session);
        await ((IAsyncRelayCommand)viewModel.ToggleDetailsCommand).ExecuteAsync(null);
        Assert.Equal(expected, viewModel.CurrentUserAvatarUrl);
        Assert.Equal(expected, Assert.Single(viewModel.Conversations).AvatarUrl);
        Assert.All(viewModel.KnownContacts, contact => Assert.Equal(expected, contact.AvatarUrl));
        Assert.Equal(expected, Assert.Single(viewModel.Messages).SenderAvatarUrl);
        Assert.Equal(expected, viewModel.DetailsAvatarUrl);
        session.StateValue = DomainReducer.Apply(session.StateValue, new UserPatchedEvent(7, null, null, null,
            HasAvatar: true, AvatarUrl: "/user_avatars/replaced.png", AvatarVersion: 3, AvatarSource: UserAvatarSource.Uploaded));
        session.StateValue = DomainReducer.Apply(session.StateValue, new UserPatchedEvent(8, null, null, null,
            HasAvatar: true, AvatarUrl: "/user_avatars/replaced.png", AvatarVersion: 3, AvatarSource: UserAvatarSource.Uploaded));
        session.Publish();
        Assert.Equal("/user_avatars/replaced.png", viewModel.CurrentUserAvatarUrl);
        Assert.Equal("/user_avatars/replaced.png", Assert.Single(viewModel.Messages).SenderAvatarUrl);
        Assert.Equal("/user_avatars/replaced.png", viewModel.DetailsAvatarUrl);
    }

    [Fact]
    public void DirectConversation_WhenPersonalStatusIsAvailableOrUpdated_ShowsOnlyPresence()
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
        Assert.Equal("在线", viewModel.ConversationSubtitle);

        session.StateValue = session.StateValue with
        {
            UserStatuses = new UserStatusState(true, new Dictionary<long, UserStatusContent>
            {
                [20] = new UserStatusContent("在办公室", new EmojiReactionIdentity("office", "1f3e2", "unicode_emoji"))
            })
        };
        session.Publish();

        Assert.Equal("在线", viewModel.ConversationSubtitle);
        Assert.Equal("Bea", Assert.Single(viewModel.Conversations).Title);
        Assert.Equal("在线", Assert.Single(viewModel.FilteredConversations).PresenceLabel);
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
            SearchMessagesAction = (_, _, _, _) => Task.FromResult(new MessageQueryPage([anchor], true, true, true)),
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
        viewModel.SearchQuery = "matched";
        await ((IAsyncRelayCommand)viewModel.SearchNowCommand).ExecuteAsync(null);

        await ((IAsyncRelayCommand<SearchResultItem?>)viewModel.SelectSearchResultCommand).ExecuteAsync(
            Assert.Single(viewModel.SearchResults));

        Assert.Equal((conversation, anchor.Id), Assert.Single(session.OpenedMessages));
        var request = Assert.IsType<MessageScrollRequest>(viewModel.PendingMessageScrollRequest);
        Assert.Equal(anchor.Id, request.TargetMessageId);
        Assert.Equal(MessageScrollReason.MessageAnchor, request.Reason);
        Assert.True(viewModel.IsMessagesSection);
        Assert.False(viewModel.IsSearchOpen);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScrollToLatest_WhenSearchHistoryNeedsReload_UsesOnlySuccessfulLatestPage(bool failReload)
    {
        var conversation = new DirectMessage([20]);
        var anchor = new ChatMessage(85, conversation, 20, "search hit", DateTimeOffset.UnixEpoch);
        var newest = new ChatMessage(1000, conversation, 20, "newest", DateTimeOffset.UnixEpoch.AddDays(1));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reloads = 0;
        var session = new FakeSession
        {
            Account = AccountId.Create(RealmEndpoint.Parse("https://zulip.example"), 10),
            CurrentUserId = 10,
            StateValue = new ClientState(connection: new ConnectionState(ConnectionStatus.Connected))
        };
        session.OpenMessageAction = (opened, _, _) =>
        {
            session.Selected = opened;
            session.HistoryState = new ConversationHistoryState(opened, 3, false, false, false, 85, null);
            session.StateValue = session.StateValue with { Messages = new Dictionary<long, ChatMessage> { [85] = anchor } };
            session.Publish();
            return Task.CompletedTask;
        };
        session.SelectAction = async (selected, cancellationToken) =>
        {
            Assert.Equal(conversation, selected);
            reloads++;
            session.HistoryState = session.HistoryState with { Generation = 4, IsLoading = true };
            session.Publish();
            started.SetResult();
            await release.Task.WaitAsync(cancellationToken);
            session.HistoryState = session.HistoryState with
            {
                IsLoading = false,
                Error = failReload ? "history_failed" : null
            };
            if (!failReload)
                session.StateValue = session.StateValue with { Messages = new Dictionary<long, ChatMessage> { [1000] = newest } };
            session.Publish();
        };
        using var viewModel = CreateViewModel(session);
        await viewModel.SelectSearchResultCommand.ExecuteAsync(
            new SearchResultItem("server-message:85", "消息", "Bea", anchor.Content, conversation, anchor.Id));
        var anchorRequest = Assert.IsType<MessageScrollRequest>(viewModel.PendingMessageScrollRequest);
        viewModel.AcknowledgeMessageScrollRequest(anchorRequest);

        var jump = viewModel.ScrollToLatestCommand.ExecuteAsync(null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotEqual(MessageScrollReason.ManualJumpToLatest, viewModel.PendingMessageScrollRequest?.Reason);
        release.SetResult();
        await jump;

        Assert.Equal(1, reloads);
        if (failReload)
        {
            Assert.True(viewModel.HasConversationActivationError);
            Assert.NotEqual(MessageScrollReason.ManualJumpToLatest, viewModel.PendingMessageScrollRequest?.Reason);
        }
        else
        {
            var request = Assert.IsType<MessageScrollRequest>(viewModel.PendingMessageScrollRequest);
            Assert.Equal(newest.Id, request.TargetMessageId);
            Assert.Equal(4, request.Generation);
            Assert.Equal(MessageScrollReason.ManualJumpToLatest, request.Reason);
            viewModel.AcknowledgeMessageScrollRequest(request);
            await viewModel.ScrollToLatestCommand.ExecuteAsync(null);
            Assert.Equal(1, reloads);
        }
    }

    [Theory]
    [InlineData("server")]
    [InlineData("")]
    [InlineData(" \t ")]
    public async Task LoadOlderSearch_WhenTwoServerPagesExist_KeepsBothPagesVisible(string query)
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
        viewModel.SearchQuery = query;

        await ((IAsyncRelayCommand)viewModel.SearchNowCommand).ExecuteAsync(null);
        await ((IAsyncRelayCommand)viewModel.LoadOlderSearchCommand).ExecuteAsync(null);

        Assert.Equal(2, calls);
        Assert.Equal(100, viewModel.SearchResults.Count);
        Assert.Contains(viewModel.SearchResults, item => item.Id == "server-message:1");
        Assert.Contains(viewModel.SearchResults, item => item.Id == "server-message:100");
    }

    [Fact]
    public async Task LoadOlderSearch_WhenFilteredPagesHaveNoVisibleMessages_ContinuesToSupportedMessages()
    {
        var session = new FakeSession
        {
            Account = AccountId.Create(RealmEndpoint.Parse("https://zulip.example"), 7),
            SearchMessagesAction = (_, before, _, _) => Task.FromResult(before is null
                ? new MessageQueryPage([new ChatMessage(90, new DirectMessage([8, 9]), 8, "隐藏消息", DateTimeOffset.UnixEpoch)], false, true, true) with { Messages = [] }
                : before == 90
                    ? new MessageQueryPage([new ChatMessage(80, new ChannelTopic(999, ""), 8, "隐藏消息", DateTimeOffset.UnixEpoch)], false, true, true) with { Messages = [] }
                    : new MessageQueryPage([new ChatMessage(70, new DirectMessage([8]), 8, "可见消息", DateTimeOffset.UnixEpoch)], true, true, true))
        };
        using var viewModel = CreateViewModel(session);
        viewModel.OpenSearchCommand.Execute(null);

        await viewModel.SearchNowCommand.ExecuteAsync(null);
        Assert.Empty(viewModel.SearchResults);
        Assert.True(viewModel.HasMoreSearchResults);
        await viewModel.LoadOlderSearchCommand.ExecuteAsync(null);
        Assert.Empty(viewModel.SearchResults);
        Assert.True(viewModel.HasMoreSearchResults);
        await viewModel.LoadOlderSearchCommand.ExecuteAsync(null);

        Assert.Equal(70, Assert.Single(viewModel.SearchResults).MessageId);
        Assert.False(viewModel.HasMoreSearchResults);
        Assert.Equal(new long?[] { null, 90, 80 }, session.SearchRequests.Select(request => request.BeforeMessageId));
    }

    [Theory]
    [InlineData("dm", "report")]
    [InlineData("self", "report")]
    [InlineData("group", "report")]
    [InlineData("dm", "")]
    [InlineData("self", "")]
    [InlineData("group", "")]
    public async Task ConversationSearch_WhenSubmittedPagedAndFiltered_KeepsOpenedConversationScope(string kind, string query)
    {
        ConversationKey conversation = kind switch
        {
            "group" => new ChannelTopic(4, string.Empty),
            "self" => new DirectMessage([]),
            _ => new DirectMessage([20])
        };
        var session = new FakeSession
        {
            Selected = conversation,
            StateValue = new ClientState(
                subscriptions: new Dictionary<long, Subscription> { [4] = PrivateGroupSubscription() }),
            SearchMessagesAction = (_, beforeMessageId, _, _) => Task.FromResult(new MessageQueryPage(
                [
                    new ChatMessage(beforeMessageId is null ? 90 : 70, conversation, 20, "[report](https://example.test/report)", DateTimeOffset.UnixEpoch),
                    new ChatMessage(beforeMessageId is null ? 80 : 60, new DirectMessage([30]), 30, "report elsewhere", DateTimeOffset.UnixEpoch)
                ], beforeMessageId is not null, true, true))
        };
        using var viewModel = CreateViewModel(session);
        viewModel.OpenConversationSearchCommand.Execute(null);
        viewModel.SearchQuery = query;
        Assert.Empty(session.SearchRequests);

        await ((IAsyncRelayCommand)viewModel.SearchNowCommand).ExecuteAsync(null);
        Assert.Equal(90, Assert.Single(viewModel.SearchResults).MessageId);
        // The dialog owns its scope even if the background selection changes.
        session.Selected = new DirectMessage([30]);
        await ((IAsyncRelayCommand)viewModel.LoadOlderSearchCommand).ExecuteAsync(null);
        Assert.Equal(new long?[] { 90, 70 }, viewModel.SearchResults.Select(result => result.MessageId));

        viewModel.SelectSearchCategoryCommand.Execute(
            viewModel.SearchCategories.Single(category => category.Filter == MessageSearchFilter.Links));
        Assert.Equal(2, session.SearchRequests.Count);
        await ((IAsyncRelayCommand)viewModel.SearchNowCommand).ExecuteAsync(null);

        Assert.Equal(90, Assert.Single(viewModel.SearchResults).MessageId);
        Assert.Collection(session.SearchRequests,
            request => Assert.Null(request.BeforeMessageId),
            request => Assert.Equal(80, request.BeforeMessageId),
            request =>
            {
                Assert.Null(request.BeforeMessageId);
                Assert.Equal(MessageSearchFilter.Links, request.Filter);
            });
        Assert.All(session.SearchRequests, request =>
        {
            Assert.Equal(query, request.Query);
            Assert.Equal(conversation.CanonicalKey, request.Conversation?.CanonicalKey);
        });
    }

    [Fact]
    public async Task ConversationSearch_WhenReopenedForAnotherPerson_DiscardsPreviousResponseAndReplacesScope()
    {
        var first = new DirectMessage([20]);
        var second = new DirectMessage([30]);
        var pending = new TaskCompletionSource<MessageQueryPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new FakeSession { Selected = first, SearchMessagesAction = (_, _, _, _) => pending.Task };
        using var viewModel = CreateViewModel(session);
        viewModel.OpenConversationSearchCommand.Execute(null);
        viewModel.SearchQuery = "report";
        var search = ((IAsyncRelayCommand)viewModel.SearchNowCommand).ExecuteAsync(null);

        viewModel.CloseSearchCommand.Execute(null);
        session.Selected = second;
        viewModel.OpenConversationSearchCommand.Execute(null);
        viewModel.SearchQuery = "report";
        pending.SetResult(new MessageQueryPage(
            [new ChatMessage(90, first, 20, "report", DateTimeOffset.UnixEpoch)], true, true, true));
        await search;
        Assert.Empty(viewModel.SearchResults);

        var message = new ChatMessage(80, second, 30, "report", DateTimeOffset.UnixEpoch);
        session.SearchMessagesAction = (_, _, _, _) => Task.FromResult(new MessageQueryPage([message], true, true, true));
        await ((IAsyncRelayCommand)viewModel.SearchNowCommand).ExecuteAsync(null);
        Assert.Equal(second.CanonicalKey, session.SearchRequests[^1].Conversation?.CanonicalKey);
        Assert.Equal(80, Assert.Single(viewModel.SearchResults).MessageId);
        await ((IAsyncRelayCommand<SearchResultItem?>)viewModel.SelectSearchResultCommand).ExecuteAsync(
            Assert.Single(viewModel.SearchResults));
        Assert.Equal((second, message.Id), Assert.Single(session.OpenedMessages));
        Assert.False(viewModel.IsSearchOpen);

        viewModel.OpenSearchCommand.Execute(null);
        viewModel.SearchQuery = "report";
        await ((IAsyncRelayCommand)viewModel.SearchNowCommand).ExecuteAsync(null);
        Assert.Null(session.SearchRequests[^1].Conversation);
    }

    [Fact]
    public void ConversationSearch_WhenNoConversationIsSelected_DoesNotOpenGlobalSearch()
    {
        var session = new FakeSession();
        using var viewModel = CreateViewModel(session);

        viewModel.OpenConversationSearchCommand.Execute(null);

        Assert.False(viewModel.IsSearchOpen);
        Assert.Empty(session.SearchRequests);
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

    [Theory]
    [InlineData("https://Chat.Example.Test/", "https://chat.example.test/accounts/password/reset/")]
    [InlineData("https://chat.example.test", "https://chat.example.test/accounts/password/reset/")]
    [InlineData("https://chat.example.test:8443/", "https://chat.example.test:8443/accounts/password/reset/")]
    public async Task OpenPasswordResetCommand_WhenRealmIsValid_OpensSameOriginPageWithoutCredentials(
        string realm, string expectedUrl)
    {
        var loginCalls = 0;
        var session = new FakeSession
        {
            LoginAction = (_, _, _, _) =>
            {
                loginCalls++;
                return Task.CompletedTask;
            }
        };
        var interactions = new FakePlatformInteractionService();
        using var viewModel = CreateViewModel(session, platformInteractions: interactions);
        viewModel.Realm = realm;
        viewModel.Email = "person@example.test";
        viewModel.Password = "unused-test-value";
        viewModel.LoginError = "之前的错误";

        await ((IAsyncRelayCommand)viewModel.OpenPasswordResetCommand).ExecuteAsync(null);

        var opened = Assert.Single(interactions.Opened);
        Assert.Equal(new Uri(expectedUrl), opened);
        Assert.Empty(opened.Query);
        Assert.Empty(opened.Fragment);
        Assert.Empty(opened.UserInfo);
        Assert.Equal(0, loginCalls);
        Assert.Null(viewModel.LoginError);
        Assert.Equal("person@example.test", viewModel.Email);
        Assert.Equal("unused-test-value", viewModel.Password);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("chat.example.test")]
    [InlineData("http://chat.example.test")]
    [InlineData("https://chat.example.test/path")]
    [InlineData("https://chat.example.test/?value=sample")]
    [InlineData("https://chat.example.test/#fragment")]
    [InlineData("https://user@chat.example.test/")]
    public async Task OpenPasswordResetCommand_WhenRealmIsInvalid_DoesNotOpenBrowser(string realm)
    {
        var interactions = new FakePlatformInteractionService();
        using var viewModel = CreateViewModel(new FakeSession(), platformInteractions: interactions);
        viewModel.Realm = realm;

        await ((IAsyncRelayCommand)viewModel.OpenPasswordResetCommand).ExecuteAsync(null);

        Assert.Empty(interactions.Opened);
        Assert.Equal("请先输入有效的 HTTPS Realm 地址。", viewModel.LoginError);
    }

    [Fact]
    public async Task OpenPasswordResetCommand_WhenBrowserFails_ShowsSafeErrorAndAllowsRetry()
    {
        var interactions = new FakePlatformInteractionService
        {
            OpenUriAction = _ => throw new InvalidOperationException("private launcher details")
        };
        using var viewModel = CreateViewModel(new FakeSession(), platformInteractions: interactions);
        viewModel.Realm = "https://chat.example.test/";

        await ((IAsyncRelayCommand)viewModel.OpenPasswordResetCommand).ExecuteAsync(null);

        Assert.Equal("无法打开密码重置页面，请检查系统浏览器设置。", viewModel.LoginError);

        interactions.OpenUriAction = null;
        await ((IAsyncRelayCommand)viewModel.OpenPasswordResetCommand).ExecuteAsync(null);

        Assert.Null(viewModel.LoginError);
        Assert.Equal(2, interactions.Opened.Count);
    }

    [Fact]
    public async Task OpenPasswordResetCommand_WhenRealmChanges_UsesCurrentInputWithoutRequiringLogin()
    {
        var interactions = new FakePlatformInteractionService();
        using var viewModel = CreateViewModel(new FakeSession(), platformInteractions: interactions);
        viewModel.Realm = "https://first.example.test/";
        await ((IAsyncRelayCommand)viewModel.OpenPasswordResetCommand).ExecuteAsync(null);
        viewModel.Realm = "https://second.example.test/";
        await ((IAsyncRelayCommand)viewModel.OpenPasswordResetCommand).ExecuteAsync(null);

        Assert.Equal(
            [new Uri("https://first.example.test/accounts/password/reset/"),
             new Uri("https://second.example.test/accounts/password/reset/")],
            interactions.Opened);
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

        viewModel.TaskbarBadgeEnabled = true;
        Assert.Equal((125, false), notifications.BadgeUpdates[^1]);
        viewModel.DoNotDisturb = true;
        Assert.Equal((125, false), notifications.BadgeUpdates[^1]);
        Assert.Equal((125, false), notifications.TrayUnreadUpdates[^1]);
        Assert.Empty(notifications.Notifications);
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
        Assert.True(notifications.StopTrayFlashCalls > 0);
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
        viewModel.TaskbarBadgeEnabled = false;

        Assert.False(preferences.Current.DoNotDisturb);
        Assert.False(preferences.Current.ShowMessagePreview);
        Assert.False(preferences.Current.TaskbarBadgeEnabled);
        Assert.Equal(3, preferences.Saved.Count);
    }

    [Fact]
    public void AutoMarkRead_WhenOwnMessageIsUnread_ReportsItToTheServer()
    {
        var conversation = new DirectMessage([8]);
        var session = new FakeSession
        {
            CurrentUserId = 7,
            Selected = conversation,
            Recent = [conversation],
            HistoryState = new ConversationHistoryState(conversation, 1, false, true, false, 11, null),
            StateValue = new ClientState(
                messages: new Dictionary<long, ChatMessage>
                {
                    [11] = new ChatMessage(11, conversation, 7, "own", DateTimeOffset.UnixEpoch)
                },
                connection: new ConnectionState(ConnectionStatus.Connected))
        };
        using var viewModel = CreateViewModel(session);
        viewModel.AcknowledgeMessageScrollRequest(Assert.IsType<MessageScrollRequest>(viewModel.PendingMessageScrollRequest));
        viewModel.SetWindowActive(true);

        Assert.Equal(conversation, Assert.Single(session.ExpectedMarkReadConversations));
    }

    [Fact]
    public void AutoMarkRead_WhenRequestFails_RetriesAfterWindowBecomesActiveAgain()
    {
        var conversation = new DirectMessage([8]);
        var session = new FakeSession
        {
            CurrentUserId = 7,
            Selected = conversation,
            Recent = [conversation],
            HistoryState = new ConversationHistoryState(conversation, 1, false, true, false, 11, null),
            StateValue = new ClientState(
                messages: new Dictionary<long, ChatMessage>
                {
                    [11] = new ChatMessage(11, conversation, 8, "new", DateTimeOffset.UnixEpoch)
                },
                unread: new UnreadState(new Dictionary<string, int> { [conversation.CanonicalKey] = 1 }),
                connection: new ConnectionState(ConnectionStatus.Connected)),
            MarkDisplayedReadAction = (_, _) => Task.FromException(
                new GatewayException(GatewayErrorKind.Offline, GatewayErrorCode.NetworkError))
        };
        using var viewModel = CreateViewModel(session);
        viewModel.AcknowledgeMessageScrollRequest(Assert.IsType<MessageScrollRequest>(viewModel.PendingMessageScrollRequest));
        viewModel.SetWindowActive(true);
        Assert.Single(session.ExpectedMarkReadConversations);

        session.Publish();
        Assert.Single(session.ExpectedMarkReadConversations);
        Assert.True(viewModel.HasNavigationUnread);

        viewModel.SetWindowActive(false);
        session.MarkDisplayedReadAction = null;
        viewModel.SetWindowActive(true);

        Assert.Equal(2, session.ExpectedMarkReadConversations.Count);
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
    public async Task ActivateDirectMessage_WhenNoMemoryWindow_DisplaysSqlitePageBeforeLatestHistoryCompletes()
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

        var cachedRow = Assert.Single(viewModel.Messages);
        Assert.Equal(20, cachedRow.MessageId);
        Assert.False(viewModel.ShowConversationLoadingIndicator);
        var cachedScroll = Assert.IsType<MessageScrollRequest>(viewModel.PendingMessageScrollRequest);
        Assert.Equal(20, cachedScroll.TargetMessageId);
        var changes = new List<NotifyCollectionChangedAction>();
        viewModel.Messages.CollectionChanged += (_, eventArgs) => changes.Add(eventArgs.Action);

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
        Assert.Same(cachedRow, viewModel.Messages[0]);
        Assert.Equal([NotifyCollectionChangedAction.Add], changes);
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
        Assert.True(viewModel.ShowPrivateGroupCreateDisabledReason);
        Assert.Equal("请填写群聊名称。", viewModel.PrivateGroupCreateDisabledReason);
        Assert.Null(viewModel.NewConversationError);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task StartNewChannelConversation_WhenNameMissing_ShowsDisabledReasonAndDoesNotCreate(string name)
    {
        var calls = 0;
        var session = CreateGroupCreationSession();
        session.CreatePrivateGroupAction = (_, _) =>
        {
            calls++;
            return Task.FromResult(new PrivateGroupCreated(55, "group", new ChannelTopic(55, string.Empty), 3));
        };
        using var viewModel = CreateViewModel(session);
        viewModel.ShowNewChannelConversationCommand.Execute(null);
        foreach (var choice in viewModel.NewConversationChoices) choice.IsSelected = true;
        viewModel.NewPrivateGroupName = name;

        await ((IAsyncRelayCommand)viewModel.StartNewChannelConversationCommand).ExecuteAsync(null);

        Assert.False(viewModel.CanStartNewChannelConversation);
        Assert.True(viewModel.ShowPrivateGroupCreateDisabledReason);
        Assert.Equal("请填写群聊名称。", viewModel.PrivateGroupCreateDisabledReason);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void NewPrivateGroupForm_WhenNameOrMembersChange_UpdatesDisabledReasonAndButtonAvailability()
    {
        using var viewModel = CreateViewModel(CreateGroupCreationSession());
        viewModel.ShowNewChannelConversationCommand.Execute(null);
        var notifications = new List<string?>();
        viewModel.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);

        viewModel.NewPrivateGroupName = "产品设计群";

        Assert.Contains(nameof(ShellViewModel.PrivateGroupCreateDisabledReason), notifications);
        Assert.Contains(nameof(ShellViewModel.ShowPrivateGroupCreateDisabledReason), notifications);
        Assert.Equal("请至少选择两名其他成员。", viewModel.PrivateGroupCreateDisabledReason);
        Assert.True(viewModel.ShowPrivateGroupCreateDisabledReason);
        viewModel.NewConversationChoices[0].IsSelected = true;
        Assert.False(viewModel.CanStartNewChannelConversation);
        notifications.Clear();

        viewModel.NewConversationChoices[1].IsSelected = true;

        Assert.Contains(nameof(ShellViewModel.PrivateGroupCreateDisabledReason), notifications);
        Assert.Contains(nameof(ShellViewModel.ShowPrivateGroupCreateDisabledReason), notifications);
        Assert.True(viewModel.CanStartNewChannelConversation);
        Assert.False(viewModel.ShowPrivateGroupCreateDisabledReason);
        Assert.Equal(string.Empty, viewModel.PrivateGroupCreateDisabledReason);
        viewModel.NewPrivateGroupName = " ";
        Assert.False(viewModel.CanStartNewChannelConversation);
        Assert.True(viewModel.ShowPrivateGroupCreateDisabledReason);
        Assert.Equal("请填写群聊名称。", viewModel.PrivateGroupCreateDisabledReason);
        viewModel.ShowNewDirectConversationCommand.Execute(null);
        Assert.False(viewModel.ShowPrivateGroupCreateDisabledReason);
    }

    [Fact]
    public void ShowNewChannelConversation_WhenOpenedFromMenu_KeepsMultipleOtherMembersSelectable()
    {
        var session = new FakeSession
        {
            CurrentUserId = 7,
            StateValue = new ClientState(
                users: new Dictionary<long, UserProfile>
                {
                    [7] = new UserProfile(7, "Ada"),
                    [8] = new UserProfile(8, "Bea"),
                    [9] = new UserProfile(9, "Chen")
                },
                connection: new ConnectionState(ConnectionStatus.Connected))
        };
        using var viewModel = CreateViewModel(session);

        viewModel.ShowNewChannelConversationCommand.Execute(null);

        Assert.True(viewModel.IsNewConversationOpen);
        Assert.True(viewModel.IsNewChannelConversationMode);
        Assert.Equal([8L, 9L], viewModel.NewConversationChoices.Select(choice => choice.UserId));
        viewModel.NewPrivateGroupName = "New group";
        viewModel.SelectNewDirectConversationContactCommand.Execute(viewModel.NewConversationChoices[0]);
        Assert.All(viewModel.NewConversationChoices, choice => Assert.False(choice.IsSelected));
        viewModel.NewConversationChoices[0].IsSelected = true;
        Assert.False(viewModel.CanStartNewChannelConversation);
        viewModel.NewConversationChoices[1].IsSelected = true;
        viewModel.SelectNewDirectConversationContactCommand.Execute(viewModel.NewConversationChoices[0]);
        Assert.All(viewModel.NewConversationChoices, choice => Assert.True(choice.IsSelected));
        Assert.True(viewModel.CanStartNewChannelConversation);
        viewModel.NewConversationChoices[0].IsSelected = false;
        viewModel.SelectNewDirectConversationContactCommand.Execute(viewModel.NewConversationChoices[0]);
        Assert.False(viewModel.NewConversationChoices[0].IsSelected);
        Assert.True(viewModel.NewConversationChoices[1].IsSelected);
        Assert.False(viewModel.CanStartNewChannelConversation);
        Assert.Empty(session.SentContents);
        Assert.Null(session.SelectedConversation);
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
        Assert.Equal([8L, 9L], viewModel.NewConversationChoices.Select(choice => choice.UserId));
        foreach (var choice in viewModel.NewConversationChoices) choice.IsSelected = true;
        viewModel.NewPrivateGroupName = "产品设计群";
        await ((IAsyncRelayCommand)viewModel.StartNewChannelConversationCommand).ExecuteAsync(null);

        Assert.Equal("产品设计群", requested!.Name);
        Assert.Equal([8L, 9L], requested.OtherMemberIds);
        Assert.Equal(new ChannelTopic(55, string.Empty), session.SelectedConversation);
        Assert.Contains(viewModel.Conversations, item => item.Title == "产品设计群" && item.IsPrivateGroup);
        Assert.False(viewModel.IsNewConversationOpen);
    }

    [Theory]
    [InlineData(GatewayErrorKind.RequestFailed, GatewayErrorCode.ChannelAlreadyExists, 409, "群名已存在", false)]
    [InlineData(GatewayErrorKind.RequestFailed, GatewayErrorCode.PermissionDenied, 400, "权限", false)]
    [InlineData(GatewayErrorKind.RequestFailed, GatewayErrorCode.InvalidChannelName, 400, "修改群名", false)]
    [InlineData(GatewayErrorKind.RequestFailed, GatewayErrorCode.InvalidChannelMembers, 400, "刷新联系人", false)]
    [InlineData(GatewayErrorKind.ReauthRequired, GatewayErrorCode.Unauthorized, 401, "重新登录", false)]
    [InlineData(GatewayErrorKind.RateLimited, GatewayErrorCode.RateLimited, 429, "限流", false)]
    [InlineData(GatewayErrorKind.RequestFailed, GatewayErrorCode.RequestFailed, 403, "服务器拒绝访问", false)]
    [InlineData(GatewayErrorKind.RequestFailed, GatewayErrorCode.RequestFailed, 404, "创建接口", false)]
    [InlineData(GatewayErrorKind.RequestFailed, GatewayErrorCode.RequestFailed, 400, "名称、成员或设置", false)]
    [InlineData(GatewayErrorKind.RequestFailed, GatewayErrorCode.RequestFailed, 418, "未提供可识别", false)]
    [InlineData(GatewayErrorKind.IncompatibleRealm, GatewayErrorCode.RedirectNotAllowed, 302, "服务器地址", false)]
    [InlineData(GatewayErrorKind.Offline, GatewayErrorCode.RequestTimedOut, null, "超时", false)]
    [InlineData(GatewayErrorKind.Offline, GatewayErrorCode.RequestTimedOut, null, "超时", true)]
    [InlineData(GatewayErrorKind.Offline, GatewayErrorCode.NetworkError, null, "网络连接中断", true)]
    [InlineData(GatewayErrorKind.Server, GatewayErrorCode.ServerError, 500, "服务器内部错误", true)]
    [InlineData(GatewayErrorKind.Protocol, GatewayErrorCode.InvalidResponse, null, "数据异常", true)]
    public async Task StartNewChannelConversation_WhenCreationFails_ShowsReasonAndPreservesDraftWithoutRetry(
        GatewayErrorKind kind, GatewayErrorCode code, int? status, string expectedReason, bool wrapped)
    {
        var calls = 0;
        var session = CreateGroupCreationSession();
        var gateway = new GatewayException(kind, code, status);
        session.CreatePrivateGroupAction = (_, _) =>
        {
            calls++;
            return Task.FromException<PrivateGroupCreated>(wrapped
                ? new InvalidOperationException("群聊创建结果无法确认；已刷新权威会话列表，请先检查是否已创建，勿直接重试。", gateway)
                : gateway);
        };
        using var viewModel = CreateViewModel(session);
        viewModel.ShowNewChannelConversationCommand.Execute(null);
        viewModel.NewPrivateGroupName = "产品设计群";
        foreach (var choice in viewModel.NewConversationChoices) choice.IsSelected = true;
        var notifications = new List<string?>();
        viewModel.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);

        await ((IAsyncRelayCommand)viewModel.StartNewChannelConversationCommand).ExecuteAsync(null);

        Assert.Contains(expectedReason, viewModel.NewConversationError);
        Assert.True(viewModel.HasNewConversationError);
        Assert.Contains(nameof(ShellViewModel.HasNewConversationError), notifications);
        if (status is not null) Assert.Contains($"HTTP {status}", viewModel.NewConversationError);
        if (kind is GatewayErrorKind.Offline or GatewayErrorKind.Server or GatewayErrorKind.Protocol)
        {
            Assert.Contains("结果无法确认", viewModel.NewConversationError);
            Assert.Contains("勿直接重试", viewModel.NewConversationError);
        }
        Assert.Equal(1, calls);
        Assert.True(viewModel.IsNewConversationOpen);
        Assert.Equal("产品设计群", viewModel.NewPrivateGroupName);
        Assert.All(viewModel.NewConversationChoices, choice => Assert.True(choice.IsSelected));
        Assert.Null(session.SelectedConversation);
    }

    [Theory]
    [InlineData("members", "刷新联系人")]
    [InlineData("arguments", "至少选择两名")]
    [InlineData("cancelled", "创建已取消")]
    [InlineData("unexpected", "群聊创建失败")]
    public async Task StartNewChannelConversation_WhenLocalFailureOccurs_ShowsSafeActionableMessage(string failure, string expectedReason)
    {
        const string privateDetail = "untrusted-private-detail";
        Exception error = failure switch
        {
            "members" => new InvalidOperationException("Refresh the active user directory before creating this group."),
            "arguments" => new ArgumentException(privateDetail),
            "cancelled" => new OperationCanceledException(privateDetail),
            _ => new Exception(privateDetail)
        };
        var session = CreateGroupCreationSession();
        session.CreatePrivateGroupAction = (_, _) => Task.FromException<PrivateGroupCreated>(error);
        using var viewModel = CreateViewModel(session);
        viewModel.ShowNewChannelConversationCommand.Execute(null);
        viewModel.NewPrivateGroupName = "产品设计群";
        foreach (var choice in viewModel.NewConversationChoices) choice.IsSelected = true;

        await ((IAsyncRelayCommand)viewModel.StartNewChannelConversationCommand).ExecuteAsync(null);

        Assert.Contains(expectedReason, viewModel.NewConversationError);
        Assert.DoesNotContain(privateDetail, viewModel.NewConversationError);
        Assert.True(viewModel.HasNewConversationError);
        Assert.True(viewModel.IsNewConversationOpen);
    }

    private static FakeSession CreateGroupCreationSession() => new()
    {
        CurrentUserId = 7,
        StateValue = new ClientState(
            users: new Dictionary<long, UserProfile>
            {
                [7] = new UserProfile(7, "Ada"),
                [8] = new UserProfile(8, "Bea"),
                [9] = new UserProfile(9, "Chen")
            },
            connection: new ConnectionState(ConnectionStatus.Connected))
    };

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
        Assert.True(viewModel.IsPrimaryShellEnabled);
    }

    [Theory]
    [InlineData(640)]
    [InlineData(1024)]
    public async Task ToggleDetails_WhenOverlayOpens_KeepsBackgroundEnabledAndRetainsModalState(int width)
    {
        var session = new FakeSession
        {
            Selected = new DirectMessage([8]),
            StateValue = new ClientState(users: new Dictionary<long, UserProfile> { [8] = new(8, "Bea") })
        };
        using var viewModel = CreateViewModel(session);
        viewModel.UpdateViewport(width);
        var enabledStates = new List<bool>();
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ShellViewModel.IsPrimaryShellEnabled))
                enabledStates.Add(viewModel.IsPrimaryShellEnabled);
        };

        await viewModel.ToggleDetailsCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsOverlayDetailsVisible);
        Assert.True(viewModel.IsModalOverlayVisible);
        Assert.True(viewModel.IsPrimaryShellEnabled);
        Assert.NotEmpty(enabledStates);
        Assert.All(enabledStates, enabled => Assert.True(enabled));

        await viewModel.ToggleDetailsCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsOverlayDetailsVisible);
        Assert.False(viewModel.IsModalOverlayVisible);
        Assert.True(viewModel.IsPrimaryShellEnabled);
        Assert.All(enabledStates, enabled => Assert.True(enabled));
    }

    [Fact]
    public void DetailsOverlay_WhenLogoutConfirmationAlsoOpens_PreservesConfirmationBlocking()
    {
        using var viewModel = CreateViewModel(new FakeSession());
        viewModel.UpdateViewport(1024);
        viewModel.IsDetailsOpen = true;
        Assert.True(viewModel.IsPrimaryShellEnabled);

        viewModel.LogoutConfirmationVisible = true;
        Assert.True(viewModel.IsModalOverlayVisible);
        Assert.False(viewModel.IsPrimaryShellEnabled);

        viewModel.LogoutConfirmationVisible = false;
        Assert.True(viewModel.IsPrimaryShellEnabled);
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
    public void OwnStatus_WhenPresenceAndPersonalStatusAreKnown_ProjectsOnlyPresence()
    {
        var session = new FakeSession
        {
            StateValue = new ClientState(connection: new ConnectionState(ConnectionStatus.Connected)),
            Account = AccountId.Create(RealmEndpoint.Parse("https://chat.example.test"), 7),
            CurrentUserId = 7,
            CanSetOwnPresenceValue = false,
            OwnPresenceStatusValue = UserPresenceStatus.Active,
            CanSetOwnUserStatusValue = false,
            IsOwnUserStatusConfirmedValue = true,
            OwnUserStatusValue = new UserStatusContent(
                "会议中",
                new EmojiReactionIdentity("calendar", "1f4c5", "unicode_emoji"))
        };
        using var viewModel = CreateViewModel(session);

        Assert.True(viewModel.HasOwnPresenceStatus);
        Assert.True(viewModel.IsOwnPresenceOnline);
        Assert.True(viewModel.HasOwnStatusSummary);
        Assert.Equal("在线", viewModel.OwnStatusSummary);
        Assert.Equal("在线状态：在线", viewModel.OwnPresenceStatusText);

        session.OwnUserStatusValue = new UserStatusContent("在办公室");
        session.Publish();

        Assert.Equal("在线", viewModel.OwnStatusSummary);
    }

    [Fact]
    public void OwnStatus_WhenOnlyPersonalStatusIsKnown_HidesSummary()
    {
        var session = new FakeSession
        {
            StateValue = new ClientState(connection: new ConnectionState(ConnectionStatus.Connected)),
            Account = AccountId.Create(RealmEndpoint.Parse("https://chat.example.test"), 7),
            CurrentUserId = 7,
            CanSetOwnPresenceValue = true,
            CanSetOwnUserStatusValue = true,
            IsOwnUserStatusConfirmedValue = true,
            OwnUserStatusValue = new UserStatusContent("在办公室")
        };
        using var viewModel = CreateViewModel(session);

        Assert.False(viewModel.HasOwnStatusSummary);
        Assert.Equal(string.Empty, viewModel.OwnStatusSummary);
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
    public void OpenSearch_WhenInvoked_KeepsBackgroundControlsEnabledWhileModalIsOpen()
    {
        using var viewModel = CreateViewModel(new FakeSession());

        viewModel.OpenSearchCommand.Execute(null);

        Assert.True(viewModel.IsSearchOpen);
        Assert.True(viewModel.IsModalOverlayVisible);
        Assert.True(viewModel.IsPrimaryShellEnabled);

        viewModel.CloseSearchCommand.Execute(null);

        Assert.False(viewModel.IsModalOverlayVisible);
        Assert.True(viewModel.IsPrimaryShellEnabled);
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
        using var viewModel = CreateViewModel(CreateCustomEmojiSession());
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
        var choices = EmojiCatalog.CreateChoices();

        Assert.Equal(1883, choices.Count);
        Assert.Equal(
            choices.Count,
            choices.Select(choice => choice.EmojiCode).Distinct(StringComparer.Ordinal).Count());
        Assert.All(choices, choice => Assert.Equal("unicode_emoji", choice.ReactionType));
        Assert.Contains(choices, choice =>
            choice.Emoji == "❤️" &&
            choice.EmojiName == "heart" &&
            choice.EmojiCode == "2764");
        Assert.Contains(choices, choice =>
            choice.Emoji == "🫠" &&
            choice.EmojiName == "melting_face" &&
            choice.EmojiCode == "1fae0");
        Assert.Contains(choices, choice =>
            choice.Emoji == "🫡" &&
            choice.EmojiName == "saluting_face" &&
            choice.EmojiCode == "1fae1");
        Assert.Contains(choices, choice =>
            choice.Emoji == "🇨🇳" &&
            choice.EmojiName == "flag_china" &&
            choice.EmojiCode == "1f1e8-1f1f3");
        Assert.Contains(choices, choice =>
            choice.Emoji == "👩‍💻" &&
            choice.EmojiName == "woman_technologist" &&
            choice.EmojiCode == "1f469-200d-1f4bb");
    }

    [Fact]
    public void EmojiPickers_WhenRealmHasCustomEmoji_ShowOnlyActiveCustomImages()
    {
        using var viewModel = CreateViewModel(CreateCustomEmojiSession());
        var category = Assert.Single(viewModel.EmojiCategories);
        Assert.Equal("自定义", category.Label);
        Assert.True(category.IsSelected);
        Assert.Equal(2, viewModel.VisibleEmojiChoices.Count);
        Assert.Equal(viewModel.EmojiChoices, viewModel.VisibleEmojiChoices);
        Assert.All(viewModel.VisibleEmojiChoices, choice => Assert.Equal("realm_emoji", choice.ReactionType));
        var choice = viewModel.VisibleEmojiChoices[0];
        Assert.Equal(new EmojiReactionIdentity("party", "1", "realm_emoji"), choice.Identity);
        Assert.Equal("/user_avatars/1/emoji/images/1-still.png", choice.SourceUrl);
        Assert.Equal(":party:", choice.Emoji);
    }

    [Fact]
    public void SessionStateChanged_WhenConfirmedAndOutboxContainCustomEmoji_ProjectsImagesForBoth()
    {
        var conversation = new DirectMessage([20]);
        var session = CreateCustomEmojiSession();
        session.Selected = conversation;
        session.CurrentUserId = 7;
        session.StateValue = session.StateValue with
        {
            Messages = new Dictionary<long, ChatMessage>
            {
                [42] = new(42, conversation, 7, "sent :party:", DateTimeOffset.UnixEpoch)
            },
            Outbox = new Dictionary<string, OutboxEntry>
            {
                ["pending"] = new("pending", conversation, "pending :rocket:", DateTimeOffset.UnixEpoch, OutboxState.Waiting)
            }
        };
        using var viewModel = CreateViewModel(session);

        Assert.Equal(2, viewModel.Messages.Count);
        Assert.All(viewModel.Messages, message =>
        {
            Assert.True(message.HasCustomEmoji);
            Assert.False(message.HasPlainBody);
            Assert.Single(message.BodyRuns, run => run.EmojiSourceUrl is not null);
        });
    }

    [Theory]
    [InlineData(1200, 420)]
    [InlineData(500, 420)]
    [InlineData(400, 376)]
    public void UpdateViewport_WhenEmojiPickerIsCompact_KeepsColumnsWithinAvailableWidth(double width, double expected)
    {
        using var viewModel = CreateViewModel(CreateCustomEmojiSession());
        viewModel.UpdateViewport(width);

        Assert.Equal(expected, viewModel.EmojiPickerWidth);
        Assert.True(viewModel.EmojiPickerColumns * 28 + 16 <= viewModel.EmojiPickerContentWidth);
        Assert.True(viewModel.ComposerEmojiPickerColumns * 34 + 16 <= viewModel.EmojiPickerContentWidth);
        Assert.Equal(392d, viewModel.DefaultEmojiPickerHeight);
        Assert.True(viewModel.EmojiPickerWidth <= width - 24);
    }

    [Theory]
    [InlineData(1440, 900)]
    [InlineData(900, 500)]
    [InlineData(500, 400)]
    [InlineData(280, 300)]
    public void UpdateViewport_WhenWindowIsSmall_KeepsEmojiCellsAndPopoverInsideViewport(double width, double height)
    {
        using var viewModel = CreateViewModel(CreateCustomEmojiSession());
        viewModel.UpdateViewport(width, height);
        var position = RelayCove.App.Platforms.Windows.Behaviors.PopoverAnchorBehavior.CalculatePosition(
            width - 5, height - 5, viewModel.EmojiPickerWidth, viewModel.EmojiPickerHeight, width, height);

        Assert.True(position.X >= 12 && position.X + viewModel.EmojiPickerWidth <= width - 12);
        Assert.True(position.Y >= 12 && position.Y + viewModel.EmojiPickerHeight <= height - 12);
        Assert.True((viewModel.EmojiPickerContentWidth - 16) / viewModel.EmojiPickerColumns >= 28);
        Assert.True((viewModel.EmojiPickerContentWidth - 16) / viewModel.ComposerEmojiPickerColumns >= 34);
        Assert.True(viewModel.DefaultEmojiPickerHeight <= 392d);
        Assert.True(viewModel.DefaultEmojiPickerHeight <= height - 24d);
        Assert.True(viewModel.EmojiPickerHeight > 100);
        Assert.Equal(2, viewModel.VisibleEmojiChoices.Count);
    }

    [Fact]
    public void EmojiPickers_WhenRealmHasNoCustomEmoji_DoNotFallBackToUnicode()
    {
        using var viewModel = CreateViewModel(new FakeSession());
        Assert.Empty(viewModel.EmojiChoices);
        Assert.Empty(viewModel.VisibleEmojiChoices);
    }

    [Fact]
    public void SessionStateChanged_WhenCustomEmojiIsInConversationSummary_PreservesTokenAndPublishesCatalogChanges()
    {
        var session = CreateCustomEmojiSession();
        var conversation = new DirectMessage([20]);
        session.Selected = conversation;
        session.Recent = [conversation];
        session.StateValue = session.StateValue with
        {
            Messages = new Dictionary<long, ChatMessage>
            {
                [42] = new(42, conversation, 20, "出发 :rocket:", DateTimeOffset.UnixEpoch)
            }
        };
        using var viewModel = CreateViewModel(session);
        Assert.Equal("出发 :rocket:", Assert.Single(viewModel.Conversations).Detail);
        Assert.Single(EmojiShortcodeCatalog.CreateRuns(viewModel.Conversations[0].Detail!, viewModel.RealmEmojis),
            run => run.EmojiSourceUrl is not null);
        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        session.StateValue = session.StateValue with { RealmEmojis = new Dictionary<string, RealmEmoji>() };
        session.Publish();

        Assert.Contains(nameof(ShellViewModel.RealmEmojis), changed);
        Assert.Empty(viewModel.RealmEmojis);
    }

    [Fact]
    public void EmojiPickers_WhenCustomEmojiChanges_RefreshAndRejectStaleChoice()
    {
        var session = CreateCustomEmojiSession();
        using var viewModel = CreateViewModel(session);
        var stale = viewModel.EmojiChoices[0];
        viewModel.SelectedComposerEmoji = stale;
        session.StateValue = DomainReducer.Apply(session.StateValue,
            new RealmEmojiUpdatedEvent([new("3", "new_one", "/user_avatars/1/emoji/images/3.png", false)], 1));
        session.Publish();

        Assert.Equal("new_one", Assert.Single(viewModel.VisibleEmojiChoices).EmojiName);
        Assert.Null(viewModel.SelectedComposerEmoji);
        viewModel.InsertComposerEmojiCommand.Execute(stale);
        Assert.Empty(viewModel.ComposerText);
        session.StateValue = ClientState.Empty;
        session.Publish();
        Assert.Empty(viewModel.VisibleEmojiChoices);
    }

    private static FakeSession CreateCustomEmojiSession() => new()
    {
        StateValue = new ClientState
        {
            RealmEmojis = new Dictionary<string, RealmEmoji>
            {
                ["1"] = new("1", "party", "/user_avatars/1/emoji/images/1.gif", false, "/user_avatars/1/emoji/images/1-still.png"),
                ["2"] = new("2", "rocket", "/user_avatars/1/emoji/images/2.png", false),
                ["9"] = new("9", "old", "/user_avatars/1/emoji/images/9.png", true)
            }
        }
    };

    [Fact]
    public async Task SelectReactionEmoji_WhenCustomChoiceIsSelected_SendsRealmIdentityOnce()
    {
        var session = CreateCustomEmojiSession();
        session.Selected = new DirectMessage([20]);
        session.CurrentUserId = 7;
        session.Recent = [session.Selected];
        session.StateValue = session.StateValue with
        {
            Connection = new ConnectionState(ConnectionStatus.Connected),
            Users = new Dictionary<long, UserProfile> { [20] = new(20, "Bea") },
            Messages = new Dictionary<long, ChatMessage>
            {
                [42] = new(42, session.Selected, 20, "hello", DateTimeOffset.UnixEpoch)
            }
        };
        using var viewModel = CreateViewModel(session);
        viewModel.ActivateConversation(Assert.Single(viewModel.Conversations));
        await WaitUntilAsync(() => viewModel.CanCompose);
        var message = Assert.Single(viewModel.Messages);
        viewModel.OpenReactionPickerAtCommand.Execute(new ReactionPickerRequest(message, 100, 100));

        await viewModel.SelectReactionEmojiCommand.ExecuteAsync(viewModel.EmojiChoices[0]);

        var reaction = Assert.Single(session.ReactionCalls);
        Assert.Equal(42, reaction.MessageId);
        Assert.Equal(new EmojiReactionIdentity("party", "1", "realm_emoji"), reaction.Identity);
        Assert.True(reaction.Add);
        Assert.False(viewModel.IsReactionPickerOpen);
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
    public async Task InitializeAsync_WhenCacheIsAvailable_SelectsItBeforeBackgroundHistoryCompletes()
    {
        var conversation = new DirectMessage([8]);
        var cached = new ChatMessage(10, conversation, 8, "cached", DateTimeOffset.UnixEpoch, isRead: true);
        var selectionCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new FakeSession
        {
            Recent = [conversation],
            StateValue = new ClientState(connection: new ConnectionState(ConnectionStatus.Offline, "cache_first"))
        };
        session.SelectAction = (selected, _) =>
        {
            session.Selected = selected;
            session.HistoryState = new ConversationHistoryState(selected, 1, true, false, false, null, null);
            session.Publish();
            return selectionCompleted.Task;
        };
        using var viewModel = CreateViewModel(session);

        await viewModel.InitializeAsync();
        Assert.Equal(conversation, session.SelectedConversation);
        Assert.True(viewModel.IsNavigationPending);
        session.StateValue = session.StateValue with { Messages = new Dictionary<long, ChatMessage> { [10] = cached } };
        session.Publish();
        var row = Assert.Single(viewModel.Messages);
        var scroll = Assert.IsType<MessageScrollRequest>(viewModel.PendingMessageScrollRequest);
        Assert.Equal(10, scroll.TargetMessageId);
        Assert.False(viewModel.ShowConversationLoadingIndicator);
        var changes = new List<NotifyCollectionChangedAction>();
        viewModel.Messages.CollectionChanged += (_, args) => changes.Add(args.Action);
        viewModel.AcknowledgeMessageScrollRequest(scroll);

        session.StateValue = session.StateValue with
        {
            Connection = new ConnectionState(ConnectionStatus.Connected),
            Messages = new Dictionary<long, ChatMessage> { [10] = cached with { Reactions = [] } }
        };
        session.HistoryState = session.HistoryState with { IsLoading = false, FoundOldest = true };
        session.Publish();
        selectionCompleted.SetResult();
        await WaitUntilAsync(() => !viewModel.IsNavigationPending);

        Assert.Same(row, Assert.Single(viewModel.Messages));
        Assert.Empty(changes);
        Assert.Null(viewModel.PendingMessageScrollRequest);
    }

    [Fact]
    public async Task InitializeAsync_WhenSelectedCachePageIsEmpty_PositionsFirstNetworkPageAtLatest()
    {
        var conversation = new DirectMessage([8]);
        var session = new FakeSession
        {
            Recent = [conversation],
            StateValue = new ClientState(connection: new ConnectionState(ConnectionStatus.Offline))
        };
        using var viewModel = CreateViewModel(session);

        await viewModel.InitializeAsync();
        Assert.Equal(conversation, session.SelectedConversation);
        Assert.Empty(viewModel.Messages);
        Assert.False(viewModel.IsNavigationPending);
        session.HistoryState = session.HistoryState with { IsLoading = true };
        session.StateValue = session.StateValue with { Connection = new ConnectionState(ConnectionStatus.Connected) };
        session.Publish();
        session.StateValue = session.StateValue with
        {
            Messages = Enumerable.Range(1, 50).ToDictionary(
                id => (long)id,
                id => new ChatMessage(id, conversation, 8, "message", DateTimeOffset.UnixEpoch.AddSeconds(id), isRead: true))
        };
        session.Publish();
        session.HistoryState = session.HistoryState with { IsLoading = false };
        session.Publish();

        var scroll = Assert.IsType<MessageScrollRequest>(viewModel.PendingMessageScrollRequest);
        Assert.Equal(50, scroll.TargetMessageId);
        Assert.Equal(MessageScrollReason.ConversationActivated, scroll.Reason);
    }

    [Fact]
    public async Task InitializeAsync_WhenCacheHasNoConversations_SelectsFirstConversationWhenRegisterArrives()
    {
        var conversation = new DirectMessage([8]);
        var session = new FakeSession
        {
            StateValue = new ClientState(connection: new ConnectionState(ConnectionStatus.Offline, "cache_first"))
        };
        using var viewModel = CreateViewModel(session);

        await viewModel.InitializeAsync();
        Assert.Null(session.SelectedConversation);
        session.Recent = [conversation];
        session.StateValue = session.StateValue with { Connection = new ConnectionState(ConnectionStatus.Connected) };
        session.Publish();

        Assert.Equal(conversation, session.SelectedConversation);
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

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ToggleMessageStarCommand_WhenInvoked_ClosesMenuBeforeRequestCompletes(bool isStarred, bool fails)
    {
        var response = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new FakeSession();
        using var viewModel = CreateViewModel(session);
        var message = new MessageItem("message-1", 1, 7, "Ada", "hello", "10:00", isStarred: isStarred);
        var menuClosedAtRequest = false;
        var calls = new List<(long MessageId, bool IsStarred)>();
        session.SetMessageStarredAction = (messageId, starred, _) =>
        {
            menuClosedAtRequest = !viewModel.IsMessageMenuOpen && viewModel.ActiveMessageAction is null;
            calls.Add((messageId, starred));
            return response.Task;
        };
        viewModel.OpenMessageMenuCommand.Execute(message);

        var toggle = viewModel.ToggleMessageStarCommand.ExecuteAsync(null);

        Assert.True(menuClosedAtRequest);
        Assert.False(viewModel.IsMessageMenuOpen);
        Assert.False(toggle.IsCompleted);
        Assert.Equal((1L, !isStarred), Assert.Single(calls));
        Assert.Equal(isStarred, message.IsStarred);
        Assert.Equal("hello", message.Content);
        if (fails)
            response.SetException(new GatewayException(GatewayErrorKind.RequestFailed, GatewayErrorCode.RequestFailed, 403));
        else
            response.SetResult();
        await toggle.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(viewModel.IsMessageMenuOpen);
        Assert.Equal(fails, !string.IsNullOrWhiteSpace(viewModel.LoginError));
        Assert.Single(calls);
    }

    [Theory]
    [InlineData(false, MessageMutationStatus.Submitting, false)]
    [InlineData(true, MessageMutationStatus.Submitting, false)]
    [InlineData(false, MessageMutationStatus.Failed, true)]
    [InlineData(true, MessageMutationStatus.Failed, true)]
    [InlineData(false, MessageMutationStatus.Uncertain, true)]
    [InlineData(true, MessageMutationStatus.Uncertain, true)]
    public void MessageStarMutation_WhenProjected_ShowsOnlyFailureOrUnconfirmedResult(
        bool isStarred, MessageMutationStatus status, bool showsStatus)
    {
        var conversation = new DirectMessage([8]);
        var message = new ChatMessage(10, conversation, 8, "hello", DateTimeOffset.UnixEpoch, isStarred: isStarred);
        var session = new FakeSession
        {
            Selected = conversation,
            StateValue = new ClientState(
                connection: new ConnectionState(ConnectionStatus.Connected),
                messages: new Dictionary<long, ChatMessage> { [10] = message },
                messageMutations: new Dictionary<long, MessageMutationState>
                {
                    [10] = new(10, MessageMutationKind.Star, status)
                })
        };
        using var viewModel = CreateViewModel(session);
        var row = Assert.Single(viewModel.Messages);

        Assert.Equal(showsStatus, row.HasMutationState);
        Assert.Equal(status != MessageMutationStatus.Failed, row.MutationBlocksActions);
        Assert.Equal(isStarred, row.IsStarred);
        Assert.Equal("hello", row.Body);

        session.StateValue = session.StateValue with { MessageMutations = new Dictionary<long, MessageMutationState>() };
        session.Publish();

        Assert.Same(row, Assert.Single(viewModel.Messages));
        Assert.False(row.HasMutationState);
        Assert.True(row.CanMutate);
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
    public async Task CopyActiveMessage_WhenTextWasSelected_CopiesTheSelectionAndClosesMenu()
    {
        var interactions = new FakePlatformInteractionService();
        using var viewModel = CreateViewModel(new FakeSession(), platformInteractions: interactions);
        var message = new MessageItem("message-1", 1, 7, "Ada", "第一行\n第二行", "10:00");

        viewModel.OpenMessageMenuAtCommand.Execute(new MessageMenuRequest(message, 812.5d, 244d, "第二行"));
        await viewModel.CopyActiveMessageCommand.ExecuteAsync(null);

        Assert.Equal(["第二行"], interactions.Copied);
        Assert.False(viewModel.IsMessageMenuOpen);
        Assert.Null(viewModel.ActiveMessageAction);
    }

    [Fact]
    public async Task CopyActiveMessage_WhenNothingIsSelected_CopiesTheFullDisplayedBodyAndPreservesLineBreaks()
    {
        var interactions = new FakePlatformInteractionService();
        using var viewModel = CreateViewModel(new FakeSession(), platformInteractions: interactions);
        var message = new MessageItem("message-1", 1, 7, "Ada", "第一行\n第二行", "10:00");

        viewModel.OpenMessageMenuAtCommand.Execute(new MessageMenuRequest(message, 812.5d, 244d));
        await viewModel.CopyActiveMessageCommand.ExecuteAsync(null);

        Assert.Equal(["第一行\n第二行"], interactions.Copied);
    }

    [Fact]
    public async Task CopyActiveMessage_WhenMenuMovesToAnotherMessage_DoesNotReuseThePreviousSelection()
    {
        var interactions = new FakePlatformInteractionService();
        using var viewModel = CreateViewModel(new FakeSession(), platformInteractions: interactions);
        var first = new MessageItem("message-1", 1, 7, "Ada", "第一条消息", "10:00");
        var second = new MessageItem("message-2", 2, 8, "Bea", "第二条消息", "10:01");

        viewModel.OpenMessageMenuAtCommand.Execute(new MessageMenuRequest(first, 812.5d, 244d, "第一条"));
        viewModel.OpenMessageMenuAtCommand.Execute(new MessageMenuRequest(second, 400d, 520d));
        await viewModel.CopyActiveMessageCommand.ExecuteAsync(null);

        Assert.Equal(["第二条消息"], interactions.Copied);
    }

    [Fact]
    public async Task CopyActiveMessage_WhenClipboardFails_ClosesMenuWithoutShowingABanner()
    {
        var interactions = new FakePlatformInteractionService
        {
            CopyTextAction = _ => Task.FromException(new InvalidOperationException("Clipboard unavailable."))
        };
        using var viewModel = CreateViewModel(new FakeSession(), platformInteractions: interactions);
        var message = new MessageItem("message-1", 1, 7, "Ada", "正文", "10:00");

        viewModel.OpenMessageMenuAtCommand.Execute(new MessageMenuRequest(message, 812.5d, 244d));
        await viewModel.CopyActiveMessageCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsMessageMenuOpen);
        Assert.Null(viewModel.ActiveMessageAction);
        Assert.Null(viewModel.MediaActionStatus);
    }

    [Fact]
    public void OpenMessageMenuAtCommand_WhenMessageHasNoText_HidesCopyAction()
    {
        using var viewModel = CreateViewModel(new FakeSession());
        var message = new MessageItem(
            "message-1",
            1,
            7,
            "Ada",
            "![图片](/user_uploads/image.png)",
            "10:00",
            realm: RealmEndpoint.Parse("https://chat.example.test/"));

        viewModel.OpenMessageMenuAtCommand.Execute(new MessageMenuRequest(message, 812.5d, 244d));

        Assert.False(viewModel.CanCopyActiveMessage);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void OpenMessageMenuAtCommand_WhenAnotherMessageMenuIsOpen_ReplacesTargetAndImageAtCurrentPointer(
        bool firstIsImage, bool secondIsImage)
    {
        using var viewModel = CreateViewModel(new FakeSession());
        var first = new MessageItem("message-1", 1, 7, "Ada", "first", "10:00");
        var second = new MessageItem("message-2", 2, 8, "Bea", "second", "10:01", isStarred: true);
        var firstImage = new MessageAttachmentItem("image", "first.png", "/user_uploads/7/first.png");
        var secondImage = new MessageAttachmentItem("image", "second.png", "/user_uploads/8/second.png");

        if (firstIsImage)
            viewModel.OpenImageAttachmentMenuAtCommand.Execute(new ImageAttachmentMenuRequest(first, firstImage, 812d, 244d));
        else
            viewModel.OpenMessageMenuAtCommand.Execute(new MessageMenuRequest(first, 812d, 244d));
        Assert.True(viewModel.IsMessageMenuOpen);

        if (secondIsImage)
            viewModel.OpenImageAttachmentMenuAtCommand.Execute(new ImageAttachmentMenuRequest(second, secondImage, 400d, 520d));
        else
            viewModel.OpenMessageMenuAtCommand.Execute(new MessageMenuRequest(second, 400d, 520d));

        Assert.True(viewModel.IsMessageMenuOpen);
        Assert.Same(second, viewModel.ActiveMessageAction);
        Assert.Equal("取消收藏", viewModel.ActiveMessageStarActionLabel);
        Assert.Same(secondIsImage ? secondImage : null, viewModel.ActiveMessageAttachment);
        Assert.Equal(secondIsImage, viewModel.HasActiveMessageAttachment);
        Assert.Equal(400d, viewModel.MessageMenuAnchorX);
        Assert.Equal(520d, viewModel.MessageMenuAnchorY);
    }

    [Theory]
    [InlineData(false, 812.5d, 244d)]
    [InlineData(false, 400d, 520d)]
    [InlineData(true, 812.5d, 244d)]
    [InlineData(true, 400d, 520d)]
    public void OpenMessageMenuAtCommand_WhenDismissed_CanReopenRepeatedlyAtCurrentPointer(
        bool isImage, double pointerX, double pointerY)
    {
        using var viewModel = CreateViewModel(new FakeSession());
        var message = new MessageItem("message-1", 1, 7, "Ada", "hello", "10:00", isOwn: true);
        var image = new MessageAttachmentItem("image", "preview.png", "/user_uploads/7/preview.png");
        viewModel.OpenMessageMenuAtCommand.Execute(new MessageMenuRequest(message, 812.5d, 244d));

        for (var attempt = 0; attempt < 3; attempt++)
        {
            viewModel.CloseMessageMenuCommand.Execute(null);
            Assert.False(viewModel.IsMessageMenuOpen);
            Assert.Null(viewModel.ActiveMessageAction);
            Assert.Null(viewModel.ActiveMessageAttachment);

            if (isImage)
                viewModel.OpenImageAttachmentMenuAtCommand.Execute(new ImageAttachmentMenuRequest(message, image, pointerX, pointerY));
            else
                viewModel.OpenMessageMenuAtCommand.Execute(new MessageMenuRequest(message, pointerX, pointerY));

            Assert.True(viewModel.IsMessageMenuOpen);
            Assert.Same(message, viewModel.ActiveMessageAction);
            Assert.Equal(isImage, viewModel.HasActiveMessageAttachment);
            Assert.Equal(pointerX, viewModel.MessageMenuAnchorX);
            Assert.Equal(pointerY, viewModel.MessageMenuAnchorY);
        }
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
        Assert.True(viewModel.CanSend);
        Assert.True(viewModel.SendCommand.CanExecute(null));

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

    [Theory]
    [InlineData("picker", "", false)]
    [InlineData("picker", "caption", true)]
    [InlineData("drop", "caption", false)]
    [InlineData("drop", "", true)]
    [InlineData("screenshot", "", false)]
    [InlineData("screenshot", "caption", true)]
    public async Task SendCommand_WhenAttachmentConfirmationIsPending_ClearsSubmittedDraftAndPreservesNewDraft(
        string source,
        string caption,
        bool switchConversation)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var conversation = new DirectMessage([8]);
        var otherConversation = new DirectMessage([9]);
        var isImage = source == "screenshot";
        var file = new SelectedAttachmentFile(
            isImage ? "screenshot.png" : "notes.txt",
            isImage ? "image/png" : "text/plain",
            3,
            _ => Task.FromResult<Stream>(new MemoryStream([1, 2, 3])),
            openPreviewStream: isImage ? () => new MemoryStream([1, 2, 3]) : null);
        var session = new FakeSession
        {
            Selected = conversation,
            Recent = [conversation, otherConversation],
            CurrentUserId = 7,
            ActiveRealm = RealmEndpoint.Parse("https://example.test"),
            StateValue = new ClientState(connection: new ConnectionState(ConnectionStatus.Connected))
        };
        session.SendAction = async (content, cancellationToken) =>
        {
            session.StateValue = DomainReducer.Apply(session.StateValue, new OutboxQueuedEvent(
                new OutboxEntry("submitted", conversation, content, DateTimeOffset.UnixEpoch, OutboxState.Hidden)));
            session.Publish();
            await release.Task.WaitAsync(cancellationToken);
        };
        using var viewModel = CreateViewModel(session, fileSelectionService: new FakeFileSelectionService { Files = [file] });
        session.Publish();
        if (source == "picker") await viewModel.PickAttachmentsCommand.ExecuteAsync(null);
        else if (source == "drop") viewModel.AddDroppedAttachmentsCommand.Execute(new[] { file });
        else viewModel.AddPastedImageCommand.Execute(file);
        await WaitUntilAsync(() => viewModel.Attachments.Single().Status == AttachmentUploadStatus.Uploaded);
        viewModel.ComposerText = caption;
        var expectedContent = (caption.Length > 0 ? caption + "\n" : string.Empty) +
            $"{(isImage ? "!" : string.Empty)}[{file.FileName}](https://example.test/user_uploads/{file.FileName})";

        var send = viewModel.SendCommand.ExecuteAsync(null);
        try
        {
            Assert.False(send.IsCompleted);
            Assert.Empty(viewModel.ComposerText);
            Assert.Empty(viewModel.Attachments);
            Assert.False(viewModel.HasAttachments);
            Assert.Equal(viewModel.ComposerHeight, viewModel.ComposerDisplayHeight);
            Assert.Equal(expectedContent, Assert.Single(session.SentContents));
            var pending = Assert.Single(viewModel.Messages);
            Assert.Equal(expectedContent, pending.Content);
            Assert.Single(pending.Attachments);
            Assert.False(pending.HasDeliveryState);
            Assert.False(pending.CanRecover);

            if (switchConversation)
            {
                session.Selected = otherConversation;
                session.Publish();
            }
            viewModel.ComposerText = "next draft";
            viewModel.AddDroppedAttachmentsCommand.Execute(new[]
            {
                new SelectedAttachmentFile("next.txt", "text/plain", 1,
                    _ => Task.FromResult<Stream>(new MemoryStream([4])))
            });
            await WaitUntilAsync(() => viewModel.Attachments.Single().Status == AttachmentUploadStatus.Uploaded);
            var nextAttachment = Assert.Single(viewModel.Attachments);
            release.SetResult();
            await send;

            Assert.Equal("next draft", viewModel.ComposerText);
            Assert.Same(nextAttachment, Assert.Single(viewModel.Attachments));
            session.Selected = switchConversation ? conversation : otherConversation;
            session.Publish();
            Assert.Empty(viewModel.ComposerText);
            Assert.Empty(viewModel.Attachments);
            session.Selected = switchConversation ? otherConversation : conversation;
            session.Publish();
            Assert.Equal("next draft", viewModel.ComposerText);
            Assert.Same(nextAttachment, Assert.Single(viewModel.Attachments));
            Assert.Equal(2, session.UploadCalls);
            Assert.Single(session.SentContents);
        }
        finally
        {
            release.TrySetResult();
            await send;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SendCommand_WhenRejectedBeforeOutbox_KeepsSubmittedAttachmentRecoverableWithoutReplacingNewDraft(
        bool switchConversation)
    {
        var conversation = new DirectMessage([8]);
        var otherConversation = new DirectMessage([9]);
        const string submittedContent = "caption\n![screenshot.png](https://example.test/user_uploads/screenshot.png)";
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var existing = new OutboxEntry("previous-attempt", conversation, submittedContent,
            DateTimeOffset.UnixEpoch, OutboxState.Failed, OutboxFailureKind.Rejected);
        var session = new FakeSession
        {
            Selected = conversation,
            Recent = [conversation, otherConversation],
            StateValue = new ClientState(
                outbox: new Dictionary<string, OutboxEntry> { [existing.LocalId] = existing },
                connection: new ConnectionState(ConnectionStatus.Connected))
        };
        session.SendAction = async (_, cancellationToken) =>
        {
            session.Publish(); // An identical older outbox item is not this submission.
            await release.Task.WaitAsync(cancellationToken);
            throw new ArgumentException("Message length exceeds the server limit.");
        };
        using var viewModel = CreateViewModel(session);
        session.Publish();
        viewModel.AddPastedImageCommand.Execute(new SelectedAttachmentFile(
            "screenshot.png", "image/png", 3,
            _ => Task.FromResult<Stream>(new MemoryStream([1, 2, 3]))));
        await WaitUntilAsync(() => viewModel.Attachments.Single().Status == AttachmentUploadStatus.Uploaded);
        viewModel.ComposerText = "caption";

        var send = viewModel.SendCommand.ExecuteAsync(null);
        try
        {
            Assert.Empty(viewModel.ComposerText);
            Assert.Empty(viewModel.Attachments);
            if (switchConversation)
            {
                session.Selected = otherConversation;
                session.Publish();
            }
            viewModel.ComposerText = "next draft";
            release.SetResult();
            await send;
            Assert.Equal("next draft", viewModel.ComposerText);
            Assert.Empty(viewModel.Attachments);
            if (switchConversation)
            {
                Assert.Empty(viewModel.Messages);
                session.Selected = conversation;
                session.Publish();
            }
            var failed = Assert.Single(viewModel.Messages, message =>
                message.Id.StartsWith("local-unsubmitted-", StringComparison.Ordinal));
            Assert.Equal(submittedContent, failed.Content);
            Assert.True(failed.IsDeliveryFailure);
            Assert.True(failed.CanRecover);
            Assert.Single(session.SentContents);
            Assert.Single(session.State.Outbox);

            viewModel.RecoverOutboxCommand.Execute(failed);
            Assert.Equal(submittedContent, viewModel.ComposerText);
            Assert.DoesNotContain(failed, viewModel.Messages);
            session.SendAction = null;
            await viewModel.SendCommand.ExecuteAsync(null);
            Assert.Equal(1, session.UploadCalls);
            Assert.Equal(new[] { submittedContent, submittedContent }, session.SentContents);
            viewModel.ComposerText = "later draft";
            viewModel.RecoverOutboxCommand.Execute(failed);
            Assert.Equal("later draft", viewModel.ComposerText);
        }
        finally
        {
            release.TrySetResult();
            await send;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SendCommand_WhenAccountChanges_DiscardsUnsubmittedMessageAndLateFailure(bool changeBeforeFailure)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new FakeSession
        {
            Selected = new DirectMessage([8]),
            StateValue = new ClientState(connection: new ConnectionState(ConnectionStatus.Connected)),
            SendAction = async (_, cancellationToken) =>
            {
                await release.Task.WaitAsync(cancellationToken);
                throw new ArgumentException("Message length exceeds the server limit.");
            }
        };
        using var viewModel = CreateViewModel(session);
        session.Publish();
        viewModel.ComposerText = "submitted draft";
        var send = viewModel.SendCommand.ExecuteAsync(null);
        MessageItem? failed = null;
        if (!changeBeforeFailure)
        {
            release.SetResult();
            await send;
            failed = Assert.Single(viewModel.Messages);
        }
        session.Account = AccountId.Create(RealmEndpoint.Parse("https://other.example.test"), 99);
        session.Publish();
        viewModel.ComposerText = "new account draft";
        if (changeBeforeFailure)
        {
            release.SetResult();
            await send;
        }
        Assert.Empty(viewModel.Messages);
        if (failed is not null) viewModel.RecoverOutboxCommand.Execute(failed);
        Assert.Equal("new account draft", viewModel.ComposerText);
        Assert.Single(session.SentContents);
    }

    [Fact]
    public async Task SendCommand_WhenRealtimeConfirmsBeforeSendThrows_DoesNotCreateUnsubmittedFailureOrRestoreDraft()
    {
        var conversation = new DirectMessage([8]);
        var session = new FakeSession
        {
            Selected = conversation,
            CurrentUserId = 7,
            StateValue = new ClientState(connection: new ConnectionState(ConnectionStatus.Connected))
        };
        session.SendAction = (content, _) =>
        {
            session.StateValue = DomainReducer.Apply(session.StateValue, new OutboxQueuedEvent(
                new OutboxEntry("submitted", conversation, content, DateTimeOffset.UnixEpoch, OutboxState.Hidden)));
            session.Publish();
            session.StateValue = DomainReducer.Apply(session.StateValue, new MessageUpsertEvent(
                new ChatMessage(101, conversation, 7, content, DateTimeOffset.UnixEpoch, isRead: true),
                Source: DomainEventSource.Realtime, LocalId: "submitted"));
            session.Publish();
            throw new OperationCanceledException();
        };
        using var viewModel = CreateViewModel(session);
        session.Publish();
        viewModel.ComposerText = "already delivered";

        await viewModel.SendCommand.ExecuteAsync(null);

        var confirmed = Assert.Single(viewModel.Messages);
        Assert.Equal(101, confirmed.MessageId);
        Assert.False(confirmed.CanRecover);
        Assert.False(confirmed.HasDeliveryState);
        Assert.Empty(viewModel.ComposerText);
        Assert.Single(session.SentContents);
        Assert.Empty(session.State.Outbox);
    }

    [Fact]
    public async Task AttachmentSelection_WhenImmediateUploadFails_DisablesSendAndKeepsCaption()
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

        await WaitUntilAsync(() => viewModel.Attachments.Single().Status == AttachmentUploadStatus.Failed);

        Assert.Equal("keep this caption", viewModel.ComposerText);
        Assert.Single(viewModel.Attachments);
        Assert.False(viewModel.CanSend);
        Assert.False(viewModel.SendCommand.CanExecute(null));
        Assert.Empty(session.SentContents);
    }

    [Fact]
    public async Task AttachmentSelection_WhenImmediateUploadResultIsUnknown_ShowsAttachmentWarningOnly()
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

        await WaitUntilAsync(() => viewModel.Attachments.Single().Status == AttachmentUploadStatus.Uncertain);

        Assert.Equal("keep this caption", viewModel.ComposerText);
        Assert.Contains("附件上传结果未知", viewModel.AttachmentError, StringComparison.Ordinal);
        Assert.Null(viewModel.LoginError);
        Assert.Equal(AttachmentUploadStatus.Uncertain, Assert.Single(viewModel.Attachments).Status);
        Assert.False(viewModel.CanSend);
        Assert.Empty(session.SentContents);
    }

    [Fact]
    public async Task SendCommand_WhenFailedAttachmentWasRemoved_DoesNotHideLaterMessageFailure()
    {
        var filePicker = new FakeFileSelectionService
        {
            Files =
            [
                new SelectedAttachmentFile(
                    "broken.txt",
                    "text/plain",
                    3,
                    _ => Task.FromResult<Stream>(new MemoryStream([1, 2, 3])))
            ]
        };
        var session = new FakeSession
        {
            Selected = new DirectMessage([8]),
            StateValue = new ClientState(connection: new ConnectionState(ConnectionStatus.Connected)),
            UploadAction = (_, _) => Task.FromException<UploadedAttachment>(new InvalidOperationException("upload failed")),
            SendAction = (_, _) => throw new GatewayException(GatewayErrorKind.Offline, GatewayErrorCode.NetworkError)
        };
        using var viewModel = CreateViewModel(session, fileSelectionService: filePicker);
        session.Publish();

        await ((IAsyncRelayCommand)viewModel.PickAttachmentsCommand).ExecuteAsync(null);
        await WaitUntilAsync(() => viewModel.Attachments.Single().Status == AttachmentUploadStatus.Failed);
        viewModel.RemoveAttachmentCommand.Execute(viewModel.Attachments.Single());
        viewModel.ComposerText = "send text after removing failed upload";

        await ((IAsyncRelayCommand)viewModel.SendCommand).ExecuteAsync(null);

        Assert.NotNull(viewModel.LoginError);
        Assert.Equal("send text after removing failed upload", Assert.Single(session.SentContents));
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
    public async Task MessageViewport_WhenBackgroundHistoryIsLoading_WaitsBeforeRequestingOlderPage()
    {
        var conversation = new DirectMessage([8]);
        var session = new FakeSession
        {
            Selected = conversation,
            HistoryState = new ConversationHistoryState(conversation, 1, true, false, true, 50, null),
            StateValue = new ClientState(connection: new ConnectionState(ConnectionStatus.Connected))
        };
        using var viewModel = CreateViewModel(session);

        await viewModel.RequestOlderFromTopInputAsync(1_000, conversation.CanonicalKey, 1);
        Assert.Equal(0, session.LoadOlderCalls);
        session.HistoryState = session.HistoryState with { IsLoading = false };
        session.Publish();
        await viewModel.RequestOlderFromTopInputAsync(1_400, conversation.CanonicalKey, 1);
        Assert.Equal(1, session.LoadOlderCalls);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task SessionStateChanged_WhenBackgroundHistoryArrives_UpdatesOnlyChangedMessages(bool nearBottom, bool hasNewMessage)
    {
        var conversation = new DirectMessage([8]);
        var cached = new ChatMessage(10, conversation, 8, "cached", DateTimeOffset.UnixEpoch, isRead: true);
        var session = new FakeSession
        {
            Selected = conversation,
            Recent = [conversation],
            HistoryState = new ConversationHistoryState(conversation, 1, false, false, true, 10, null),
            StateValue = new ClientState(
                messages: new Dictionary<long, ChatMessage> { [10] = cached },
                connection: new ConnectionState(ConnectionStatus.Offline))
        };
        using var viewModel = CreateViewModel(session);
        var row = Assert.Single(viewModel.Messages);
        viewModel.AcknowledgeMessageScrollRequest(Assert.IsType<MessageScrollRequest>(viewModel.PendingMessageScrollRequest));
        await viewModel.ReportMessageViewportAsync(0, 0, 0, 1_000,
            bottomDistanceDip: nearBottom ? 0 : 2_000, viewportHeightDip: 600);
        var changes = new List<NotifyCollectionChangedAction>();
        viewModel.Messages.CollectionChanged += (_, args) => changes.Add(args.Action);

        session.HistoryState = session.HistoryState with { IsLoading = true };
        session.StateValue = session.StateValue with { Connection = new ConnectionState(ConnectionStatus.Connected) };
        session.Publish();
        var messages = new Dictionary<long, ChatMessage> { [10] = cached with { Reactions = [] } };
        if (hasNewMessage) messages[11] = new ChatMessage(11, conversation, 8, "new", DateTimeOffset.UnixEpoch.AddMinutes(1));
        session.StateValue = session.StateValue with { Messages = messages };
        session.Publish();
        session.HistoryState = session.HistoryState with { IsLoading = false };
        session.Publish();

        Assert.Same(row, viewModel.Messages[0]);
        Assert.Equal(hasNewMessage ? [NotifyCollectionChangedAction.Add] : [], changes);
        if (nearBottom && hasNewMessage)
        {
            var follow = Assert.IsType<MessageScrollRequest>(viewModel.PendingMessageScrollRequest);
            Assert.Equal(MessageScrollReason.RealtimeFollow, follow.Reason);
            Assert.Equal(11, follow.TargetMessageId);
        }
        else
        {
            Assert.Null(viewModel.PendingMessageScrollRequest);
        }
        Assert.Equal(!nearBottom && hasNewMessage ? 1 : 0, viewModel.NewMessageCount);
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
        Assert.Contains("再次发送可能重复", message.DeliveryState, StringComparison.Ordinal);
    }

    [Fact]
    public void SessionStateChanged_WhenOutboxStatesAreProjected_HidesWaitingAndClassifiesFailureVersusUnknown()
    {
        var conversation = new DirectMessage([8]);
        var waiting = new OutboxEntry("waiting", conversation, "waiting content", DateTimeOffset.UnixEpoch, OutboxState.Waiting);
        var failed = new OutboxEntry("failed", conversation, "failed content", DateTimeOffset.UnixEpoch.AddMinutes(1), OutboxState.Failed, OutboxFailureKind.Rejected);
        var unknown = new OutboxEntry("unknown", conversation, "unknown content", DateTimeOffset.UnixEpoch.AddMinutes(2), OutboxState.WaitExpired, OutboxFailureKind.NetworkResultUnknown);
        var session = new FakeSession
        {
            Selected = conversation,
            StateValue = new ClientState(
                outbox: new Dictionary<string, OutboxEntry>
                {
                    [waiting.LocalId] = waiting,
                    [failed.LocalId] = failed,
                    [unknown.LocalId] = unknown
                },
                connection: new ConnectionState(ConnectionStatus.Connected))
        };
        using var viewModel = CreateViewModel(session);

        var rows = viewModel.Messages.ToDictionary(message => message.Content, StringComparer.Ordinal);

        Assert.False(rows["waiting content"].HasDeliveryState);
        Assert.False(rows["waiting content"].CanRecover);
        Assert.Equal("发送失败；恢复内容后可手动重试", rows["failed content"].DeliveryState);
        Assert.True(rows["failed content"].IsDeliveryFailure);
        Assert.True(rows["failed content"].CanRecover);
        Assert.Equal("发送结果未确认；再次发送可能重复", rows["unknown content"].DeliveryState);
        Assert.False(rows["unknown content"].IsDeliveryFailure);
        Assert.True(rows["unknown content"].CanRecover);
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
        using var viewModel = CreateViewModel(CreateCustomEmojiSession());
        viewModel.ComposerText = "hello xx world";
        viewModel.ComposerCursorPosition = 6;
        viewModel.ComposerSelectionLength = 2;
        var choice = viewModel.EmojiChoices.Single(item => item.EmojiName == "rocket");

        viewModel.InsertComposerEmojiCommand.Execute(choice);

        Assert.Equal("hello :rocket: world", viewModel.ComposerText);
        Assert.Equal(14, viewModel.ComposerCursorPosition);
        Assert.Equal(0, viewModel.ComposerSelectionLength);
        Assert.Equal(1, viewModel.ComposerFocusRequest);
    }

    [Fact]
    public async Task SearchQuery_WhenStateIsLoaded_ReturnsOnlyServerMessagesAndClearsOldQueryResults()
    {
        var conversation = new ChannelTopic(4, string.Empty);
        var session = new FakeSession
        {
            Account = AccountId.Create(RealmEndpoint.Parse("https://zulip.example"), 7),
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
        session.SearchMessagesAction = (_, _, _, _) => Task.FromResult(new MessageQueryPage(
            [new ChatMessage(5, conversation, 8, "native search from server", DateTimeOffset.UnixEpoch)],
            false, true, true));

        viewModel.OpenSearchCommand.Execute(null);
        viewModel.SearchQuery = "native search";
        Assert.Empty(viewModel.SearchResults);
        await ((IAsyncRelayCommand)viewModel.SearchNowCommand).ExecuteAsync(null);

        var result = Assert.Single(viewModel.SearchResults);
        Assert.Equal(5, result.MessageId);
        Assert.Equal(conversation, result.Conversation);
        Assert.Equal("native search from server", result.Subtitle);
        Assert.True(viewModel.HasMoreSearchResults);
        session.Publish();
        Assert.Single(viewModel.SearchResults);

        viewModel.SearchQuery = "Bea";
        Assert.Empty(viewModel.SearchResults);
        Assert.False(viewModel.HasMoreSearchResults);
    }

    [Fact]
    public async Task SearchCategory_WhenSelected_ShowsServerResultsForContentType()
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
        session.SearchMessagesAction = (query, _, _, _) => Task.FromResult(new MessageQueryPage(
            session.StateValue.Messages.Values.Where(message => message.Content.Contains(query, StringComparison.Ordinal)).ToArray(),
            true, true, true));
        viewModel.OpenSearchCommand.Execute(null);
        viewModel.SearchQuery = "shot";

        viewModel.SelectSearchCategoryCommand.Execute(viewModel.SearchCategories.Single(item => item.Filter == MessageSearchFilter.Images));
        await ((IAsyncRelayCommand)viewModel.SearchNowCommand).ExecuteAsync(null);
        var image = Assert.Single(viewModel.SearchResults);
        Assert.Equal("server-message:3", image.Id);
        Assert.Equal("图片", image.Kind);

        viewModel.SearchQuery = "clip";
        viewModel.SelectSearchCategoryCommand.Execute(viewModel.SearchCategories.Single(item => item.Filter == MessageSearchFilter.Videos));
        await ((IAsyncRelayCommand)viewModel.SearchNowCommand).ExecuteAsync(null);
        var video = Assert.Single(viewModel.SearchResults);
        Assert.Equal("server-message:4", video.Id);
        Assert.Equal("视频", video.Kind);

        viewModel.SearchQuery = "notes";
        viewModel.SelectSearchCategoryCommand.Execute(viewModel.SearchCategories.Single(item => item.Filter == MessageSearchFilter.Files));
        await ((IAsyncRelayCommand)viewModel.SearchNowCommand).ExecuteAsync(null);
        var file = Assert.Single(viewModel.SearchResults);
        Assert.Equal("server-message:2", file.Id);
        Assert.Equal("文件", file.Kind);

        viewModel.SearchQuery = "example";
        viewModel.SelectSearchCategoryCommand.Execute(viewModel.SearchCategories.Single(item => item.Filter == MessageSearchFilter.Links));
        await ((IAsyncRelayCommand)viewModel.SearchNowCommand).ExecuteAsync(null);
        var link = Assert.Single(viewModel.SearchResults);
        Assert.Equal("server-message:5", link.Id);
        Assert.Equal("链接", link.Kind);

        viewModel.SearchQuery = "plain";
        viewModel.SelectSearchCategoryCommand.Execute(viewModel.SearchCategories.Single(item => item.Filter == MessageSearchFilter.Messages));
        await ((IAsyncRelayCommand)viewModel.SearchNowCommand).ExecuteAsync(null);
        Assert.Equal("server-message:1", Assert.Single(viewModel.SearchResults).Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(MessageSearchFilter.Images)]
    public async Task SearchImages_WhenMessageContainsImages_ProjectsPreviewsAndKeepsOriginalMessageTarget(MessageSearchFilter? filter)
    {
        var realm = RealmEndpoint.Parse("https://chat.example.test/");
        var conversation = new DirectMessage([8]);
        const string raw = "设计图\n![shot](/user_uploads/1/shot.png)\n" +
                           "[other](https://chat.example.test/user_uploads/1/other.JPG)\n" +
                           "[notes.pdf](/user_uploads/1/notes.pdf)";
        var message = new ChatMessage(90, conversation, 8, raw, DateTimeOffset.UnixEpoch, senderDisplayName: "Bea");
        var session = new FakeSession
        {
            Account = AccountId.Create(realm, 7),
            ActiveRealm = realm,
            SearchMessagesAction = (_, _, _, _) => Task.FromResult(new MessageQueryPage([message], true, true, true))
        };
        using var viewModel = CreateViewModel(session);
        viewModel.OpenSearchCommand.Execute(null);
        viewModel.SelectSearchCategoryCommand.Execute(viewModel.SearchCategories.Single(item => item.Filter == filter));

        await viewModel.SearchNowCommand.ExecuteAsync(null);

        var result = Assert.Single(viewModel.SearchResults);
        Assert.True(result.HasImages);
        Assert.True(result.HasSubtitle);
        Assert.Equal("Bea", result.Title);
        Assert.Equal("设计图 [文件] notes.pdf", result.Subtitle);
        Assert.Equal(new[]
        {
            "https://chat.example.test/user_uploads/1/shot.png",
            "https://chat.example.test/user_uploads/1/other.JPG"
        }, result.Images.Select(image => image.SourceUrl));
        Assert.Equal(raw, message.Content);
        await viewModel.SelectSearchResultCommand.ExecuteAsync(result);
        Assert.Equal((conversation, 90L), Assert.Single(session.OpenedMessages));
    }

    [Theory]
    [InlineData("普通文字")]
    [InlineData("[notes.pdf](/user_uploads/1/notes.pdf)")]
    [InlineData("![external](https://outside.example.test/user_uploads/1/shot.png)")]
    [InlineData("![temporary](/user_uploads/temporary/shot.png)")]
    [InlineData("![http](http://chat.example.test/user_uploads/1/shot.png)")]
    public async Task SearchImages_WhenNoControlledImageExists_KeepsTextResult(string content)
    {
        var realm = RealmEndpoint.Parse("https://chat.example.test/");
        var session = new FakeSession
        {
            Account = AccountId.Create(realm, 7),
            ActiveRealm = realm,
            SearchMessagesAction = (_, _, _, _) => Task.FromResult(new MessageQueryPage(
                [new ChatMessage(90, new DirectMessage([8]), 8, content, DateTimeOffset.UnixEpoch)], true, true, true))
        };
        using var viewModel = CreateViewModel(session);
        viewModel.OpenSearchCommand.Execute(null);

        await viewModel.SearchNowCommand.ExecuteAsync(null);

        var result = Assert.Single(viewModel.SearchResults);
        Assert.Empty(result.Images);
        Assert.False(result.HasImages);
        Assert.True(result.HasSubtitle);
        Assert.Equal(content, result.Subtitle);
    }

    [Fact]
    public async Task SearchImages_WhenLoadingMore_ProjectsThumbnailsOnEveryPageWithoutRawImageLinks()
    {
        var realm = RealmEndpoint.Parse("https://chat.example.test/");
        var session = new FakeSession
        {
            Account = AccountId.Create(realm, 7),
            ActiveRealm = realm,
            SearchMessagesAction = (_, before, _, _) => Task.FromResult(new MessageQueryPage(
                [new ChatMessage(before is null ? 90 : 80, new DirectMessage([8]), 8,
                    before is null ? "![first](/user_uploads/1/first.png)" : "![second](/user_uploads/1/second.png)",
                    DateTimeOffset.UnixEpoch)], before is not null, true, true))
        };
        using var viewModel = CreateViewModel(session);
        viewModel.OpenSearchCommand.Execute(null);
        viewModel.SelectSearchCategoryCommand.Execute(viewModel.SearchCategories.Single(item => item.Filter == MessageSearchFilter.Images));

        await viewModel.SearchNowCommand.ExecuteAsync(null);
        await viewModel.LoadOlderSearchCommand.ExecuteAsync(null);

        Assert.Equal(2, viewModel.SearchResults.Count);
        Assert.All(viewModel.SearchResults, result =>
        {
            Assert.Single(result.Images);
            Assert.True(result.HasImages);
            Assert.False(result.HasSubtitle);
            Assert.Empty(result.Subtitle);
        });
        Assert.Equal("https://chat.example.test/user_uploads/1/second.png", viewModel.SearchResults[1].Images[0].SourceUrl);
    }

    [Theory]
    [InlineData("design")]
    [InlineData("")]
    [InlineData(" \t ")]
    public async Task SearchNow_WhenAllCategoryIsSelected_SearchesAllContentOnlyAfterExplicitSubmit(string query)
    {
        var calls = 0;
        var conversation = new DirectMessage([8]);
        var content = new[]
        {
            "design text", "[design file](/user_uploads/1/design.pdf)",
            "![design image](/user_uploads/1/design.png)", "[design video](/user_uploads/1/design.mp4)",
            "https://example.test/design"
        };
        var messages = content.Select((text, index) => new ChatMessage(
            index + 1, conversation, 8, text, DateTimeOffset.UnixEpoch)).ToArray();
        var session = new FakeSession
        {
            Account = AccountId.Create(RealmEndpoint.Parse("https://zulip.example"), 7),
            ActiveRealm = RealmEndpoint.Parse("https://zulip.example"),
            SearchMessagesWithFilterAction = (requestedQuery, _, _, filter, _) =>
            {
                calls++;
                Assert.Equal(query.Trim(), requestedQuery);
                Assert.Equal(MessageSearchFilter.Messages, filter);
                return Task.FromResult(new MessageQueryPage(messages, true, true, true));
            }
        };
        using var viewModel = CreateViewModel(session);
        viewModel.OpenSearchCommand.Execute(null);
        Assert.Equal("全部", Assert.Single(viewModel.SearchCategories, category => category.IsSelected).Label);
        viewModel.SearchQuery = query;
        Assert.Equal(0, calls);
        Assert.Empty(viewModel.SearchResults);
        Assert.Equal(string.IsNullOrWhiteSpace(query)
            ? "点击搜索或按 Enter 查看记录"
            : "点击搜索或按 Enter 开始搜索", viewModel.SearchEmptyText);

        await viewModel.SearchNowCommand.ExecuteAsync(null);
        Assert.Equal(1, calls);
        Assert.Equal(5, viewModel.SearchResults.Count);

        viewModel.SelectSearchCategoryCommand.Execute(
            viewModel.SearchCategories.Single(category => category.Filter == MessageSearchFilter.Messages));
        Assert.Equal(1, calls);
        Assert.Empty(viewModel.SearchResults);
        Assert.False(viewModel.IsSearchBusy);
        await viewModel.SearchNowCommand.ExecuteAsync(null);
        Assert.Equal(2, calls);
        Assert.Equal(1, Assert.Single(viewModel.SearchResults).MessageId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(MessageSearchFilter.Messages)]
    [InlineData(MessageSearchFilter.Files)]
    [InlineData(MessageSearchFilter.Images)]
    [InlineData(MessageSearchFilter.Videos)]
    [InlineData(MessageSearchFilter.Links)]
    public async Task SearchCategory_WhenFilterHasNoKeyword_RequestsServerOnExplicitSubmit(MessageSearchFilter? category)
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
        viewModel.SelectSearchCategoryCommand.Execute(viewModel.SearchCategories.Single(item => item.Filter == category));

        Assert.Null(requestedFilter);
        Assert.Empty(viewModel.SearchResults);

        await ((IAsyncRelayCommand)viewModel.SearchNowCommand).ExecuteAsync(null);

        Assert.Equal(category ?? MessageSearchFilter.Messages, requestedFilter);
        Assert.Equal("没有匹配结果。", viewModel.SearchEmptyText);
        Assert.Empty(viewModel.SearchResults);
    }

    [Theory]
    [InlineData("query")]
    [InlineData("category")]
    [InlineData("close")]
    public async Task SearchNow_WhenEmptyQueryChangesDuringRequest_DiscardsLateResults(string change)
    {
        var pending = new TaskCompletionSource<MessageQueryPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new FakeSession
        {
            Account = AccountId.Create(RealmEndpoint.Parse("https://zulip.example"), 7),
            SearchMessagesAction = (_, _, _, _) => pending.Task
        };
        using var viewModel = CreateViewModel(session);
        viewModel.OpenSearchCommand.Execute(null);
        var search = viewModel.SearchNowCommand.ExecuteAsync(null);
        Assert.True(viewModel.IsSearchBusy);
        Assert.Equal("正在搜索…", viewModel.SearchEmptyText);

        if (change == "query") viewModel.SearchQuery = "next";
        else if (change == "category") viewModel.SelectSearchCategoryCommand.Execute(
            viewModel.SearchCategories.Single(item => item.Filter == MessageSearchFilter.Images));
        else viewModel.CloseSearchCommand.Execute(null);
        pending.SetResult(new MessageQueryPage(
            [new ChatMessage(90, new DirectMessage([8]), 8, "旧结果", DateTimeOffset.UnixEpoch)], false, true, true));
        await search;

        Assert.Empty(viewModel.SearchResults);
        Assert.False(viewModel.HasMoreSearchResults);
    }

    [Fact]
    public async Task OpenSearch_WhenEmptySearchCompleted_RefreshesHintWithoutRequestingAgain()
    {
        var session = new FakeSession
        {
            Account = AccountId.Create(RealmEndpoint.Parse("https://zulip.example"), 7),
            SearchMessagesAction = (_, _, _, _) => Task.FromResult(new MessageQueryPage([], true, true, true))
        };
        using var viewModel = CreateViewModel(session);
        viewModel.OpenSearchCommand.Execute(null);
        await viewModel.SearchNowCommand.ExecuteAsync(null);
        Assert.Equal("没有匹配结果。", viewModel.SearchEmptyText);
        viewModel.CloseSearchCommand.Execute(null);
        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        viewModel.OpenSearchCommand.Execute(null);

        Assert.Single(session.SearchRequests);
        Assert.Equal("点击搜索或按 Enter 查看记录", viewModel.SearchEmptyText);
        Assert.Contains(nameof(viewModel.SearchEmptyText), changed);
    }

    [Fact]
    public async Task SearchCategory_WhenMediaFilterHasKeyword_RequestsServerFilter()
    {
        MessageSearchFilter? requestedFilter = null;
        string? requestedQuery = null;
        var session = new FakeSession
        {
            Account = AccountId.Create(RealmEndpoint.Parse("https://zulip.example"), 7),
            SearchMessagesWithFilterAction = (query, _, _, filter, _) =>
            {
                requestedQuery = query;
                requestedFilter = filter;
                return Task.FromResult(new MessageQueryPage([], true, true, true));
            }
        };
        using var viewModel = CreateViewModel(session);
        viewModel.OpenSearchCommand.Execute(null);
        viewModel.SelectSearchCategoryCommand.Execute(viewModel.SearchCategories.Single(item => item.Filter == MessageSearchFilter.Images));
        viewModel.SearchQuery = "design";

        await ((IAsyncRelayCommand)viewModel.SearchNowCommand).ExecuteAsync(null);

        Assert.Equal("design", requestedQuery);
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

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public void ConversationPreview_WhenLatestMessageQuotesAnotherMessage_ShowsReplyForEveryConversationKind(
        int conversationKind, bool useCachedSummary)
    {
        ConversationKey conversation = conversationKind switch
        {
            0 => new DirectMessage([8]),
            1 => new DirectMessage([]),
            _ => new ChannelTopic(4, string.Empty)
        };
        const string content = "@_**Bea|8** [said](https://chat.example.test/#narrow/channel/4-a-long-conversation-name/near/42):\n" +
                               "```quote\n被引用的原消息\n```\n\n这是回复正文";
        var message = new ChatMessage(50, conversation, 7, content, DateTimeOffset.UnixEpoch, senderDisplayName: "Ada");
        var session = new FakeSession
        {
            CurrentUserId = 7,
            Recent = conversation is DirectMessage ? [conversation] : [],
            StateValue = new ClientState(
                messages: useCachedSummary ? null : new Dictionary<long, ChatMessage> { [50] = message },
                conversationSummaries: useCachedSummary
                    ? new Dictionary<string, ConversationSummary> { [conversation.CanonicalKey] = new(conversation, message) }
                    : null,
                subscriptions: conversation is ChannelTopic
                    ? new Dictionary<long, Subscription> { [4] = PrivateGroupSubscription(4, "测试群") }
                    : null,
                users: new Dictionary<long, UserProfile>
                {
                    [7] = new UserProfile(7, "Ada"),
                    [8] = new UserProfile(8, "Bea")
                })
        };
        using var viewModel = CreateViewModel(session);
        var item = Assert.Single(viewModel.Conversations);
        var prefix = conversation is ChannelTopic ? "Ada: " : string.Empty;

        Assert.Equal(prefix + "这是回复正文", item.Detail);
        Assert.Equal(item.Detail, Assert.Single(viewModel.FilteredConversations).Detail);
        if (conversation is DirectMessage)
            Assert.Equal("这是回复正文", Assert.Single(viewModel.DirectMessages).Detail);
        Assert.Equal(content, message.Content);

        var edited = message with { Content = content.Replace("这是回复正文", "更新后的回复", StringComparison.Ordinal) };
        session.StateValue = useCachedSummary
            ? session.StateValue with
            {
                ConversationSummaries = new Dictionary<string, ConversationSummary>
                {
                    [conversation.CanonicalKey] = new(conversation, edited)
                }
            }
            : session.StateValue with { Messages = new Dictionary<long, ChatMessage> { [50] = edited } };
        session.Publish();

        Assert.Same(item, Assert.Single(viewModel.Conversations));
        Assert.Equal(prefix + "更新后的回复", item.Detail);
    }

    [Theory]
    [InlineData("**Bea** [said](#):\n```quote\n只有引用内容\n```\n\n", "只有引用内容")]
    [InlineData("@_**Bea|8** [said](https://chat.example.test/#narrow/near/42):\r\n```quote\r\n原文\r\n```\r\n\r\n回复", "回复")]
    [InlineData("**Bea** [said](#):\n````quote\n原文含 `代码`\n````\n\n回复", "回复")]
    [InlineData("**Bea** [said](#):\n```quote\n原文\n```紧接的回复", "紧接的回复")]
    [InlineData("**Bea** [said](#):\n```quote\n第一条\n```\n\n**Ada** [said](#):\n```quote\n第二条\n```\n\n回复两条", "回复两条")]
    [InlineData("**Bea** [said](#):\n```quote\n第一条\n```\n\n**Ada** [said](#):\n```quote\n第二条\n```", "第一条 第二条")]
    [InlineData("普通消息 https://example.test/page", "普通消息 https://example.test/page")]
    [InlineData("![截图](/user_uploads/1/shot.png)", "[图片]")]
    [InlineData("[截图](/user_uploads/1/shot.PNG)", "[图片]")]
    [InlineData("[报告](/user_uploads/1/report.pdf)", "[文件]")]
    [InlineData("[报\\[告\\].pdf](/user_uploads/1/report.pdf)", "[文件]")]
    [InlineData("看这张图\n![截图](/user_uploads/1/shot.png)", "看这张图 [图片]")]
    [InlineData("![图](/user_uploads/1/shot.png)\n[文件](/user_uploads/1/file.zip)", "[图片] [文件]")]
    [InlineData("**Bea** [said](#):\n```quote\n![截图](/user_uploads/1/shot.png)\n```", "[图片]")]
    [InlineData("**Bea** [said](#):\n```quote\n旧消息\n```\n\n[文件](/user_uploads/1/file.zip)", "[文件]")]
    [InlineData("**Bea** [said](#):\n````quote\n**Ada** [said](#):\n```quote\n原始消息\n```\n\n上一条回复\n````", "原始消息 上一条回复")]
    [InlineData("**Bea** [said](#):\n```quote\n第一条\n```\n\n**Bea** [said](#):\n````quote\n**Ada** [said](#):\n```quote\n嵌套引用\n```\n\n上一条回复\n````", "第一条 嵌套引用 上一条回复")]
    public void ConversationPreview_WhenQuoteFormatVaries_ShowsMessageTextWithoutQuoteMetadata(
        string content, string expected)
    {
        var conversation = new DirectMessage([8]);
        var session = new FakeSession
        {
            CurrentUserId = 7,
            Recent = [conversation],
            StateValue = new ClientState(messages: new Dictionary<long, ChatMessage>
            {
                [50] = new ChatMessage(50, conversation, 8, content, DateTimeOffset.UnixEpoch)
            })
        };
        using var viewModel = CreateViewModel(session);

        Assert.Equal(expected, Assert.Single(viewModel.Conversations).Detail);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ConversationPreview_WhenLatestAttachmentChanges_UpdatesExistingRowFromMessagesOrSummary(
        bool isGroup, bool useCachedSummary)
    {
        ConversationKey conversation = isGroup ? new ChannelTopic(4, string.Empty) : new DirectMessage([8]);
        var message = new ChatMessage(50, conversation, 8,
            "[截图](https://chat.example.test/user_uploads/1/shot.png)", DateTimeOffset.UnixEpoch,
            senderDisplayName: "Bea");
        var session = new FakeSession
        {
            CurrentUserId = 7,
            ActiveRealm = RealmEndpoint.Parse("https://chat.example.test/"),
            Recent = isGroup ? [] : [conversation],
            StateValue = new ClientState(
                messages: useCachedSummary ? null : new Dictionary<long, ChatMessage> { [50] = message },
                conversationSummaries: useCachedSummary
                    ? new Dictionary<string, ConversationSummary> { [conversation.CanonicalKey] = new(conversation, message) }
                    : null,
                subscriptions: isGroup
                    ? new Dictionary<long, Subscription> { [4] = PrivateGroupSubscription(4, "测试群") }
                    : null)
        };
        using var viewModel = CreateViewModel(session);
        var item = Assert.Single(viewModel.Conversations);
        var prefix = isGroup ? "Bea: " : string.Empty;
        Assert.Equal(prefix + "[图片]", item.Detail);

        var edited = message with { Content = "[报告](/user_uploads/1/report.pdf)" };
        session.StateValue = useCachedSummary
            ? session.StateValue with
            {
                ConversationSummaries = new Dictionary<string, ConversationSummary>
                {
                    [conversation.CanonicalKey] = new(conversation, edited)
                }
            }
            : session.StateValue with { Messages = new Dictionary<long, ChatMessage> { [50] = edited } };
        session.Publish();

        Assert.Same(item, Assert.Single(viewModel.Conversations));
        Assert.Equal(prefix + "[文件]", item.Detail);
        Assert.Equal(item.Detail, Assert.Single(viewModel.FilteredConversations).Detail);
        Assert.Equal("[报告](/user_uploads/1/report.pdf)", edited.Content);
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
        Assert.Equal(["Ada", "Bea", "Chen"], viewModel.NewConversationChoices.Select(choice => choice.Name));
        Assert.Contains(viewModel.NewConversationChoices, choice => choice.UserId == 7);
        foreach (var choice in viewModel.NewConversationChoices)
        {
            viewModel.SelectNewDirectConversationContactCommand.Execute(choice);
            Assert.Same(choice, Assert.Single(viewModel.NewConversationChoices, item => item.IsSelected));
        }
        viewModel.SelectNewDirectConversationContactCommand.Execute(viewModel.NewConversationChoices[2]);
        Assert.Equal(9, Assert.Single(viewModel.NewConversationChoices, choice => choice.IsSelected).UserId);
        Assert.True(viewModel.CanStartNewConversation);
        Assert.True(viewModel.IsNewConversationOpen);
        Assert.Null(session.SelectedConversation);

        await ((IAsyncRelayCommand)viewModel.StartNewConversationCommand).ExecuteAsync(null);

        var direct = Assert.IsType<DirectMessage>(session.Selected);
        Assert.Equal([9L], direct.OtherUserIds);
        Assert.False(viewModel.IsNewConversationOpen);
    }

    [Fact]
    public async Task StartNewConversationCommand_WhenSelfIsSelected_OpensCanonicalSelfConversation()
    {
        var session = new FakeSession
        {
            CurrentUserId = 7,
            StateValue = new ClientState(
                users: new Dictionary<long, UserProfile>
                {
                    [7] = new UserProfile(7, "Ada"),
                    [8] = new UserProfile(8, "Bea"),
                    [9] = new UserProfile(9, "Inactive", isActive: false)
                },
                connection: new ConnectionState(ConnectionStatus.Connected))
        };
        using var viewModel = CreateViewModel(session);
        viewModel.OpenNewConversationCommand.Execute(null);
        Assert.Equal([7L, 8L], viewModel.NewConversationChoices.Select(choice => choice.UserId));
        var other = Assert.Single(viewModel.NewConversationChoices, choice => choice.UserId == 8);
        viewModel.SelectNewDirectConversationContactCommand.Execute(other);
        viewModel.NewConversationQuery = "自己";
        var self = Assert.Single(viewModel.NewConversationChoices);
        Assert.Equal(7, self.UserId);
        Assert.Equal("自己", self.KindLabel);
        viewModel.SelectNewDirectConversationContactCommand.Execute(self);
        Assert.False(other.IsSelected);
        Assert.True(viewModel.CanStartNewConversation);

        await viewModel.StartNewConversationCommand.ExecuteAsync(null);

        Assert.Equal(new DirectMessage([]), session.SelectedConversation);
        Assert.False(viewModel.IsNewConversationOpen);
        Assert.Empty(session.SentContents);
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
    public async Task AddDroppedAttachments_WhenWindowsDropSuppliesFiles_StartsImmediateUpload()
    {
        var session = new FakeSession
        {
            Selected = new DirectMessage([8]),
            StateValue = new ClientState(connection: new ConnectionState(ConnectionStatus.Connected))
        };
        using var viewModel = CreateViewModel(session);
        session.Publish();
        var dropped = new SelectedAttachmentFile(
            "notes.txt",
            "text/plain",
            3,
            _ => Task.FromResult<Stream>(new MemoryStream([1, 2, 3])));
        viewModel.IsFileDragActive = true;

        viewModel.AddDroppedAttachmentsCommand.Execute(new[] { dropped });
        await WaitUntilAsync(() => viewModel.Attachments.Single().Status == AttachmentUploadStatus.Uploaded);

        Assert.False(viewModel.IsFileDragActive);
        Assert.Equal("notes.txt", Assert.Single(viewModel.Attachments).FileName);
        Assert.True(viewModel.HasAttachments);
        Assert.Equal(1, session.UploadCalls);
    }

    [Theory]
    [InlineData("design[1].png", "image/png", "design[1].png",
        "![design\\[1\\].png](https://example.test/user_uploads/design[1].png)")]
    [InlineData("设计图[终版].png", "image/png", "设计图终版.png",
        "![设计图\\[终版\\].png](https://example.test/user_uploads/设计图终版.png)")]
    [InlineData("使用说明[终版].txt", "text/plain", "使用说明终版.txt",
        "[使用说明\\[终版\\].txt](https://example.test/user_uploads/使用说明终版.txt)")]
    public async Task SendCommand_WhenAttachmentIsSelected_UploadsOnceThenSendsOneMarkdownMessage(
        string fileName, string contentType, string uploadPath, string expectedMarkdown)
    {
        var conversation = new DirectMessage([8]);
        var filePicker = new FakeFileSelectionService
        {
            Files =
            [
                new SelectedAttachmentFile(
                    fileName,
                    contentType,
                    3,
                    _ => Task.FromResult<Stream>(new MemoryStream([1, 2, 3])))
            ]
        };
        var session = new FakeSession
        {
            Selected = conversation,
            StateValue = new ClientState(connection: new ConnectionState(ConnectionStatus.Connected)),
            UploadAction = (upload, _) => Task.FromResult(new UploadedAttachment(
                upload.FileName, "https://example.test/user_uploads/" + uploadPath))
        };
        using var viewModel = CreateViewModel(session, fileSelectionService: filePicker);
        session.Publish();

        await ((IAsyncRelayCommand)viewModel.PickAttachmentsCommand).ExecuteAsync(null);
        await WaitUntilAsync(() => viewModel.Attachments.Single().Status == AttachmentUploadStatus.Uploaded);
        viewModel.ComposerText = "caption";
        await ((IAsyncRelayCommand)viewModel.SendCommand).ExecuteAsync(null);

        Assert.Equal(1, session.UploadCalls);
        Assert.Equal("caption\n" + expectedMarkdown, Assert.Single(session.SentContents));
        Assert.Empty(viewModel.Attachments);
        Assert.Equal(string.Empty, viewModel.ComposerText);
    }

    [Fact]
    public async Task SendCommand_WhenOnlyAttachmentExists_EnablesOnlyAfterImmediateUploadCompletes()
    {
        var releaseUpload = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var filePicker = new FakeFileSelectionService
        {
            Files =
            [
                new SelectedAttachmentFile(
                    "only.txt",
                    "text/plain",
                    3,
                    _ => Task.FromResult<Stream>(new MemoryStream([1, 2, 3])))
            ]
        };
        var session = new FakeSession
        {
            Selected = new DirectMessage([8]),
            StateValue = new ClientState(connection: new ConnectionState(ConnectionStatus.Connected)),
            UploadAction = async (upload, cancellationToken) =>
            {
                await releaseUpload.Task.WaitAsync(cancellationToken);
                return new UploadedAttachment(upload.FileName, "https://example.test/user_uploads/only.txt");
            }
        };
        using var viewModel = CreateViewModel(session, fileSelectionService: filePicker);
        session.Publish();

        await ((IAsyncRelayCommand)viewModel.PickAttachmentsCommand).ExecuteAsync(null);
        await WaitUntilAsync(() => viewModel.Attachments.Single().Status == AttachmentUploadStatus.Uploading);
        Assert.False(viewModel.CanSend);
        Assert.False(viewModel.SendCommand.CanExecute(null));

        releaseUpload.SetResult(true);
        await WaitUntilAsync(() => viewModel.Attachments.Single().Status == AttachmentUploadStatus.Uploaded);
        Assert.True(viewModel.CanSend);
        Assert.True(viewModel.SendCommand.CanExecute(null));

        await ((IAsyncRelayCommand)viewModel.SendCommand).ExecuteAsync(null);

        Assert.Equal("[only.txt](https://example.test/user_uploads/only.txt)", Assert.Single(session.SentContents));
    }

    [Fact]
    public async Task RetryAttachment_WhenImmediateUploadFailed_RetriesOnlyAfterExplicitCommand()
    {
        var attempt = 0;
        var filePicker = new FakeFileSelectionService
        {
            Files =
            [
                new SelectedAttachmentFile(
                    "retry.txt",
                    "text/plain",
                    3,
                    _ => Task.FromResult<Stream>(new MemoryStream([1, 2, 3])))
            ]
        };
        var session = new FakeSession
        {
            Selected = new DirectMessage([8]),
            StateValue = new ClientState(connection: new ConnectionState(ConnectionStatus.Connected)),
            UploadAction = (upload, _) => ++attempt == 1
                ? Task.FromException<UploadedAttachment>(new InvalidOperationException("first attempt failed"))
                : Task.FromResult(new UploadedAttachment(upload.FileName, "https://example.test/user_uploads/retry.txt"))
        };
        using var viewModel = CreateViewModel(session, fileSelectionService: filePicker);
        session.Publish();

        await ((IAsyncRelayCommand)viewModel.PickAttachmentsCommand).ExecuteAsync(null);
        await WaitUntilAsync(() => viewModel.Attachments.Single().Status == AttachmentUploadStatus.Failed);
        Assert.Equal(1, session.UploadCalls);

        viewModel.RetryAttachmentCommand.Execute(viewModel.Attachments.Single());
        await WaitUntilAsync(() => viewModel.Attachments.Single().Status == AttachmentUploadStatus.Uploaded);

        Assert.Equal(2, session.UploadCalls);
        Assert.True(viewModel.CanSend);
    }

    [Fact]
    public async Task AttachmentSelection_WhenAccountChangesBeforeUploadCall_DoesNotUploadWithNewAccount()
    {
        FakeSession? session = null;
        var otherAccount = RelayCove.Core.AccountId.Create(RealmEndpoint.Parse("https://other.example.test"), 99);
        var filePicker = new FakeFileSelectionService
        {
            Files =
            [
                new SelectedAttachmentFile(
                    "account-bound.txt",
                    "text/plain",
                    3,
                    _ =>
                    {
                        session!.Account = otherAccount;
                        return Task.FromResult<Stream>(new MemoryStream([1, 2, 3]));
                    })
            ]
        };
        session = new FakeSession
        {
            Selected = new DirectMessage([8]),
            StateValue = new ClientState(connection: new ConnectionState(ConnectionStatus.Connected))
        };
        using var viewModel = CreateViewModel(session, fileSelectionService: filePicker);
        session.Publish();

        await ((IAsyncRelayCommand)viewModel.PickAttachmentsCommand).ExecuteAsync(null);
        await WaitUntilAsync(() => session.Account == otherAccount);
        await Task.Delay(50);

        Assert.Equal(0, session.UploadCalls);
    }

    [Fact]
    public async Task RecoverOutbox_WhenUploadedAttachmentIsStillInDraft_SendsItsMarkdownExactlyOnce()
    {
        var conversation = new DirectMessage([8]);
        var uploadedMarkdown = "[notes.txt](https://example.test/user_uploads/notes.txt)";
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
        var session = new FakeSession
        {
            Selected = conversation,
            StateValue = new ClientState(connection: new ConnectionState(ConnectionStatus.Connected))
        };
        using var viewModel = CreateViewModel(session, fileSelectionService: filePicker);
        session.Publish();
        await ((IAsyncRelayCommand)viewModel.PickAttachmentsCommand).ExecuteAsync(null);
        await WaitUntilAsync(() => viewModel.Attachments.Single().Status == AttachmentUploadStatus.Uploaded);

        var outbox = new OutboxEntry(
            "attachment-recovery",
            conversation,
            uploadedMarkdown,
            DateTimeOffset.UnixEpoch,
            OutboxState.WaitExpired,
            OutboxFailureKind.NetworkResultUnknown);
        session.StateValue = new ClientState(
            outbox: new Dictionary<string, OutboxEntry> { [outbox.LocalId] = outbox },
            connection: new ConnectionState(ConnectionStatus.Connected));
        session.Publish();

        viewModel.RecoverOutboxCommand.Execute(viewModel.Messages.Single(message => message.Id == "local-attachment-recovery"));
        Assert.Equal(string.Empty, viewModel.ComposerText);
        await ((IAsyncRelayCommand)viewModel.SendCommand).ExecuteAsync(null);

        Assert.Equal(1, session.UploadCalls);
        Assert.Equal(uploadedMarkdown, Assert.Single(session.SentContents));
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
        viewModel.ComposerText = "caption";

        await uploadReported.Task;
        var attachment = Assert.Single(viewModel.Attachments);

        Assert.Equal(AttachmentUploadStatus.Uploading, attachment.Status);
        Assert.Equal(0.5d, attachment.UploadProgress);
        Assert.Equal("正在上传 50%", attachment.StatusLabel);
        Assert.False(viewModel.CanSend);
        Assert.False(viewModel.SendCommand.CanExecute(null));

        releaseUpload.SetResult(true);
        await WaitUntilAsync(() => viewModel.Attachments.Single().Status == AttachmentUploadStatus.Uploaded);
        Assert.True(viewModel.CanSend);
        Assert.True(viewModel.SendCommand.CanExecute(null));
        await ((IAsyncRelayCommand)viewModel.SendCommand).ExecuteAsync(null);
        Assert.Empty(viewModel.Attachments);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadAttachments_WhenMultipleFilesAreRead_UploadsOriginalFilesWithoutChangingText(bool drop)
    {
        var session = ConversationMenuSession();
        using var viewModel = CreateViewModel(session);
        viewModel.ComposerText = "caption";
        Func<CancellationToken, Task<IReadOnlyList<SelectedAttachmentFile>>> read = _ =>
            Task.FromResult<IReadOnlyList<SelectedAttachmentFile>>([ClipboardTestFile("资料.txt"), ClipboardTestFile("original.png")]);

        await (drop ? viewModel.DropAttachmentsCommand : viewModel.PasteAttachmentsCommand).ExecuteAsync(read);
        await WaitUntilAsync(() => viewModel.Attachments.Count == 2 &&
            viewModel.Attachments.All(item => item.Status == AttachmentUploadStatus.Uploaded));

        Assert.Equal(["资料.txt", "original.png"], viewModel.Attachments.Select(item => item.FileName));
        Assert.Equal("caption", viewModel.ComposerText);
        Assert.Equal(2, session.UploadCalls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ReadAttachments_WhenConversationChangesDuringRead_DiscardsLateFiles(bool drop, bool returnToOriginal)
    {
        var session = ConversationMenuSession();
        using var viewModel = CreateViewModel(session);
        var readResult = new TaskCompletionSource<IReadOnlyList<SelectedAttachmentFile>>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken readToken = default;
        Func<CancellationToken, Task<IReadOnlyList<SelectedAttachmentFile>>> read = token =>
        {
            readToken = token;
            return readResult.Task;
        };
        var command = drop ? viewModel.DropAttachmentsCommand : viewModel.PasteAttachmentsCommand;
        var pending = command.ExecuteAsync(read);
        Assert.False(command.CanExecute(read));

        session.Selected = new DirectMessage([9]);
        session.Publish();
        if (returnToOriginal)
        {
            session.Selected = new DirectMessage([8]);
            session.Publish();
        }
        readResult.SetResult([ClipboardTestFile("late.txt")]);
        await pending;

        Assert.True(readToken.IsCancellationRequested);
        Assert.Empty(viewModel.Attachments);
        Assert.Null(viewModel.AttachmentError);
        Assert.Equal(0, session.UploadCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadAttachments_WhenAccountChangesWithSameConversation_DiscardsLateFiles(bool drop)
    {
        var session = ConversationMenuSession();
        using var viewModel = CreateViewModel(session);
        var readResult = new TaskCompletionSource<IReadOnlyList<SelectedAttachmentFile>>(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<CancellationToken, Task<IReadOnlyList<SelectedAttachmentFile>>> read = _ => readResult.Task;
        var pending = (drop ? viewModel.DropAttachmentsCommand : viewModel.PasteAttachmentsCommand).ExecuteAsync(read);

        session.Account = AccountId.Create(RealmEndpoint.Parse("https://other.example.test"), 7);
        session.Publish();
        readResult.SetResult([ClipboardTestFile("late.txt")]);
        await pending;

        Assert.Empty(viewModel.Attachments);
        Assert.Equal(0, session.UploadCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadAttachments_WhenMessageIsSentDuringRead_DoesNotAddFilesToNextDraft(bool drop)
    {
        var session = ConversationMenuSession();
        using var viewModel = CreateViewModel(session);
        viewModel.ComposerText = "send this";
        var readResult = new TaskCompletionSource<IReadOnlyList<SelectedAttachmentFile>>(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<CancellationToken, Task<IReadOnlyList<SelectedAttachmentFile>>> read = _ => readResult.Task;
        var pending = (drop ? viewModel.DropAttachmentsCommand : viewModel.PasteAttachmentsCommand).ExecuteAsync(read);

        await viewModel.SendCommand.ExecuteAsync(null);
        viewModel.ComposerText = "next draft";
        readResult.SetResult([ClipboardTestFile("old-draft.txt")]);
        await pending;

        Assert.Equal("send this", Assert.Single(session.SentContents));
        Assert.Equal("next draft", viewModel.ComposerText);
        Assert.Empty(viewModel.Attachments);
        Assert.Equal(0, session.UploadCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadAttachments_WhenAccountHasChangedBeforeUiProjection_DoesNotReadFiles(bool drop)
    {
        var session = ConversationMenuSession();
        using var viewModel = CreateViewModel(session);
        session.Account = AccountId.Create(RealmEndpoint.Parse("https://other.example.test"), 7);
        var called = false;
        Func<CancellationToken, Task<IReadOnlyList<SelectedAttachmentFile>>> read = _ =>
        {
            called = true;
            return Task.FromResult<IReadOnlyList<SelectedAttachmentFile>>([ClipboardTestFile("file.txt")]);
        };

        await (drop ? viewModel.DropAttachmentsCommand : viewModel.PasteAttachmentsCommand).ExecuteAsync(read);

        Assert.False(called);
        Assert.Empty(viewModel.Attachments);
        Assert.Equal(0, session.UploadCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadAttachments_WhenReaderFails_ShowsSafeErrorWithoutUploading(bool drop)
    {
        var session = ConversationMenuSession();
        using var viewModel = CreateViewModel(session);
        Func<CancellationToken, Task<IReadOnlyList<SelectedAttachmentFile>>> read = _ =>
            throw new IOException("private clipboard path");

        await (drop ? viewModel.DropAttachmentsCommand : viewModel.PasteAttachmentsCommand).ExecuteAsync(read);

        Assert.Contains(drop ? "无法读取拖入的附件" : "无法读取剪贴板附件", viewModel.AttachmentError);
        Assert.DoesNotContain("private", viewModel.AttachmentError);
        Assert.Empty(viewModel.Attachments);
        Assert.Equal(0, session.UploadCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadAttachments_WhenSelectionExceedsUploadLimit_RejectsWholeSelection(bool drop)
    {
        var session = ConversationMenuSession();
        using var viewModel = CreateViewModel(session);
        Func<CancellationToken, Task<IReadOnlyList<SelectedAttachmentFile>>> read = _ =>
            Task.FromResult<IReadOnlyList<SelectedAttachmentFile>>([
                ClipboardTestFile("small.txt"),
                new SelectedAttachmentFile("large.zip", "application/zip", long.MaxValue,
                    _ => throw new InvalidOperationException("Must not open an oversized file."))]);

        await (drop ? viewModel.DropAttachmentsCommand : viewModel.PasteAttachmentsCommand).ExecuteAsync(read);

        Assert.NotNull(viewModel.AttachmentError);
        Assert.Empty(viewModel.Attachments);
        Assert.Equal(0, session.UploadCalls);
    }

    [Fact]
    public async Task RemoveAttachment_WhenUploading_CancelsOnlyThatFileAndContinuesQueue()
    {
        var session = ConversationMenuSession();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.UploadAction = async (upload, token) =>
        {
            if (upload.FileName == "first.txt")
            {
                started.SetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) { cancelled.SetResult(); throw; }
            }
            return new UploadedAttachment(upload.FileName, "https://chat.example.test/user_uploads/second.txt");
        };
        using var viewModel = CreateViewModel(session);
        viewModel.AddDroppedAttachmentsCommand.Execute(new[] { ClipboardTestFile("first.txt"), ClipboardTestFile("second.txt") });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var removed = viewModel.Attachments[0];
        Assert.True(removed.CanRemove);

        viewModel.RemoveAttachmentCommand.Execute(removed);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => viewModel.Attachments.Count == 1 && viewModel.Attachments[0].Status == AttachmentUploadStatus.Uploaded);

        Assert.Equal("second.txt", viewModel.Attachments[0].FileName);
        Assert.Equal(2, session.UploadCalls);
        Assert.Null(viewModel.AttachmentError);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemoveAttachment_WhenRemovedUploadCompletesLate_DoesNotRestoreItOrStopNextFile(bool fails)
    {
        var session = ConversationMenuSession();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken uploadToken = default;
        session.UploadAction = async (upload, token) =>
        {
            if (upload.FileName == "first.txt")
            {
                uploadToken = token;
                await release.Task;
                upload.Progress?.Report(new RealmMediaTransferProgress(3, 3));
                if (fails) throw new GatewayException(GatewayErrorKind.Offline, GatewayErrorCode.NetworkError);
            }
            return new UploadedAttachment(upload.FileName, "https://chat.example.test/user_uploads/file.txt");
        };
        using var viewModel = CreateViewModel(session);
        viewModel.AddDroppedAttachmentsCommand.Execute(new[] { ClipboardTestFile("first.txt"), ClipboardTestFile("second.txt") });
        var removed = viewModel.Attachments[0];
        viewModel.RemoveAttachmentCommand.Execute(removed);
        release.SetResult();
        await WaitUntilAsync(() => viewModel.Attachments.Count == 1 && viewModel.Attachments[0].Status == AttachmentUploadStatus.Uploaded);

        Assert.True(uploadToken.IsCancellationRequested);
        Assert.Null(removed.Uploaded);
        Assert.Equal(0, removed.UploadProgress);
        Assert.Equal(2, session.UploadCalls);
        Assert.Null(viewModel.AttachmentError);
        session.Selected = new DirectMessage([9]);
        session.Publish();
        session.Selected = new DirectMessage([8]);
        session.Publish();
        Assert.Equal("second.txt", Assert.Single(viewModel.Attachments).FileName);
    }

    [Fact]
    public async Task RemoveAttachment_WhenQueued_SkipsRemovedFileWithoutCancellingActiveFile()
    {
        var session = ConversationMenuSession();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken activeToken = default;
        session.UploadAction = async (upload, token) =>
        {
            activeToken = token;
            await release.Task.WaitAsync(token);
            return new UploadedAttachment(upload.FileName, "https://chat.example.test/user_uploads/first.txt");
        };
        using var viewModel = CreateViewModel(session);
        viewModel.AddDroppedAttachmentsCommand.Execute(new[] { ClipboardTestFile("first.txt"), ClipboardTestFile("queued.txt") });
        viewModel.RemoveAttachmentCommand.Execute(viewModel.Attachments[1]);
        Assert.False(activeToken.IsCancellationRequested);
        release.SetResult();
        await WaitUntilAsync(() => viewModel.Attachments[0].Status == AttachmentUploadStatus.Uploaded);

        Assert.Equal("first.txt", Assert.Single(viewModel.Attachments).FileName);
        Assert.Equal(1, session.UploadCalls);
    }

    private static SelectedAttachmentFile ClipboardTestFile(string name) => new(
        name, name.EndsWith(".png", StringComparison.Ordinal) ? "image/png" : "text/plain", 3,
        _ => Task.FromResult<Stream>(new MemoryStream([1, 2, 3])));

    [Fact]
    public async Task AddPastedImage_WhenClipboardProvidesPng_StartsImmediateUpload()
    {
        var session = new FakeSession
        {
            Selected = new DirectMessage([8]),
            StateValue = new ClientState(connection: new ConnectionState(ConnectionStatus.Connected))
        };
        using var viewModel = CreateViewModel(session);
        session.Publish();
        var screenshot = new SelectedAttachmentFile(
            "screenshot-20260826-120000.png",
            "image/png",
            3,
            _ => Task.FromResult<Stream>(new MemoryStream([1, 2, 3])),
            openPreviewStream: () => new MemoryStream([1, 2, 3]));

        viewModel.AddPastedImageCommand.Execute(screenshot);
        await WaitUntilAsync(() => viewModel.Attachments.Single().Status == AttachmentUploadStatus.Uploaded);

        var draft = Assert.Single(viewModel.Attachments);
        Assert.True(draft.IsImage);
        Assert.True(draft.HasPreview);
        Assert.Equal("screenshot-20260826-120000.png", draft.FileName);
        Assert.True(viewModel.HasAttachments);
        Assert.Equal(1, session.UploadCalls);
    }

    [Fact]
    public void AddPastedImage_WhenClipboardImageCouldNotBeRead_ShowsSafeError()
    {
        using var viewModel = CreateViewModel(new FakeSession());

        viewModel.AddPastedImageCommand.Execute(null);

        Assert.Equal("无法读取剪贴板中的截图。", viewModel.AttachmentError);
        Assert.Empty(viewModel.Attachments);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task SendCommand_WhenUploadSucceededButSendFailed_ReusesUploadedReferenceOnExplicitRetry(
        bool isImage,
        bool resultUnknown,
        bool unicodeFileName)
    {
        var conversation = new DirectMessage([8]);
        var fileName = unicodeFileName
            ? isImage ? "测试图片.png" : "使用说明.txt"
            : isImage ? "screenshot.png" : "notes.txt";
        var expectedContent = $"caption\n{(isImage ? "!" : string.Empty)}[{fileName}](https://example.test/user_uploads/{fileName})";
        var filePicker = new FakeFileSelectionService
        {
            Files =
            [
                new SelectedAttachmentFile(
                    fileName,
                    isImage ? "image/png" : "text/plain",
                    3,
                    _ => Task.FromResult<Stream>(new MemoryStream([1, 2, 3])))
            ]
        };
        var failSend = true;
        var session = new FakeSession
        {
            Selected = conversation,
            StateValue = new ClientState(connection: new ConnectionState(ConnectionStatus.Connected))
        };
        session.SendAction = (content, _) =>
        {
            if (!failSend) return Task.CompletedTask;
            var entry = new OutboxEntry("failed-attachment", conversation, content, DateTimeOffset.UnixEpoch,
                OutboxState.Failed, resultUnknown ? OutboxFailureKind.NetworkResultUnknown : OutboxFailureKind.Rejected);
            session.StateValue = session.StateValue with
            {
                Outbox = new Dictionary<string, OutboxEntry> { [entry.LocalId] = entry }
            };
            session.Publish();
            throw new GatewayException(
                resultUnknown ? GatewayErrorKind.Offline : GatewayErrorKind.RequestFailed,
                resultUnknown ? GatewayErrorCode.NetworkError : GatewayErrorCode.RequestFailed);
        };
        using var viewModel = CreateViewModel(session, fileSelectionService: filePicker);
        session.Publish();
        await ((IAsyncRelayCommand)viewModel.PickAttachmentsCommand).ExecuteAsync(null);
        await WaitUntilAsync(() => viewModel.Attachments.Single().Status == AttachmentUploadStatus.Uploaded);
        viewModel.ComposerText = "caption";

        await ((IAsyncRelayCommand)viewModel.SendCommand).ExecuteAsync(null);
        Assert.Empty(viewModel.Attachments);
        Assert.Empty(viewModel.ComposerText);
        Assert.False(viewModel.CanSend);
        Assert.Single(session.SentContents);
        var failed = Assert.Single(viewModel.Messages);
        Assert.Equal(expectedContent, failed.Content);
        Assert.True(failed.CanRecover);
        Assert.Equal(!resultUnknown, failed.IsDeliveryFailure);
        Assert.Contains(resultUnknown ? "发送结果未确认" : "发送失败", failed.DeliveryState, StringComparison.Ordinal);

        failSend = false;
        viewModel.RecoverOutboxCommand.Execute(failed);
        Assert.Equal(expectedContent, viewModel.ComposerText);
        await ((IAsyncRelayCommand)viewModel.SendCommand).ExecuteAsync(null);

        Assert.Equal(1, session.UploadCalls);
        Assert.Equal(2, session.SentContents.Count);
        Assert.All(
            session.SentContents,
            sent => Assert.Equal(expectedContent, sent));
        Assert.Empty(viewModel.Attachments);
    }

    [Fact]
    public void OpenImageViewerCommand_WhenImageBindingStarts_ViewerIsAlreadyOpen()
    {
        using var viewModel = CreateViewModel(new FakeSession());
        var image = new MessageAttachmentItem("image", "preview.png", "/user_uploads/7/preview.png");
        var bindingStarted = false;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(ShellViewModel.ActiveImageAttachment) ||
                viewModel.ActiveImageAttachment is null) return;
            bindingStarted = true;
            Assert.True(viewModel.IsImageViewerOpen);
        };

        viewModel.OpenImageViewerCommand.Execute(image);

        Assert.True(bindingStarted);
        viewModel.CloseImageViewerCommand.Execute(null);
        Assert.False(viewModel.IsImageViewerOpen);
        Assert.Null(viewModel.ActiveImageAttachment);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectMessageSettings_WhenChanged_PersistLocallyAndPinNavigationItem(bool isSelf)
    {
        var accountId = AccountId.Create(RealmEndpoint.Parse("https://zulip.example"), 7);
        var selected = isSelf ? new DirectMessage([]) : new DirectMessage([8]);
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
                [7] = new UserProfile(7, "Ada", avatarUrl: "https://zulip.example/avatar/7"),
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
        Assert.Equal(isSelf ? "https://zulip.example/avatar/7" : "https://zulip.example/avatar/8", viewModel.DetailsAvatarUrl);
        Assert.True(viewModel.IsSelectedDirectMessageMuted);
        Assert.True(viewModel.IsSelectedDirectMessagePinned);
        Assert.Equal(selected.CanonicalKey, viewModel.DirectMessages.First().Conversation.CanonicalKey);
        Assert.True(viewModel.DirectMessages.First().IsMuted);
        Assert.True(preferences.Get(accountId, selected.CanonicalKey).IsPinned);
        Assert.True(preferences.Get(accountId, selected.CanonicalKey).IsMuted);
        Assert.Equal(new ConversationPreference(), preferences.Get(accountId, other.CanonicalKey));
        Assert.Equal(0, session.SubscriptionPreferenceCalls);

        await viewModel.ToggleDetailsCommand.ExecuteAsync(null);
        await viewModel.ToggleDetailsCommand.ExecuteAsync(null);
        Assert.True(viewModel.IsDetailsOpen);
        Assert.True(viewModel.IsSelectedDirectMessagePinned);
        Assert.True(viewModel.IsSelectedDirectMessageMuted);
    }

    [Fact]
    public async Task ToggleDetails_WhenSelfSelected_OpensOwnProfileWithoutReadingGroupData()
    {
        var reads = 0;
        var session = new FakeSession
        {
            CurrentUserId = 7,
            Selected = new DirectMessage([]),
            StateValue = new ClientState(users: new Dictionary<long, UserProfile>
            {
                [7] = new(7, "Ada", avatarUrl: "https://chat.example.test/avatar/7")
            }),
            LoadChannelDetailsAction = (id, _) => { reads++; return Task.FromResult(PrivateGroupDetails(id, "group", "", 7)); },
            ChannelMemberIdsAction = (_, _) => { reads++; return Task.FromResult<IReadOnlyList<long>>([]); },
            RealmUsersAction = _ => { reads++; return Task.FromResult<IReadOnlyList<UserProfile>>([]); }
        };
        using var viewModel = CreateViewModel(session);

        Assert.True(viewModel.CanOpenConversationSettings);
        await viewModel.ToggleDetailsCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsDetailsOpen);
        Assert.True(viewModel.IsDetailsContentReady);
        Assert.True(viewModel.ShowDirectMessageSettings);
        Assert.False(viewModel.ShowChannelDetails);
        Assert.False(viewModel.IsDetailsLoading);
        Assert.Equal("Ada（自己）", viewModel.DetailsTitle);
        Assert.Equal("仅你自己可见", viewModel.DetailsBody);
        Assert.Equal("https://chat.example.test/avatar/7", viewModel.DetailsAvatarUrl);
        Assert.Equal(0, reads);
        Assert.Empty(viewModel.DetailsMembers);
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
    public async Task ToggleDetails_WhenGroupDirectMessageSelected_DoesNotOpenEmptySettings()
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
    public async Task ToggleDetails_WhenAuthorityIsPending_StartsRosterReadsAndCanCloseImmediately()
    {
        var session = ConversationMenuSession();
        session.Selected = new ChannelTopic(4, string.Empty);
        using var viewModel = CreateViewModel(session);
        var details = new TaskCompletionSource<ChannelDetails>(TaskCreationOptions.RunContinuationsAsynchronously);
        var membersStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var usersStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken loadToken = default;
        session.LoadChannelDetailsAction = (_, token) => { loadToken = token; return details.Task; };
        session.ChannelMemberIdsAction = (_, _) =>
        {
            membersStarted.TrySetResult();
            return Task.FromResult<IReadOnlyList<long>>([7, 8]);
        };
        session.RealmUsersAction = _ =>
        {
            usersStarted.TrySetResult();
            return Task.FromResult<IReadOnlyList<UserProfile>>([new(7, "Me"), new(8, "Bea")]);
        };

        var open = viewModel.ToggleDetailsCommand.ExecuteAsync(null);
        Assert.True(viewModel.IsDetailsOpen);
        await Task.WhenAll(membersStarted.Task, usersStarted.Task).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(open.IsCompleted);
        Assert.True(viewModel.IsDetailsLoading);
        Assert.False(viewModel.IsPrivateGroupAuthorityLoaded);
        Assert.True(viewModel.ToggleDetailsCommand.CanExecute(null));
        await viewModel.ToggleDetailsCommand.ExecuteAsync(null);
        Assert.False(viewModel.IsDetailsOpen);
        Assert.True(loadToken.IsCancellationRequested);

        details.SetResult(PrivateGroupDetails(4, "late-group", "late-announcement", 7));
        await open;
        Assert.False(viewModel.IsDetailsOpen);
        Assert.Empty(viewModel.DetailsMembers);
        Assert.NotEqual("late-group", viewModel.DetailsChannelName);
    }

    [Fact]
    public async Task ToggleDetails_WhenReopenedBeforeOldReadFinishes_KeepsLatestDetails()
    {
        var session = ConversationMenuSession();
        session.Selected = new ChannelTopic(4, string.Empty);
        using var viewModel = CreateViewModel(session);
        var oldDetails = new TaskCompletionSource<ChannelDetails>(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.LoadChannelDetailsAction = (_, _) => { oldStarted.SetResult(); return oldDetails.Task; };
        var oldOpen = viewModel.ToggleDetailsCommand.ExecuteAsync(null);
        await oldStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await viewModel.ToggleDetailsCommand.ExecuteAsync(null);
        session.LoadChannelDetailsAction = (_, _) => Task.FromResult(PrivateGroupDetails(4, "new-group", "new-announcement", 7));
        await viewModel.ToggleDetailsCommand.ExecuteAsync(null);

        oldDetails.SetResult(PrivateGroupDetails(4, "old-group", "old-announcement", 7));
        await oldOpen;

        Assert.True(viewModel.IsDetailsOpen);
        Assert.False(viewModel.IsDetailsLoading);
        Assert.Equal("new-group", viewModel.DetailsChannelName);
        Assert.Equal("new-announcement", viewModel.DetailsChannelAnnouncement);
    }

    [Fact]
    public async Task ToggleDetails_WhenAccountChangesBeforeProjection_IgnoresOldAuthority()
    {
        var session = ConversationMenuSession();
        session.Selected = new ChannelTopic(4, string.Empty);
        using var viewModel = CreateViewModel(session);
        var details = new TaskCompletionSource<ChannelDetails>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.LoadChannelDetailsAction = (_, _) => { started.SetResult(); return details.Task; };
        var open = viewModel.ToggleDetailsCommand.ExecuteAsync(null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        session.Account = AccountId.Create(RealmEndpoint.Parse("https://other.example.test"), 7);
        details.SetResult(PrivateGroupDetails(4, "old-account-group", "old-account-announcement", 7));
        await open;

        Assert.False(viewModel.IsPrivateGroupAuthorityLoaded);
        Assert.NotEqual("old-account-group", viewModel.DetailsChannelName);
        Assert.Empty(viewModel.DetailsMembers);
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

    [Theory]
    [InlineData(59, true, true)]
    [InlineData(60, false, true)]
    [InlineData(120, false, false)]
    public void MessageMenu_WhenServerDeadlinesDiffer_HidesExpiredActions(int age, bool edit, bool delete)
    {
        var time = new MessageActionTestTimeProvider();
        var message = new ChatMessage(1, new DirectMessage([8]), 7, "hello", time.GetUtcNow().AddSeconds(-age));
        var session = new FakeSession
        {
            CurrentUserId = 7,
            StateValue = new ClientState(messages: new Dictionary<long, ChatMessage> { [1] = message },
                connection: new ConnectionState(ConnectionStatus.Connected),
                messageActions: new MessageActionPolicy { EditLimitSeconds = 60, DeleteLimitSeconds = 120 })
        };
        using var viewModel = CreateViewModel(session, timeProvider: time);
        viewModel.OpenMessageMenuCommand.Execute(new MessageItem("1", 1, 7, "Me", "hello", "10:00", isOwn: true));
        Assert.Equal(edit, viewModel.CanEditActiveMessage);
        Assert.Equal(delete, viewModel.CanDeleteActiveMessage);
        viewModel.OpenEditDialogCommand.Execute(null);
        Assert.Equal(edit, viewModel.IsEditDialogOpen);
    }

    [Fact]
    public void MessageMenu_WhenTimeExpiresWhileOpen_UpdatesVisibilityAndDisposesTimerOnClose()
    {
        var time = new MessageActionTestTimeProvider();
        var message = new ChatMessage(1, new DirectMessage([8]), 7, "hello", time.GetUtcNow().AddSeconds(-599));
        var session = new FakeSession
        {
            CurrentUserId = 7,
            StateValue = new ClientState(messages: new Dictionary<long, ChatMessage> { [1] = message },
                connection: new ConnectionState(ConnectionStatus.Connected), messageActions: new MessageActionPolicy())
        };
        using var viewModel = CreateViewModel(session, timeProvider: time);
        viewModel.OpenMessageMenuCommand.Execute(new MessageItem("1", 1, 7, "Me", "hello", "10:00", isOwn: true));
        Assert.True(viewModel.CanEditActiveMessage);
        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Contains(nameof(viewModel.CanEditActiveMessage), changed);
        Assert.Contains(nameof(viewModel.CanDeleteActiveMessage), changed);
        Assert.False(viewModel.CanEditActiveMessage);
        Assert.False(viewModel.CanDeleteActiveMessage);
        viewModel.CloseMessageMenuCommand.Execute(null);
        Assert.True(time.Timer!.Disposed);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task MessageConfirmation_WhenDeadlineExpiresOrPermissionChanges_DoesNotSubmit(bool delete, bool changePermission)
    {
        var time = new MessageActionTestTimeProvider();
        var message = new ChatMessage(1, new DirectMessage([8]), 7, "hello", time.GetUtcNow().AddSeconds(-599));
        var session = new FakeSession
        {
            CurrentUserId = 7,
            StateValue = new ClientState(messages: new Dictionary<long, ChatMessage> { [1] = message },
                connection: new ConnectionState(ConnectionStatus.Connected), messageActions: new MessageActionPolicy())
        };
        using var viewModel = CreateViewModel(session, timeProvider: time);
        var item = new MessageItem("1", 1, 7, "Me", "hello", "10:00", isOwn: true);
        if (delete) viewModel.RequestDeleteMessageCommand.Execute(item);
        else viewModel.OpenEditDialogCommand.Execute(item);
        Assert.True(delete ? viewModel.IsDeleteConfirmationOpen : viewModel.IsEditDialogOpen);
        if (changePermission)
        {
            session.StateValue = session.StateValue with { MessageActions = MessageActionPolicy.Unavailable };
            session.Publish();
        }
        else time.Advance(TimeSpan.FromSeconds(1));
        if (delete) await viewModel.ConfirmDeleteMessageCommand.ExecuteAsync(null);
        else await viewModel.ConfirmEditMessageCommand.ExecuteAsync(null);
        Assert.Equal(0, session.MessageEditCalls);
        Assert.Equal(0, session.MessageDeleteCalls);
        Assert.Contains(delete ? "删除" : "编辑", viewModel.LoginError);
    }

    private sealed class MessageActionTestTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch.AddDays(1);
        public TestTimer? Timer { get; private set; }
        public override DateTimeOffset GetUtcNow() => _now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            Timer = new TestTimer(callback, state);
        public void Advance(TimeSpan duration)
        {
            _now += duration;
            Timer?.Fire();
        }
        public sealed class TestTimer(TimerCallback callback, object? state) : ITimer
        {
            public bool Disposed { get; private set; }
            public void Fire() { if (!Disposed) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => !Disposed;
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ToggleDetails_WhenFirstRenderIsPending_ShowsShellBeforeLoadingContent(bool isGroup)
    {
        var session = ConversationMenuSession();
        session.StateValue = session.StateValue with { Connection = new ConnectionState(ConnectionStatus.Offline) };
        session.Selected = isGroup ? new ChannelTopic(4, string.Empty) : new DirectMessage([8]);
        var dispatcher = new RenderGateDispatcher();
        using var viewModel = CreateViewModel(session, dispatcher: dispatcher);
        var reads = 0;
        session.LoadChannelDetailsAction = (id, _) =>
        {
            reads++;
            return Task.FromResult(PrivateGroupDetails(id, "product", "announcement", 7));
        };
        session.ChannelMemberIdsAction = (_, _) => { reads++; return Task.FromResult<IReadOnlyList<long>>([7, 8]); };
        session.RealmUsersAction = _ => { reads++; return Task.FromResult<IReadOnlyList<UserProfile>>([new(7, "Me"), new(8, "Bea")]); };

        var open = viewModel.ToggleDetailsCommand.ExecuteAsync(null);
        try
        {
            Assert.True(viewModel.IsDetailsOpen);
            Assert.Equal(1, dispatcher.YieldCount);
            Assert.True(viewModel.IsDetailsLoading);
            Assert.False(viewModel.IsDetailsContentReady);
            Assert.False(open.IsCompleted);
            Assert.Equal(0, reads);
            Assert.True(viewModel.ToggleDetailsCommand.CanExecute(null));
        }
        finally
        {
            dispatcher.RenderReady.TrySetResult();
            await open;
        }

        Assert.True(viewModel.IsDetailsOpen);
        Assert.False(viewModel.IsDetailsLoading);
        Assert.True(viewModel.IsDetailsContentReady);
        Assert.Equal(isGroup ? 3 : 0, reads);
        if (isGroup) Assert.Equal(2, viewModel.DetailsMembers.Count);
        else Assert.Equal("Bea", viewModel.DetailsTitle);
    }

    [Theory]
    [InlineData("close")]
    [InlineData("conversation")]
    [InlineData("account")]
    public async Task ToggleDetails_WhenTargetChangesBeforeFirstRender_DoesNotStartOldReads(string change)
    {
        var session = ConversationMenuSession();
        session.StateValue = session.StateValue with { Connection = new ConnectionState(ConnectionStatus.Offline) };
        session.Selected = new ChannelTopic(4, string.Empty);
        var dispatcher = new RenderGateDispatcher();
        using var viewModel = CreateViewModel(session, dispatcher: dispatcher);
        var reads = 0;
        session.LoadChannelDetailsAction = (id, _) =>
        {
            reads++;
            return Task.FromResult(PrivateGroupDetails(id, "stale-group", "stale-announcement", 7));
        };

        var open = viewModel.ToggleDetailsCommand.ExecuteAsync(null);
        try
        {
            Assert.Equal(1, dispatcher.YieldCount);
            if (change == "close")
            {
                await viewModel.ToggleDetailsCommand.ExecuteAsync(null);
                await open.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(viewModel.IsDetailsOpen);
            }
            else if (change == "conversation") session.Selected = new DirectMessage([8]);
            else session.Account = AccountId.Create(RealmEndpoint.Parse("https://other.example.test"), 7);
        }
        finally
        {
            dispatcher.RenderReady.TrySetResult();
            await open;
        }

        Assert.Equal(0, reads);
        Assert.False(viewModel.IsDetailsContentReady);
        Assert.False(viewModel.IsPrivateGroupAuthorityLoaded);
        Assert.Empty(viewModel.DetailsMembers);
        Assert.NotEqual("stale-group", viewModel.DetailsChannelName);
    }

    [Fact]
    public async Task ToggleDetails_WhenReopenedBeforeFirstRender_LoadsOnlyLatestOpening()
    {
        var session = ConversationMenuSession();
        session.StateValue = session.StateValue with { Connection = new ConnectionState(ConnectionStatus.Offline) };
        session.Selected = new ChannelTopic(4, string.Empty);
        var dispatcher = new RenderGateDispatcher();
        using var viewModel = CreateViewModel(session, dispatcher: dispatcher);
        var reads = 0;
        session.LoadChannelDetailsAction = (id, _) =>
        {
            reads++;
            return Task.FromResult(PrivateGroupDetails(id, "latest-group", "announcement", 7));
        };

        var oldOpen = viewModel.ToggleDetailsCommand.ExecuteAsync(null);
        await viewModel.ToggleDetailsCommand.ExecuteAsync(null);
        await oldOpen.WaitAsync(TimeSpan.FromSeconds(5));
        var newOpen = viewModel.ToggleDetailsCommand.ExecuteAsync(null);
        Assert.True(viewModel.IsDetailsOpen);
        Assert.True(viewModel.IsDetailsLoading);
        Assert.False(viewModel.IsDetailsContentReady);
        Assert.Equal(2, dispatcher.YieldCount);
        Assert.Equal(0, reads);

        dispatcher.RenderReady.TrySetResult();
        await newOpen;

        Assert.True(viewModel.IsDetailsOpen);
        Assert.True(viewModel.IsDetailsContentReady);
        Assert.False(viewModel.IsDetailsLoading);
        Assert.Equal("latest-group", viewModel.DetailsChannelName);
        Assert.Equal(1, reads);
    }

    private sealed class RenderGateDispatcher : IUiDispatcher
    {
        public TaskCompletionSource RenderReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int YieldCount { get; private set; }
        public void Dispatch(Action action) => action();
        public Task YieldToRenderAsync(CancellationToken cancellationToken = default)
        {
            YieldCount++;
            return RenderReady.Task.WaitAsync(cancellationToken);
        }
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
        IDownloadHistoryStore? downloadHistoryStore = null,
        TimeProvider? timeProvider = null,
        IUiDispatcher? dispatcher = null,
        IStartupService? startupService = null,
        StickerPickerViewModel? stickers = null) =>
        new(
            session,
            lastRealmStore ?? new FakeLastRealmStore(),
            dispatcher ?? new InlineDispatcher(),
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
            downloadHistoryStore,
            timeProvider,
            startupService, stickers: stickers);

    private sealed class FakeStartupService : IStartupService
    {
        public StartupState State { get; set; }
        public bool ThrowOnRead { get; set; }
        public bool ThrowOnWrite { get; set; }
        public List<bool> Writes { get; } = [];

        public StartupState GetState()
        {
            if (ThrowOnRead) throw new UnauthorizedAccessException("Raw platform failure must not reach UI.");
            return State;
        }

        public void SetEnabled(bool enabled)
        {
            Writes.Add(enabled);
            if (ThrowOnWrite) throw new UnauthorizedAccessException("Raw platform failure must not reach UI.");
            State = enabled ? StartupState.Enabled : StartupState.Disabled;
        }
    }

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
        public event EventHandler<AvatarChangedEventArgs>? AvatarChanged { add { } remove { } }

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
        public Func<CancellationToken, Task<SelectedAttachmentFile?>>? PickAvatarAction { get; set; }
        public Task<SelectedAttachmentFile?> PickAvatarAsync(CancellationToken cancellationToken = default) =>
            PickAvatarAction?.Invoke(cancellationToken) ?? Task.FromResult(Files.FirstOrDefault());
        public Task<IReadOnlyList<SelectedAttachmentFile>> PickMultipleAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Files);
    }

    private sealed class FakePlatformInteractionService : IPlatformInteractionService
    {
        public List<string> Copied { get; } = [];
        public List<Uri> Opened { get; } = [];
        public Func<string, Task>? CopyTextAction { get; set; }
        public Func<Uri, Task>? OpenUriAction { get; set; }

        public Task CopyTextAsync(string text, CancellationToken cancellationToken = default)
        {
            Copied.Add(text);
            return CopyTextAction?.Invoke(text) ?? Task.CompletedTask;
        }

        public Task OpenUriAsync(Uri uri, CancellationToken cancellationToken = default)
        {
            Opened.Add(uri);
            return OpenUriAction?.Invoke(uri) ?? Task.CompletedTask;
        }
    }

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public void Dispatch(Action action) => action();
        public Task YieldToRenderAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
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
        private ConversationKey? _selected;

        public ClientState StateValue { get; set; } = ClientState.Empty;
        public ConversationKey? Selected
        {
            get => _selected;
            set
            {
                _selected = value;
                if (value is null) return;
                Account ??= RelayCove.Core.AccountId.Create(RealmEndpoint.Parse("https://chat.example.test"), 7);
                if (HistoryState.Conversation is not null) return;
                HistoryState = new ConversationHistoryState(
                    value,
                    HistoryState.Generation + 1,
                    false,
                    true,
                    false,
                    null,
                    null);
            }
        }
        public IReadOnlyList<ConversationKey> Recent { get; set; } = [];
        public AccountId? Account { get; set; }
        public Func<string, string, string, CancellationToken, Task>? LoginAction { get; set; }
        public Func<CancellationToken, Task>? LogoutAction { get; set; }
        public Func<ConversationKey, CancellationToken, Task>? SelectAction { get; set; }
        public Func<string, CancellationToken, Task>? SendAction { get; set; }
        public Func<long, bool, CancellationToken, Task>? SetMessageStarredAction { get; set; }
        public Func<AttachmentUpload, CancellationToken, Task<UploadedAttachment>>? UploadAction { get; set; }
        public int AvatarUploadCalls { get; private set; }
        public int CloseConversationCalls { get; private set; }
        public Func<AccountId, ConversationKey, CancellationToken, Task>? CloseConversationAction { get; set; }
        public async Task CloseConversationAsync(AccountId expectedAccountId, ConversationKey conversation, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(Account, expectedAccountId);
            CloseConversationCalls++;
            if (CloseConversationAction is not null) await CloseConversationAction(expectedAccountId, conversation, cancellationToken);
            if (Selected == conversation) Selected = null;
            Publish();
        }
        public int SubscriptionPreferenceCalls { get; private set; }
        public Func<long, SubscriptionPreference, bool, CancellationToken, Task>? SubscriptionPreferenceAction { get; set; }
        public Task SetSubscriptionPreferenceAsync(long channelId, SubscriptionPreference preference, bool value, CancellationToken cancellationToken = default)
        {
            SubscriptionPreferenceCalls++;
            return SubscriptionPreferenceAction?.Invoke(channelId, preference, value, cancellationToken) ?? Task.CompletedTask;
        }
        public int NameUpdateCalls { get; private set; }
        public Func<AccountId, string, CancellationToken, Task<bool>>? UpdateOwnNameAction { get; set; }
        public Task<bool> UpdateOwnNameAsync(AccountId expectedAccountId, string fullName, CancellationToken cancellationToken = default)
        {
            NameUpdateCalls++;
            return UpdateOwnNameAction?.Invoke(expectedAccountId, fullName, cancellationToken) ?? Task.FromResult(true);
        }
        public Func<AccountId, AttachmentUpload, CancellationToken, Task>? UploadAvatarAction { get; set; }
        public Task UploadOwnAvatarAsync(AccountId expectedAccountId, AttachmentUpload upload, CancellationToken cancellationToken = default)
        {
            AvatarUploadCalls++;
            return UploadAvatarAction?.Invoke(expectedAccountId, upload, cancellationToken) ?? Task.CompletedTask;
        }
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
        public List<(string Query, long? BeforeMessageId, MessageSearchFilter Filter, ConversationKey? Conversation)> SearchRequests { get; } = [];

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
            MessageSearchFilter filter = MessageSearchFilter.Messages,
            ConversationKey? conversation = null)
        {
            SearchRequests.Add((query, beforeMessageId, filter, conversation));
            return SearchMessagesWithFilterAction?.Invoke(query, beforeMessageId, limit, filter, cancellationToken) ??
                SearchMessagesAction?.Invoke(query, beforeMessageId, limit, cancellationToken) ??
                Task.FromResult(new MessageQueryPage([], false, true, true));
        }
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
        public List<(long MessageId, EmojiReactionIdentity Identity, bool Add)> ReactionCalls { get; } = [];
        public Task SetReactionAsync(long messageId, EmojiReactionIdentity reaction, bool add, CancellationToken cancellationToken = default)
        {
            ReactionCalls.Add((messageId, reaction, add));
            return Task.CompletedTask;
        }
        public int MessageEditCalls { get; private set; }
        public int MessageDeleteCalls { get; private set; }
        public Task EditMessageAsync(long messageId, string content, CancellationToken cancellationToken = default) { MessageEditCalls++; return Task.CompletedTask; }
        public Task DeleteMessageAsync(long messageId, CancellationToken cancellationToken = default) { MessageDeleteCalls++; return Task.CompletedTask; }
        public Task SetMessageStarredAsync(long messageId, bool isStarred, CancellationToken cancellationToken = default) =>
            SetMessageStarredAction?.Invoke(messageId, isStarred, cancellationToken) ?? Task.CompletedTask;
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

        public void Publish()
        {
            if (Selected is { } selected && HistoryState.Conversation is null)
            {
                HistoryState = new ConversationHistoryState(
                    selected,
                    HistoryState.Generation + 1,
                    false,
                    true,
                    false,
                    StateValue.Messages.Values
                        .Where(message => message.Conversation == selected)
                        .Select(message => (long?)message.Id)
                        .DefaultIfEmpty()
                        .Min(),
                    null);
            }
            StateChanged?.Invoke(this, new ClientStateChangedEventArgs(StateValue));
        }

        public void PublishRealtime(ChatMessage message) =>
            RealtimeMessageReceived?.Invoke(this, new RealtimeMessageReceivedEventArgs(message));
    }
}
