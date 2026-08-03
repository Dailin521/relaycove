using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RelayCove.Client.Accounts;
using RelayCove.Client.Notifications;
using RelayCove.Client.Storage;
using RelayCove.Client.Sync;

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
    private bool composerContextReady;
    private bool suppressSelectionRequest;
    private bool applyingMessageSnapshot;
    private bool composerAvailable;
    private bool composerSubmissionRunning;

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
        if (sender is not System.Windows.Controls.Button
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

    private async Task SendComposedMessageAsync()
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
                submittedReplyToMessageId);
            var replyContextUnchanged = submittedReplyToMessageId.HasValue
                ? composerReplyConversationId == submittedConversationId &&
                  composerReplyToMessageId == submittedReplyToMessageId
                : !composerReplyToMessageId.HasValue;
            if (outcome.PendingCommitted &&
                displayedMessageSnapshot?.ConversationId == submittedConversationId &&
                composerContextVersion == submittedContextVersion &&
                replyContextUnchanged &&
                string.Equals(
                    MessageComposerTextBox.Text,
                    submittedContent,
                    StringComparison.Ordinal))
            {
                MessageComposerTextBox.Clear();
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

    private void UpdateComposerState()
    {
        SendMessageButton.IsEnabled = composerAvailable &&
            !composerSubmissionRunning &&
            ClientTextMessageContentValidator.IsValid(MessageComposerTextBox.Text);
    }

    private static string DescribeSendOutcome(
        ClientMessageSendOutcome outcome,
        bool isRetry) =>
        outcome.Status switch
        {
            ClientMessageSendStatus.Completed => isRetry ? "重试发送成功。" : "发送成功。",
            ClientMessageSendStatus.ValidationFailed =>
                "消息需包含 1–4000 个 Unicode 字符，且不能只有空白或含不支持的控制字符。",
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
