using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RelayCove.Client.Accounts;
using RelayCove.Client.Attachments;
using RelayCove.Client.Mentions;
using RelayCove.Client.Notifications;
using RelayCove.Client.Storage;
using RelayCove.Client.Sync;
using RelayCove.Shared.Messages;

namespace RelayCove.Client;

public partial class MainWindow : Window
{
    private ClientAccountShellCoordinator? accountShell;
    private Guid? pendingConversationSelectionId;
    private long lastConversationRevision;
    private long lastMessageRevision;
    private Guid? displayedMessageConversationId;
    private long? displayedTargetMessageId;
    private ClientMessageListSnapshot? displayedMessageSnapshot;
    private Guid? composerReplyConversationId;
    private long? composerReplyToMessageId;
    private Guid? composerContextConversationId;
    private long composerContextVersion;
    private readonly Dictionary<Guid, MentionCandidateDto> composerMentions = [];
    private readonly List<ClientAttachmentFileSelection> composerAttachments = [];
    private long mentionSearchVersion;
    private long attachmentSubmissionVersion;
    private bool composerContextReady;
    private bool suppressSelectionRequest;
    private bool applyingMessageSnapshot;
    private bool composerAvailable;
    private bool composerSubmissionRunning;
    private bool mentionSearchRunning;
    private bool attachmentSelectionRunning;
    private int lastAnnouncedAttachmentIndex;
    private int lastAnnouncedAttachmentProgressBucket = -1;

    public MainWindow()
    {
        InitializeComponent();
    }

    internal void BindAccountShell(ClientAccountShellCoordinator coordinator)
    {
        accountShell = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        ApplyAccountShellSnapshot(coordinator.Snapshot);
        ApplyConversationListSnapshot(coordinator.ConversationList);
        ApplyMessageListSnapshot(coordinator.MessageList);
    }

    internal Guid? SelectedConversationId =>
        (ConversationList.SelectedItem as ClientConversationListItemPresentation)?.Id;

    internal void ApplyAccountShellSnapshot(ClientAccountShellSnapshot snapshot)
    {
        if (!snapshot.HasActiveAccount)
        {
            pendingConversationSelectionId = null;
            composerAvailable = false;
            MessageComposerTextBox.IsEnabled = false;
            UpdateComposerConversationContext(conversationId: null, isReady: false);
            ClearComposerReply();
            UpdateComposerState();
        }

        var presentation = ClientAccountShellPresenter.Present(snapshot);
        SetLiveText(HeadingText, presentation.Heading);
        SetLiveText(DetailText, presentation.Detail);
        SetLiveText(SidebarConnectionText, presentation.ConnectionLabel);
        SetLiveText(SidebarSyncText, presentation.SyncLabel);
        SetLiveText(SidebarDisplayNameText, string.IsNullOrWhiteSpace(presentation.DisplayName)
            ? "尚未登录"
            : presentation.DisplayName);
        SetLiveText(SidebarServerText, string.IsNullOrWhiteSpace(presentation.ServerAddress)
            ? "—"
            : presentation.ServerAddress);
        SetLiveText(SidebarUnreadText, $"未读计数：{snapshot.TotalUnreadCount}");
        LoginPanel.Visibility = presentation.ShowLogin
            ? Visibility.Visible
            : Visibility.Collapsed;
        AccountPanel.Visibility = presentation.ShowLogin
            ? Visibility.Collapsed
            : Visibility.Visible;
        BusyIndicator.Visibility = presentation.IsBusy
            ? Visibility.Visible
            : Visibility.Collapsed;
        LoginButton.IsEnabled = !presentation.IsBusy;
        ServerAddressTextBox.IsEnabled = !presentation.IsBusy;
        UserNameTextBox.IsEnabled = !presentation.IsBusy;
        PasswordInput.IsEnabled = !presentation.IsBusy;
        RetryButton.IsEnabled = presentation.CanRetry;
        LogoutButton.IsEnabled = presentation.CanLogout;
    }

    internal void ApplyConversationListSnapshot(LocalConversationListReadOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (outcome.Revision < lastConversationRevision)
        {
            return;
        }

        lastConversationRevision = outcome.Revision;
        var previousSelectionId = SelectedConversationId;
        var items = ClientConversationListPresenter.Present(outcome);
        suppressSelectionRequest = true;
        try
        {
            ConversationList.ItemsSource = items;
            ConversationList.IsEnabled = outcome.Status == LocalCacheOperationStatus.Ready;
            ConversationEmptyText.Visibility = items.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            SetLiveText(ConversationEmptyText, outcome.Status switch
            {
                LocalCacheOperationStatus.Ready => "暂无会话",
                LocalCacheOperationStatus.AuthoritativeSnapshotRequired =>
                    "正在等待权威会话同步…",
                LocalCacheOperationStatus.FatalScope =>
                    "本地会话缓存不可用；不会显示旧账户数据。",
                _ => "会话列表暂时不可用，请稍后重试。",
            });

            var selection = ClientConversationListPresenter.ResolveSelection(
                items,
                outcome.Status,
                pendingConversationSelectionId,
                previousSelectionId);
            if (selection.ClearPendingSelection)
            {
                pendingConversationSelectionId = null;
            }

            if (selection.Selection is not null)
            {
                ConversationList.SelectedItem = selection.Selection;
                ConversationList.ScrollIntoView(selection.Selection);
            }

            ApplySelectedConversation(
                ConversationList.SelectedItem as ClientConversationListItemPresentation);
        }
        finally
        {
            suppressSelectionRequest = false;
        }

        accountShell?.SelectConversation(SelectedConversationId);
    }

    internal void ShowAuthorizedNotificationTarget(
        ClientNotificationActivationTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Kind == ClientNotificationActivationKind.Message &&
            target.ConversationId is { } conversationId)
        {
            pendingConversationSelectionId = conversationId;
            suppressSelectionRequest = true;
            var selected = ConversationList.Items
                .OfType<ClientConversationListItemPresentation>()
                .FirstOrDefault(item => item.Id == conversationId);
            try
            {
                if (selected is not null)
                {
                    ConversationList.SelectedItem = selected;
                    ConversationList.ScrollIntoView(selected);
                    pendingConversationSelectionId = null;
                    ApplySelectedConversation(selected);
                }
            }
            finally
            {
                suppressSelectionRequest = false;
            }

            accountShell?.SelectConversation(conversationId, target.MessageId);
        }
        else
        {
            ConversationList.Focus();
        }

        SetLiveText(NavigationNoticeText, target.Kind switch
        {
            ClientNotificationActivationKind.Message =>
                "通知目标已通过账户与缓存授权；正在定位对应消息。",
            _ => "未读通知已通过账户与缓存授权；已打开真实会话列表。",
        });
    }

    internal void ApplyMessageListSnapshot(ClientMessageListSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Revision < lastMessageRevision)
        {
            return;
        }

        var previousItems = MessageList.ItemsSource?
            .OfType<ClientMessageListItemPresentation>()
            .ToArray() ?? Array.Empty<ClientMessageListItemPresentation>();
        var previousOldest = previousItems
            .Select(item => item.ServerMessageId)
            .FirstOrDefault(messageId => messageId.HasValue);
        var previousLatest = previousItems
            .Select(item => item.ServerMessageId)
            .LastOrDefault(messageId => messageId.HasValue);
        var nextOldest = snapshot.Messages
            .Select(item => item.ServerMessageId)
            .FirstOrDefault(messageId => messageId.HasValue);
        var nextLatest = snapshot.LatestMessageId;
        var sameConversation = displayedMessageConversationId == snapshot.ConversationId;
        var contentAppended = sameConversation &&
            snapshot.Messages.Count != 0 &&
            (previousItems.Length == 0 ||
             !previousItems.Any(item =>
                 item.ClientMessageId == snapshot.Messages[^1].ClientMessageId));
        var scrollViewer = FindVisualChild<ScrollViewer>(MessageList);
        var oldOffset = scrollViewer?.VerticalOffset ?? 0;
        var oldExtent = scrollViewer?.ExtentHeight ?? 0;
        var wasNearBottom = IsNearBottom(scrollViewer);
        var targetChanged = snapshot.TargetMessageId.HasValue &&
            (!sameConversation || displayedTargetMessageId != snapshot.TargetMessageId);
        var decision = ClientMessageScrollPolicy.Decide(
            sameConversation,
            previousOldest,
            previousLatest,
            nextOldest,
            nextLatest,
            wasNearBottom,
            snapshot.TargetMessageId,
            targetChanged,
            contentAppended,
            snapshot.Messages.Count != 0);

        applyingMessageSnapshot = true;
        try
        {
            if (!previousItems.SequenceEqual(snapshot.Messages))
            {
                MessageList.ItemsSource = snapshot.Messages;
            }
            MessageList.IsEnabled = snapshot.Status == ClientMessageListStatus.Ready;
            MessageList.Visibility = snapshot.Messages.Count == 0
                ? Visibility.Collapsed
                : Visibility.Visible;
            MessageEmptyText.Visibility = snapshot.Messages.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            SetLiveText(MessageEmptyText, DescribeMessageState(snapshot));
            SetLiveText(MessageStatusText, DescribeMessageLoad(snapshot));
            LoadOlderButton.IsEnabled = snapshot.CanLoadOlder;
            LoadOlderButton.Visibility = snapshot.Status == ClientMessageListStatus.Ready &&
                (snapshot.HasMoreBefore || snapshot.IsLoading)
                ? Visibility.Visible
                : Visibility.Collapsed;
            MessageLoadingBar.Visibility = snapshot.IsLoading
                ? Visibility.Visible
                : Visibility.Collapsed;
            composerAvailable = snapshot.Status == ClientMessageListStatus.Ready &&
                snapshot.ConversationId.HasValue;
            MessageComposerTextBox.IsEnabled = composerAvailable;
            UpdateComposerConversationContext(
                snapshot.ConversationId,
                composerAvailable);
            ReconcileComposerReply(snapshot);
            UpdateComposerState();

            UpdateLayout();
            scrollViewer ??= FindVisualChild<ScrollViewer>(MessageList);
            if (decision.PreservePrependOffset && scrollViewer is not null)
            {
                var extentDelta = Math.Max(0, scrollViewer.ExtentHeight - oldExtent);
                scrollViewer.ScrollToVerticalOffset(oldOffset + extentDelta);
            }
            else if (decision.ScrollToMessageId is { } scrollTarget)
            {
                var targetItem = snapshot.Messages.FirstOrDefault(
                    item => item.ServerMessageId == scrollTarget);
                if (targetItem is not null)
                {
                    MessageList.ScrollIntoView(targetItem);
                }
            }
            else if (decision.ScrollToEnd && snapshot.Messages.Count != 0)
            {
                MessageList.ScrollIntoView(snapshot.Messages[^1]);
            }

            NewMessageIndicatorButton.Visibility = decision.ShowNewMessageIndicator
                ? Visibility.Visible
                : Visibility.Collapsed;
            displayedMessageSnapshot = snapshot;
        }
        finally
        {
            applyingMessageSnapshot = false;
        }

        lastMessageRevision = snapshot.Revision;
        displayedMessageConversationId = snapshot.ConversationId;
        displayedTargetMessageId = snapshot.TargetMessageId;
        if (snapshot.Status == ClientMessageListStatus.Ready &&
            snapshot.ConversationId is { } conversationId)
        {
            accountShell?.AcknowledgeMessageSnapshotApplied(
                conversationId,
                snapshot.Revision,
                decision.ObservedThroughMessageId,
                IsNearBottom(scrollViewer));
        }
    }

    internal void SetNotificationAvailability(bool? isAvailable)
    {
        SetLiveText(
            SidebarNotificationText,
            ClientAccountShellPresenter.DescribeNotificationAvailability(isAvailable));
    }

    private static string DescribeMessageState(ClientMessageListSnapshot snapshot) =>
        snapshot.Status switch
        {
            ClientMessageListStatus.None => "请选择会话",
            ClientMessageListStatus.Loading => "正在读取本地消息…",
            ClientMessageListStatus.Ready => "此会话暂无本地或远端消息。",
            ClientMessageListStatus.AuthoritativeSnapshotRequired =>
                "正在等待权威会话同步；不会显示旧缓存。",
            ClientMessageListStatus.RevokedConversation =>
                "会话访问已撤销；本地内容已隐藏。",
            ClientMessageListStatus.FatalScope =>
                "本地消息缓存不可用；不会显示旧账户数据。",
            ClientMessageListStatus.AuthenticationRequired =>
                "登录状态已失效，请重新登录。",
            ClientMessageListStatus.AccessDenied => "当前账户无权读取该会话。",
            ClientMessageListStatus.ProtocolError => "消息响应不符合协议，已拒绝显示。",
            ClientMessageListStatus.TransientFailure => "消息暂时不可用，请稍后重试。",
            _ => "消息读取失败，请稍后重试。",
        };

    private static string DescribeMessageLoad(ClientMessageListSnapshot snapshot)
    {
        if (snapshot.IsLoading)
        {
            return snapshot.Messages.Count == 0
                ? "正在加载消息…"
                : "正在同步消息窗口…";
        }

        if (snapshot.LastLoadStatus is { } loadStatus &&
            loadStatus != ClientMessageLoadStatus.Completed)
        {
            return loadStatus switch
            {
                ClientMessageLoadStatus.TransientFailure =>
                    "远端历史暂时不可用；当前显示已验证的本地消息。",
                ClientMessageLoadStatus.AccessDenied => "远端拒绝了历史读取。",
                ClientMessageLoadStatus.AccessRevoked => "会话访问已撤销。",
                ClientMessageLoadStatus.AuthenticationRequired => "登录状态已失效。",
                ClientMessageLoadStatus.ProtocolError => "远端历史响应已因协议错误被拒绝。",
                _ => "远端历史读取失败；当前显示已验证的本地消息。",
            };
        }

        if (snapshot.HasMoreAfter)
        {
            return "已定位通知消息；其后仍有更多消息。";
        }

        return snapshot.Messages.Count == 0
            ? "暂无消息"
            : $"已显示 {snapshot.Messages.Count} 条消息";
    }

    private static void SetLiveText(TextBlock textBlock, string value)
    {
        if (string.Equals(textBlock.Text, value, StringComparison.Ordinal))
        {
            return;
        }

        textBlock.Text = value;
        UIElementAutomationPeer.FromElement(textBlock)?.RaiseAutomationEvent(
            AutomationEvents.LiveRegionChanged);
    }

    private async void OnLoginClicked(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var coordinator = accountShell;
        if (coordinator is null)
        {
            return;
        }

        var password = PasswordInput.Password;
        PasswordInput.Clear();
        try
        {
            await coordinator.LoginAsync(
                ServerAddressTextBox.Text,
                UserNameTextBox.Text,
                password);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async void OnRetryClicked(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        try
        {
            if (accountShell is not null)
            {
                await accountShell.RetryAsync();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async void OnLogoutClicked(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        try
        {
            if (accountShell is not null)
            {
                await accountShell.LogoutAsync();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void OnConversationSelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        var selected = ConversationList.SelectedItem as
            ClientConversationListItemPresentation;
        ApplySelectedConversation(selected);
        if (!suppressSelectionRequest)
        {
            accountShell?.SelectConversation(selected?.Id);
        }
    }

    private async void OnLoadOlderMessagesClicked(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        try
        {
            if (accountShell is not null)
            {
                await accountShell.LoadOlderMessagesAsync();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void OnMessageComposerTextChanged(object sender, TextChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        composerContextVersion++;
        ReconcileComposerMentions();
        UpdateComposerState();
    }

    private async void OnSelectAttachmentsClicked(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (!CanSelectAttachments())
        {
            SetLiveText(
                MessageComposerStatusText,
                "请先清空正文和已选 @用户，再选择附件。");
            UpdateComposerState();
            return;
        }

        var selectionConversationId = displayedMessageSnapshot?.ConversationId;
        var selectionContextVersion = composerContextVersion;
        var selectionDraftIds = GetComposerAttachmentDraftIds();

        string[] selectedPaths;
        try
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择要发送的附件",
                Filter = "所有文件|*.*",
                Multiselect = true,
                CheckFileExists = true,
                CheckPathExists = true,
                ValidateNames = true,
            };
            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            selectedPaths = dialog.FileNames;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
                System.Runtime.InteropServices.COMException)
        {
            SetLiveText(MessageComposerStatusText, "无法打开文件选择窗口，请稍后重试。");
            return;
        }

        if (!CanSelectAttachments() ||
            !IsAttachmentComposerContextCurrent(
                selectionConversationId,
                selectionContextVersion,
                selectionDraftIds))
        {
            return;
        }

        attachmentSelectionRunning = true;
        UpdateComposerState();
        SetLiveText(MessageComposerStatusText, "正在检查所选文件，不会把本地路径上传或记录到日志…");
        try
        {
            var outcome = await ClientAttachmentFileSourceFactory.CreateAsync(
                selectedPaths,
                composerAttachments);
            if (!composerAvailable ||
                !IsAttachmentComposerContextCurrent(
                    selectionConversationId,
                    selectionContextVersion,
                    selectionDraftIds))
            {
                return;
            }

            if (outcome.Status != ClientAttachmentFileSelectionStatus.Success)
            {
                SetLiveText(
                    MessageComposerStatusText,
                    DescribeAttachmentSelectionOutcome(outcome.Status));
                return;
            }

            composerAttachments.AddRange(outcome.Selections);
            composerContextVersion++;
            MentionPickerPanel.Visibility = Visibility.Collapsed;
            RefreshSelectedAttachmentPresentation();
            SetLiveText(
                MessageComposerStatusText,
                composerAttachments.All(static attachment => attachment.IsImage)
                    ? $"已选择 {composerAttachments.Count} 个附件，将作为图片消息发送。"
                    : $"已选择 {composerAttachments.Count} 个附件，将作为文件消息发送。");
        }
        catch (OperationCanceledException)
        {
            SetLiveText(MessageComposerStatusText, "文件检查已取消。");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            SetLiveText(MessageComposerStatusText, "无法读取所选文件，请检查文件是否仍然可用。");
        }
        finally
        {
            attachmentSelectionRunning = false;
            UpdateComposerState();
        }
    }

    private void OnRemoveAttachmentClicked(object sender, RoutedEventArgs e)
    {
        _ = e;
        if (composerSubmissionRunning ||
            attachmentSelectionRunning ||
            sender is not System.Windows.Controls.Button
            {
                DataContext: ClientAttachmentFileSelection selection,
            })
        {
            return;
        }

        var removed = composerAttachments.RemoveAll(
            candidate => candidate.DraftId == selection.DraftId);
        if (removed == 0)
        {
            return;
        }

        composerContextVersion++;
        RefreshSelectedAttachmentPresentation();
        SetLiveText(
            MessageComposerStatusText,
            composerAttachments.Count == 0
                ? "已移除全部附件，可以继续编辑文字消息。"
                : $"已保留 {composerAttachments.Count} 个附件。");
        MessageComposerTextBox.Focus();
    }

    private void OnMentionPickerClicked(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (!composerAvailable)
        {
            return;
        }

        MentionPickerPanel.Visibility = MentionPickerPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
        if (MentionPickerPanel.Visibility == Visibility.Visible)
        {
            MentionSearchTextBox.Focus();
        }

        UpdateComposerState();
    }

    private void OnCloseMentionPickerClicked(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        MentionPickerPanel.Visibility = Visibility.Collapsed;
        MessageComposerTextBox.Focus();
    }

    private void OnMentionSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        UpdateComposerState();
    }

    private async void OnMentionSearchPreviewKeyDown(
        object sender,
        System.Windows.Input.KeyEventArgs e)
    {
        _ = sender;
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        await SearchMentionCandidatesAsync();
    }

    private async void OnMentionSearchClicked(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        await SearchMentionCandidatesAsync();
    }

    private async Task SearchMentionCandidatesAsync()
    {
        var query = MentionSearchTextBox.Text;
        var conversationId = composerContextConversationId;
        if (accountShell is null ||
            mentionSearchRunning ||
            !composerAvailable ||
            !conversationId.HasValue ||
            !ClientMentionPolicy.IsValidQuery(query))
        {
            SetLiveText(
                MentionSearchStatusText,
                "请输入 1–64 位 ASCII 字母、数字、点、下划线或连字符前缀。");
            UpdateComposerState();
            return;
        }

        var searchVersion = ++mentionSearchVersion;
        mentionSearchRunning = true;
        MentionCandidateList.ItemsSource = null;
        SetLiveText(MentionSearchStatusText, "正在搜索当前会话候选…");
        UpdateComposerState();
        try
        {
            var outcome = await accountShell.SearchMentionCandidatesAsync(query);
            if (searchVersion != mentionSearchVersion ||
                !composerAvailable ||
                composerContextConversationId != conversationId)
            {
                return;
            }

            if (outcome.Status == ClientMentionCandidateStatus.Completed)
            {
                MentionCandidateList.ItemsSource = outcome.Candidates;
                SetLiveText(
                    MentionSearchStatusText,
                    outcome.Candidates.Count == 0
                        ? "当前会话没有匹配候选。"
                        : outcome.HasMore
                            ? $"显示前 {outcome.Candidates.Count} 个候选，请继续缩小前缀。"
                            : $"找到 {outcome.Candidates.Count} 个候选。");
            }
            else
            {
                MentionCandidateList.ItemsSource = null;
                SetLiveText(MentionSearchStatusText, DescribeMentionSearchOutcome(outcome));
            }
        }
        catch (OperationCanceledException)
        {
            if (searchVersion == mentionSearchVersion)
            {
                SetLiveText(MentionSearchStatusText, "候选搜索已取消。");
            }
        }
        catch (ObjectDisposedException)
        {
            if (searchVersion == mentionSearchVersion)
            {
                SetLiveText(MentionSearchStatusText, "账户已结束，无法继续搜索。");
            }
        }
        finally
        {
            if (searchVersion == mentionSearchVersion)
            {
                mentionSearchRunning = false;
                UpdateComposerState();
            }
        }
    }

    private void OnMentionCandidateClicked(object sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not System.Windows.Controls.Button
            {
                DataContext: MentionCandidateDto candidate,
            } ||
            MentionCandidateList.ItemsSource is not IEnumerable<MentionCandidateDto> candidates ||
            !candidates.Any(value => value == candidate) ||
            !composerAvailable ||
            (!composerMentions.ContainsKey(candidate.UserId) &&
             composerMentions.Count >= ClientMentionPolicy.MaximumMentionCount))
        {
            SetLiveText(MentionSearchStatusText, "最多选择 20 个提及用户。");
            return;
        }

        if (ClientMentionPolicy.ContainsToken(MessageComposerTextBox.Text, candidate.UserName))
        {
            if (!composerMentions.ContainsKey(candidate.UserId))
            {
                composerMentions[candidate.UserId] = candidate;
                composerContextVersion++;
                RefreshSelectedMentionPresentation();
            }

            SetLiveText(MentionSearchStatusText, "正文中已有 token，已关联该提及。");
            MessageComposerTextBox.Focus();
            UpdateComposerState();
            return;
        }

        if (!ClientMentionPolicy.TryInsertToken(
                MessageComposerTextBox.Text,
                MessageComposerTextBox.SelectionStart,
                MessageComposerTextBox.SelectionLength,
                candidate.UserName,
                out var edit))
        {
            SetLiveText(MentionSearchStatusText, "无法插入该候选，请重新搜索。");
            return;
        }

        MessageComposerTextBox.Text = edit.Text;
        MessageComposerTextBox.SelectionStart = edit.CaretIndex;
        MessageComposerTextBox.SelectionLength = 0;
        composerMentions[candidate.UserId] = candidate;
        composerContextVersion++;
        RefreshSelectedMentionPresentation();
        SetLiveText(MentionSearchStatusText, "已插入提及；编辑或删除 token 会同步更新发送集合。");
        MessageComposerTextBox.Focus();
        UpdateComposerState();
    }

    private void OnRemoveMentionClicked(object sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not System.Windows.Controls.Button
            {
                DataContext: MentionCandidateDto candidate,
            } ||
            !composerMentions.Remove(candidate.UserId))
        {
            return;
        }

        composerContextVersion++;
        RefreshSelectedMentionPresentation();
        SetLiveText(
            MentionSearchStatusText,
            "已移除提及 ID；正文 token 保留为普通文字。");
        UpdateComposerState();
    }

    private async void OnMessageComposerPreviewKeyDown(
        object sender,
        System.Windows.Input.KeyEventArgs e)
    {
        _ = sender;
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            var start = MessageComposerTextBox.SelectionStart;
            MessageComposerTextBox.SelectedText = Environment.NewLine;
            MessageComposerTextBox.SelectionStart = start + Environment.NewLine.Length;
            MessageComposerTextBox.SelectionLength = 0;
            return;
        }

        await SendComposedMessageAsync();
    }

    private async void OnSendMessageClicked(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        await SendComposedMessageAsync();
    }

    private void OnReplyMessageClicked(object sender, RoutedEventArgs e)
    {
        _ = e;
        if ((composerSubmissionRunning && composerAttachments.Count != 0) ||
            sender is not System.Windows.Controls.Button
            {
                DataContext: ClientMessageListItemPresentation item,
                Tag: long messageId,
            } ||
            !item.CanReply ||
            item.ServerMessageId != messageId ||
            displayedMessageSnapshot is not
            {
                Status: ClientMessageListStatus.Ready,
                ConversationId: { } conversationId,
            } snapshot ||
            !snapshot.Messages.Any(candidate => candidate.ServerMessageId == messageId))
        {
            return;
        }

        composerReplyConversationId = conversationId;
        composerReplyToMessageId = messageId;
        composerContextVersion++;
        SetLiveText(ReplyComposerSenderText, $"正在回复 {item.SenderLabel}");
        SetLiveText(ReplyComposerContentText, item.Content);
        ReplyComposerPanel.Visibility = Visibility.Visible;
        MessageComposerTextBox.Focus();
    }

    private void OnCopyMessageClicked(object sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not System.Windows.Controls.Button
            {
                DataContext: ClientMessageListItemPresentation item,
            } ||
            displayedMessageSnapshot is not { } snapshot ||
            !ClientMessageCopyPolicy.TryResolveContent(snapshot, item, out var content))
        {
            return;
        }

        var copied = ClientClipboardWriter.TryWrite(
            content,
            static value => System.Windows.Clipboard.SetText(
                value,
                System.Windows.TextDataFormat.UnicodeText));
        SetLiveText(
            MessageComposerStatusText,
            copied ? "消息正文已复制。" : "剪贴板暂时不可用，请稍后重试。");
    }

    private void OnMessageLinkClicked(object sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not System.Windows.Controls.Button
            {
                DataContext: ClientMessageLinkPresentation link,
            } ||
            displayedMessageSnapshot is not { } snapshot ||
            !ClientMessageLinkPolicy.IsCurrent(snapshot, link))
        {
            return;
        }

        var opened = ClientExternalLinkLauncher.TryOpen(
            link,
            static startInfo => _ = Process.Start(startInfo));
        SetLiveText(
            MessageComposerStatusText,
            opened ? "已交给系统浏览器打开链接。" : "无法打开链接，请检查系统浏览器设置。");
    }

    private void OnReplyReferenceClicked(object sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not System.Windows.Controls.Button
            {
                DataContext: ClientMessageListItemPresentation item,
                Tag: long messageId,
            } ||
            !item.HasReply ||
            item.ReplyToMessageId != messageId ||
            displayedMessageSnapshot is not
            {
                Status: ClientMessageListStatus.Ready,
                ConversationId: { } conversationId,
            } snapshot ||
            !snapshot.Messages.Any(candidate => candidate.ClientMessageId == item.ClientMessageId))
        {
            return;
        }

        SetLiveText(NavigationNoticeText, item.IsReplyTargetAvailable
            ? "正在定位被回复的消息。"
            : "原消息尚未加载；正在从服务器定位。");
        accountShell?.SelectConversation(conversationId, messageId);
    }

    private void OnCancelReplyClicked(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (composerSubmissionRunning && composerAttachments.Count != 0)
        {
            return;
        }

        ClearComposerReply();
        MessageComposerTextBox.Focus();
    }

    private async void OnRetryPendingMessageClicked(object sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not System.Windows.Controls.Button { Tag: Guid clientMessageId } ||
            accountShell is null)
        {
            return;
        }

        SetLiveText(MessageComposerStatusText, "正在重试失败消息…");
        var outcome = await accountShell.RetryPendingMessageAsync(clientMessageId);
        SetLiveText(MessageComposerStatusText, DescribeSendOutcome(outcome, isRetry: true));
    }

    private Task SendComposedMessageAsync() =>
        composerAttachments.Count == 0
            ? SendComposedTextMessageAsync()
            : SendComposedAttachmentsAsync();

    private async Task SendComposedTextMessageAsync()
    {
        if (accountShell is null ||
            composerSubmissionRunning ||
            !composerAvailable ||
            !ClientTextMessageContentValidator.IsValid(MessageComposerTextBox.Text))
        {
            UpdateComposerState();
            return;
        }

        var submittedContent = MessageComposerTextBox.Text;
        var submittedConversationId = displayedMessageSnapshot?.ConversationId;
        var submittedReplyToMessageId = composerReplyToMessageId;
        if (!ClientMentionPolicy.TryCanonicalizeUserIds(
                composerMentions.Keys.ToArray(),
                out var submittedMentionUserIds))
        {
            SetLiveText(MessageComposerStatusText, "提及用户集合无效，请重新选择。");
            UpdateComposerState();
            return;
        }

        var submittedContextVersion = composerContextVersion;
        if (submittedReplyToMessageId.HasValue &&
            composerReplyConversationId != submittedConversationId)
        {
            ClearComposerReply();
            UpdateComposerState();
            return;
        }

        composerSubmissionRunning = true;
        UpdateComposerState();
        SetLiveText(MessageComposerStatusText, "正在持久化并发送…");
        try
        {
            var outcome = await accountShell.SendTextMessageAsync(
                submittedContent,
                submittedReplyToMessageId,
                submittedMentionUserIds);
            var replyContextUnchanged = submittedReplyToMessageId.HasValue
                ? composerReplyConversationId == submittedConversationId &&
                  composerReplyToMessageId == submittedReplyToMessageId
                : !composerReplyToMessageId.HasValue;
            var mentionContextUnchanged =
                ClientMentionPolicy.TryCanonicalizeUserIds(
                    composerMentions.Keys.ToArray(),
                    out var currentMentionUserIds) &&
                currentMentionUserIds.SequenceEqual(submittedMentionUserIds);
            if (outcome.PendingCommitted &&
                displayedMessageSnapshot?.ConversationId == submittedConversationId &&
                composerContextVersion == submittedContextVersion &&
                replyContextUnchanged &&
                mentionContextUnchanged &&
                string.Equals(
                    MessageComposerTextBox.Text,
                    submittedContent,
                    StringComparison.Ordinal))
            {
                MessageComposerTextBox.Clear();
                ClearComposerMentions(closePicker: true);
                ClearComposerReply();
            }

            SetLiveText(MessageComposerStatusText, DescribeSendOutcome(outcome, isRetry: false));
        }
        catch (OperationCanceledException)
        {
            SetLiveText(MessageComposerStatusText, "发送已取消；已落盘消息会保留当前状态。");
        }
        catch (ObjectDisposedException)
        {
            SetLiveText(MessageComposerStatusText, "账户已结束，无法继续发送。");
        }
        finally
        {
            composerSubmissionRunning = false;
            UpdateComposerState();
        }
    }

    private async Task SendComposedAttachmentsAsync()
    {
        if (accountShell is null ||
            composerSubmissionRunning ||
            attachmentSelectionRunning ||
            !composerAvailable ||
            composerAttachments.Count == 0 ||
            MessageComposerTextBox.Text.Length != 0 ||
            composerMentions.Count != 0)
        {
            UpdateComposerState();
            return;
        }

        var submittedAttachments = composerAttachments.ToArray();
        var submittedSources = submittedAttachments
            .Select(static attachment => attachment.Source)
            .ToArray();
        var submittedDraftIds = submittedAttachments
            .Select(static attachment => attachment.DraftId)
            .ToArray();
        var submittedType = ClientAttachmentFileSourceFactory.ResolveMessageType(
            submittedAttachments);
        var submittedConversationId = displayedMessageSnapshot?.ConversationId;
        var submittedReplyToMessageId = composerReplyToMessageId;
        var submittedContextVersion = composerContextVersion;
        if (submittedReplyToMessageId.HasValue &&
            composerReplyConversationId != submittedConversationId)
        {
            ClearComposerReply();
            UpdateComposerState();
            return;
        }

        composerSubmissionRunning = true;
        var submissionVersion = ++attachmentSubmissionVersion;
        lastAnnouncedAttachmentIndex = 0;
        lastAnnouncedAttachmentProgressBucket = -1;
        AttachmentUploadProgressBar.Value = 0;
        AttachmentUploadProgressPanel.Visibility = Visibility.Visible;
        SetLiveText(AttachmentUploadProgressText, "正在准备附件上传…");
        SetLiveText(
            MessageComposerStatusText,
            "正在上传附件；进度表示客户端复制到 HTTP 请求的文件字节。");
        UpdateComposerState();

        var progress = new Progress<ClientAttachmentSendProgress>(value =>
            ApplyAttachmentSendProgressSafely(
                value,
                submissionVersion,
                submittedContextVersion,
                submittedConversationId,
                submittedDraftIds));
        try
        {
            var outcome = await accountShell.SendAttachmentsAsync(
                submittedType,
                submittedSources,
                submittedReplyToMessageId,
                Array.Empty<Guid>(),
                progress: progress);
            var contextUnchanged = IsAttachmentComposerContextCurrent(
                submittedConversationId,
                submittedContextVersion,
                submittedDraftIds);
            if (ClientAttachmentComposerContextPolicy.ShouldClearCommittedDraft(
                    outcome.PendingCommitted,
                    contextUnchanged))
            {
                ClearComposerAttachments();
                ClearComposerReply();
            }

            if (contextUnchanged)
            {
                SetLiveText(
                    MessageComposerStatusText,
                    DescribeAttachmentSendOutcome(outcome));
            }
        }
        catch (OperationCanceledException)
        {
            if (IsAttachmentComposerContextCurrent(
                    submittedConversationId,
                    submittedContextVersion,
                    submittedDraftIds))
            {
                SetLiveText(
                    MessageComposerStatusText,
                    "附件发送已取消；上传前选择仍会保留，已落盘消息会保留当前状态。");
            }
        }
        catch (ObjectDisposedException)
        {
            if (IsAttachmentComposerContextCurrent(
                    submittedConversationId,
                    submittedContextVersion,
                    submittedDraftIds))
            {
                SetLiveText(MessageComposerStatusText, "账户已结束，无法继续发送附件。");
            }
        }
        finally
        {
            attachmentSubmissionVersion++;
            composerSubmissionRunning = false;
            ResetAttachmentUploadProgress();
            UpdateComposerState();
        }
    }

    private bool IsAttachmentComposerContextCurrent(
        Guid? submittedConversationId,
        long submittedContextVersion,
        IReadOnlyList<Guid> submittedDraftIds) =>
        ClientAttachmentComposerContextPolicy.IsCurrent(
            submittedConversationId,
            submittedContextVersion,
            submittedDraftIds,
            displayedMessageSnapshot?.ConversationId,
            composerContextVersion,
            GetComposerAttachmentDraftIds());

    private Guid[] GetComposerAttachmentDraftIds() =>
        composerAttachments.Select(static attachment => attachment.DraftId).ToArray();

    private void UpdateComposerState()
    {
        var hasAttachments = composerAttachments.Count != 0;
        SendMessageButton.IsEnabled = composerAvailable &&
            !composerSubmissionRunning &&
            !attachmentSelectionRunning &&
            (hasAttachments ||
             ClientTextMessageContentValidator.IsValid(MessageComposerTextBox.Text));
        MessageComposerTextBox.IsEnabled = composerAvailable &&
            !attachmentSelectionRunning &&
            !hasAttachments;
        SelectAttachmentsButton.IsEnabled = CanSelectAttachments();
        MentionPickerButton.IsEnabled = composerAvailable &&
            !composerSubmissionRunning &&
            !attachmentSelectionRunning &&
            !hasAttachments;
        MentionSearchTextBox.IsEnabled = composerAvailable &&
            !mentionSearchRunning &&
            !attachmentSelectionRunning &&
            !hasAttachments;
        MentionSearchButton.IsEnabled = composerAvailable &&
            !mentionSearchRunning &&
            !attachmentSelectionRunning &&
            !hasAttachments &&
            ClientMentionPolicy.IsValidQuery(MentionSearchTextBox.Text);
        SelectedAttachmentPanel.IsEnabled = !composerSubmissionRunning &&
            !attachmentSelectionRunning;
        ReplyComposerPanel.IsEnabled = !hasAttachments || !composerSubmissionRunning;
    }

    private bool CanSelectAttachments() =>
        composerAvailable &&
        !composerSubmissionRunning &&
        !attachmentSelectionRunning &&
        MessageComposerTextBox.Text.Length == 0 &&
        composerMentions.Count == 0 &&
        composerAttachments.Count < ClientAttachmentMetadataPolicy.MaximumAttachmentsPerMessage;

    private void ApplyAttachmentSendProgressSafely(
        ClientAttachmentSendProgress progress,
        long submissionVersion,
        long submittedContextVersion,
        Guid? submittedConversationId,
        IReadOnlyList<Guid> submittedDraftIds)
    {
        try
        {
            ApplyAttachmentSendProgress(
                progress,
                submissionVersion,
                submittedContextVersion,
                submittedConversationId,
                submittedDraftIds);
        }
        catch (Exception exception) when (!IsCriticalException(exception))
        {
            Debug.WriteLine(
                $"Attachment progress presentation failed: {exception.GetType().Name}.");
        }
    }

    private void ApplyAttachmentSendProgress(
        ClientAttachmentSendProgress progress,
        long submissionVersion,
        long submittedContextVersion,
        Guid? submittedConversationId,
        IReadOnlyList<Guid> submittedDraftIds)
    {
        var contextCurrent = IsAttachmentComposerContextCurrent(
            submittedConversationId,
            submittedContextVersion,
            submittedDraftIds);
        if (!ClientAttachmentComposerContextPolicy.CanApplyProgress(
                composerSubmissionRunning,
                attachmentSubmissionVersion,
                submissionVersion,
                contextCurrent))
        {
            return;
        }

        AttachmentUploadProgressPanel.Visibility = Visibility.Visible;
        AttachmentUploadProgressBar.Value = progress.Percent;
        if (progress.Stage == ClientAttachmentSendProgressStage.Finalizing)
        {
            if (lastAnnouncedAttachmentProgressBucket != 10)
            {
                lastAnnouncedAttachmentProgressBucket = 10;
                SetLiveText(
                    AttachmentUploadProgressText,
                    "服务器已接受全部附件，正在创建并发送消息…");
            }

            return;
        }

        var bucket = progress.Percent / 10;
        if (progress.AttachmentIndex == lastAnnouncedAttachmentIndex &&
            bucket == lastAnnouncedAttachmentProgressBucket)
        {
            return;
        }

        lastAnnouncedAttachmentIndex = progress.AttachmentIndex;
        lastAnnouncedAttachmentProgressBucket = bucket;
        SetLiveText(
            AttachmentUploadProgressText,
            $"正在复制第 {progress.AttachmentIndex}/{progress.AttachmentCount} 个附件到上传请求… " +
            $"{progress.Percent}%");
    }

    private static bool IsCriticalException(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;

    private void RefreshSelectedAttachmentPresentation()
    {
        var snapshot = composerAttachments.ToList().AsReadOnly();
        SelectedAttachmentList.ItemsSource = null;
        SelectedAttachmentList.ItemsSource = snapshot;
        SelectedAttachmentPanel.Visibility = snapshot.Count == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
        var typeLabel = snapshot.Count == 0
            ? "附件"
            : snapshot.All(static attachment => attachment.IsImage)
                ? "图片消息"
                : "文件消息";
        SetLiveText(
            SelectedAttachmentHeadingText,
            $"已选 {snapshot.Count}/{ClientAttachmentMetadataPolicy.MaximumAttachmentsPerMessage} 个附件 · {typeLabel}");
    }

    private void ClearComposerAttachments()
    {
        if (composerAttachments.Count != 0)
        {
            composerContextVersion++;
        }

        composerAttachments.Clear();
        SelectedAttachmentList.ItemsSource = null;
        SelectedAttachmentPanel.Visibility = Visibility.Collapsed;
        SetLiveText(SelectedAttachmentHeadingText, "已选 0/10 个附件");
        ResetAttachmentUploadProgress();
    }

    private void ResetAttachmentUploadProgress()
    {
        lastAnnouncedAttachmentIndex = 0;
        lastAnnouncedAttachmentProgressBucket = -1;
        AttachmentUploadProgressBar.Value = 0;
        AttachmentUploadProgressPanel.Visibility = Visibility.Collapsed;
        AttachmentUploadProgressText.Text = "准备附件上传…";
    }

    private static string DescribeAttachmentSelectionOutcome(
        ClientAttachmentFileSelectionStatus status) =>
        status switch
        {
            ClientAttachmentFileSelectionStatus.NoFilesSelected => "没有选择文件。",
            ClientAttachmentFileSelectionStatus.TooManyFiles => "一条消息最多选择 10 个附件。",
            ClientAttachmentFileSelectionStatus.DuplicateFile => "同一文件不能在一条消息中重复选择。",
            ClientAttachmentFileSelectionStatus.InvalidPath or
                ClientAttachmentFileSelectionStatus.FileNotFound =>
                "所选文件路径无效或文件已不存在。",
            ClientAttachmentFileSelectionStatus.FileUnavailable =>
                "所选文件暂时无法读取，请检查权限或是否被其他程序占用。",
            ClientAttachmentFileSelectionStatus.InvalidFileName =>
                "所选文件名不符合安全展示规则。",
            ClientAttachmentFileSelectionStatus.EmptyFile => "不能发送空文件。",
            ClientAttachmentFileSelectionStatus.FileTooLarge => "单个附件不能超过 100 MiB。",
            ClientAttachmentFileSelectionStatus.Canceled => "文件检查已取消。",
            _ => "无法使用所选文件，请重新选择。",
        };

    private static string DescribeAttachmentSendOutcome(ClientMessageSendOutcome outcome) =>
        outcome.Status switch
        {
            ClientMessageSendStatus.Completed => "附件发送成功。",
            ClientMessageSendStatus.ValidationFailed => "附件选择或回复上下文无效，请重新检查。",
            ClientMessageSendStatus.AttachmentTooLarge =>
                "服务器拒绝了附件大小；服务器限制可能低于客户端的 100 MiB 上限。",
            ClientMessageSendStatus.SourceUnavailable =>
                "本地文件无法重新打开或选择后已发生变化，请移除并重新选择。",
            ClientMessageSendStatus.AuthenticationRequired => "登录已失效，请重新登录。",
            ClientMessageSendStatus.AccessRevoked => "会话访问已撤销。",
            ClientMessageSendStatus.AccessDenied => "当前账户无权发送到此会话。",
            ClientMessageSendStatus.TransientFailure when outcome.PendingCommitted =>
                "消息网络结果不确定；失败行已保留，点击重试不会重新上传附件。",
            ClientMessageSendStatus.TransientFailure =>
                "上传结果不确定且未自动重传；选择已保留，显式再次发送会重新上传。",
            ClientMessageSendStatus.IdempotencyConflict or
                ClientMessageSendStatus.ProtocolError when outcome.PendingCommitted =>
                "消息状态冲突；失败行已保留供检查，不会自动重新上传。",
            ClientMessageSendStatus.IdempotencyConflict or
                ClientMessageSendStatus.ProtocolError =>
                "附件响应不符合协议；选择已保留，未自动重新上传。",
            ClientMessageSendStatus.RemoteFailure =>
                "附件上传被远端拒绝；选择已保留，请检查后显式重试。",
            ClientMessageSendStatus.LocalCacheFailure when outcome.PendingCommitted =>
                "本地状态异常；已落盘消息保留当前状态，不会自动重新上传。",
            ClientMessageSendStatus.LocalCacheFailure =>
                "本地附件状态暂不可用；选择已保留，请稍后重试。",
            ClientMessageSendStatus.CapacityExceeded =>
                "此会话已有 50 条待处理消息，请先处理失败项。",
            ClientMessageSendStatus.Unavailable => "请先选择可用会话。",
            ClientMessageSendStatus.Canceled when outcome.PendingCommitted =>
                "发送已取消；已落盘消息会保留当前状态并可原键重试。",
            ClientMessageSendStatus.Canceled =>
                "上传已取消；选择已保留，未自动重新上传。",
            _ => outcome.PendingCommitted
                ? "附件消息发送失败；失败行已保留且不会重新上传。"
                : "附件上传失败；选择已保留且未自动重传。",
        };

    private static string DescribeSendOutcome(
        ClientMessageSendOutcome outcome,
        bool isRetry) =>
        outcome.Status switch
        {
            ClientMessageSendStatus.Completed => isRetry ? "重试发送成功。" : "发送成功。",
            ClientMessageSendStatus.ValidationFailed =>
                "消息正文或提及用户无效；请检查正文字符与当前候选后重试。",
            ClientMessageSendStatus.AuthenticationRequired => "登录已失效，请重新登录。",
            ClientMessageSendStatus.AccessRevoked => "会话访问已撤销。",
            ClientMessageSendStatus.AccessDenied => "当前账户无权发送到此会话。",
            ClientMessageSendStatus.IdempotencyConflict or
                ClientMessageSendStatus.ProtocolError => "消息状态冲突；已保留失败行供检查。",
            ClientMessageSendStatus.TransientFailure =>
                "网络结果不确定；未自动重发，请点击失败行的“重试”。",
            ClientMessageSendStatus.CapacityExceeded =>
                "此会话已有 50 条待处理消息，请先处理失败项。",
            ClientMessageSendStatus.NotRetryable => "该消息当前不可重试。",
            ClientMessageSendStatus.Unavailable => "请先选择可用会话。",
            ClientMessageSendStatus.Canceled => "发送已取消；已落盘消息会保留当前状态。",
            _ => "发送失败；已落盘消息会显示为失败并可重试。",
        };

    private static string DescribeMentionSearchOutcome(
        ClientMentionCandidateOutcome outcome) =>
        outcome.Status switch
        {
            ClientMentionCandidateStatus.ValidationFailed =>
                "请输入有效的用户名字符前缀。",
            ClientMentionCandidateStatus.AuthenticationRequired => "登录已失效，请重新登录。",
            ClientMentionCandidateStatus.AccessRevoked => "会话访问已撤销。",
            ClientMentionCandidateStatus.AccessDenied => "当前账户无权搜索此会话。",
            ClientMentionCandidateStatus.TransientFailure => "网络暂时不可用，请稍后重试。",
            ClientMentionCandidateStatus.ProtocolError => "候选响应无效，已拒绝显示。",
            ClientMentionCandidateStatus.Canceled => "候选搜索已取消。",
            ClientMentionCandidateStatus.Stale => "会话已切换，旧候选结果已丢弃。",
            ClientMentionCandidateStatus.Unavailable => "请先选择可用会话。",
            _ => "候选搜索失败，请稍后重试。",
        };

    private void OnMessageScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        _ = sender;
        if (applyingMessageSnapshot)
        {
            return;
        }

        var scrollViewer = e.OriginalSource as ScrollViewer ??
            FindVisualChild<ScrollViewer>(MessageList);
        if (scrollViewer is null)
        {
            return;
        }

        var isAtLatestRegion = IsNearBottom(scrollViewer);
        if (isAtLatestRegion)
        {
            NewMessageIndicatorButton.Visibility = Visibility.Collapsed;
        }

        var snapshot = displayedMessageSnapshot;
        if (snapshot?.Status == ClientMessageListStatus.Ready &&
            snapshot.ConversationId is { } conversationId)
        {
            accountShell?.AcknowledgeMessageViewportChanged(
                conversationId,
                snapshot.Revision,
                isAtLatestRegion ? snapshot.LatestMessageId : null,
                isAtLatestRegion);
        }
    }

    private void OnNewMessagesClicked(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var snapshot = displayedMessageSnapshot;
        if (snapshot?.Status != ClientMessageListStatus.Ready ||
            snapshot.ConversationId is not { } conversationId ||
            snapshot.Messages.Count == 0)
        {
            return;
        }

        MessageList.ScrollIntoView(snapshot.Messages[^1]);
        NewMessageIndicatorButton.Visibility = Visibility.Collapsed;
        accountShell?.AcknowledgeMessageViewportChanged(
            conversationId,
            snapshot.Revision,
            snapshot.LatestMessageId,
            isAtLatestRegion: true);
    }

    private void ApplySelectedConversation(
        ClientConversationListItemPresentation? selected)
    {
        if (composerContextConversationId != selected?.Id)
        {
            UpdateComposerConversationContext(selected?.Id, isReady: false);
        }

        if (composerReplyConversationId.HasValue &&
            composerReplyConversationId != selected?.Id)
        {
            ClearComposerReply();
        }

        SetLiveText(
            ConversationHeadingText,
            selected is null ? "请选择会话" : selected.Name);
        SetLiveText(
            NavigationNoticeText,
            selected is null
                ? "选择左侧真实会话以查看消息。"
                : $"已选择{selected.TypeLabel}；正在读取账户隔离的真实消息。");
    }

    private void ReconcileComposerReply(ClientMessageListSnapshot snapshot)
    {
        if (!composerReplyToMessageId.HasValue)
        {
            return;
        }

        if (snapshot.Status != ClientMessageListStatus.Ready ||
            snapshot.ConversationId != composerReplyConversationId ||
            !snapshot.Messages.Any(item =>
                item.ServerMessageId == composerReplyToMessageId.Value &&
                item.CanReply))
        {
            ClearComposerReply();
        }
    }

    private void ClearComposerReply()
    {
        if (composerReplyConversationId.HasValue || composerReplyToMessageId.HasValue)
        {
            composerContextVersion++;
        }

        composerReplyConversationId = null;
        composerReplyToMessageId = null;
        ReplyComposerPanel.Visibility = Visibility.Collapsed;
        SetLiveText(ReplyComposerSenderText, "正在回复");
        SetLiveText(ReplyComposerContentText, string.Empty);
    }

    private void ReconcileComposerMentions()
    {
        var removed = composerMentions.Values
            .Where(candidate => !ClientMentionPolicy.ContainsToken(
                MessageComposerTextBox.Text,
                candidate.UserName))
            .Select(candidate => candidate.UserId)
            .ToArray();
        foreach (var userId in removed)
        {
            composerMentions.Remove(userId);
        }

        if (removed.Length != 0)
        {
            RefreshSelectedMentionPresentation();
        }
    }

    private void RefreshSelectedMentionPresentation()
    {
        var selected = composerMentions.Values
            .OrderBy(candidate => candidate.UserName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.UserId)
            .ToList()
            .AsReadOnly();
        SelectedMentionList.ItemsSource = selected;
        SetLiveText(
            SelectedMentionHeadingText,
            $"已选 {selected.Count}/{ClientMentionPolicy.MaximumMentionCount}");
    }

    private void ClearComposerMentions(bool closePicker)
    {
        var hadSelectedMentions = composerMentions.Count != 0;
        mentionSearchVersion++;
        mentionSearchRunning = false;
        composerMentions.Clear();
        MentionCandidateList.ItemsSource = null;
        SelectedMentionList.ItemsSource = null;
        MentionSearchTextBox.Clear();
        SetLiveText(SelectedMentionHeadingText, "已选 0/20");
        SetLiveText(
            MentionSearchStatusText,
            "输入 1–64 位用户名字符前缀后显式搜索。");
        if (closePicker)
        {
            MentionPickerPanel.Visibility = Visibility.Collapsed;
        }

        if (hadSelectedMentions)
        {
            composerContextVersion++;
        }
    }

    private void UpdateComposerConversationContext(Guid? conversationId, bool isReady)
    {
        if (composerContextConversationId == conversationId &&
            composerContextReady == isReady)
        {
            return;
        }

        composerContextConversationId = conversationId;
        composerContextReady = isReady;
        composerContextVersion++;
        ClearComposerMentions(closePicker: true);
        ClearComposerAttachments();
    }

    private static bool IsNearBottom(ScrollViewer? scrollViewer) =>
        scrollViewer is null ||
        scrollViewer.ScrollableHeight - scrollViewer.VerticalOffset <= 1.5;

    private static T? FindVisualChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                return match;
            }

            var descendant = FindVisualChild<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }
}
