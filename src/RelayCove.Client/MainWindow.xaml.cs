using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
    private const int MaximumAttachmentThumbnailInProgressRetries = 15;
    private static readonly TimeSpan AttachmentThumbnailRetryMinimumDelay =
        TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan AttachmentThumbnailRetryMaximumDelay =
        TimeSpan.FromSeconds(1);
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
    private readonly List<ClientAttachmentDraft> composerAttachments = [];
    private readonly Dictionary<
        ClientAttachmentViewKey,
        ClientAttachmentDownloadStateEntry> attachmentDownloadStates = [];
    private readonly Dictionary<
        ClientAttachmentViewKey,
        ClientAttachmentDownloadOperation> attachmentDownloadOperations = [];
    private readonly Dictionary<
        ClientAttachmentViewKey,
        ClientAttachmentRevealOperation> attachmentRevealOperations = [];
    private readonly Dictionary<
        ClientAttachmentViewKey,
        ClientAttachmentImageOperation> attachmentThumbnailOperations = [];
    private ClientAttachmentImageViewerOperation? attachmentImageViewerOperation;
    private IInputElement? attachmentImageViewerRestoreFocus;
    private long mentionSearchVersion;
    private long attachmentSubmissionVersion;
    private long attachmentDownloadContextVersion;
    private Guid? attachmentDownloadConversationId;
    private bool composerContextReady;
    private bool suppressSelectionRequest;
    private bool applyingMessageSnapshot;
    private bool composerAvailable;
    private bool composerSubmissionRunning;
    private bool mentionSearchRunning;
    private bool attachmentInputRunning;
    private CancellationTokenSource? attachmentInputCancellationSource;
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

    internal void CancelAttachmentInputForShutdown()
    {
        if (attachmentInputCancellationSource is { IsCancellationRequested: false } cancellationSource)
        {
            cancellationSource.Cancel();
        }

        ResetAttachmentDownloadContext(conversationId: null);
    }

    internal void ApplyAccountShellSnapshot(ClientAccountShellSnapshot snapshot)
    {
        if (!snapshot.HasActiveAccount)
        {
            pendingConversationSelectionId = null;
            composerAvailable = false;
            ResetAttachmentDownloadContext(conversationId: null);
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

        snapshot = ReconcileAttachmentDownloadSnapshot(snapshot);

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
            // Visual materialization can synchronously raise Loaded and
            // DataContextChanged while ItemsSource is assigned. Publish the
            // reconciled snapshot first so those handlers resolve the new
            // attachment identity instead of the previous snapshot.
            displayedMessageSnapshot = snapshot;
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
        RaiseLiveRegionChanged(textBlock);
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
        AdvanceComposerContextVersion();
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

        await AddAttachmentFilesAsync(
            selectedPaths,
            selectionConversationId,
            selectionContextVersion,
            selectionDraftIds);
    }

    private void OnAttachmentInputPreviewDragEnter(object sender, System.Windows.DragEventArgs e)
    {
        _ = sender;
        var canAccept = UpdateAttachmentFileDropEffect(e);
        UpdateAttachmentDropVisual(canAccept);
        SetLiveText(
            MessageComposerStatusText,
            canAccept
                ? "松开鼠标即可检查并添加本地文件；不会记录本地路径。"
                : "这里只接受当前可用会话中的真实本地文件拖放。");
    }

    private void OnAttachmentInputPreviewDragOver(object sender, System.Windows.DragEventArgs e)
    {
        _ = sender;
        UpdateAttachmentDropVisual(UpdateAttachmentFileDropEffect(e));
    }

    private void OnAttachmentInputPreviewDragLeave(object sender, System.Windows.DragEventArgs e)
    {
        _ = sender;
        e.Handled = true;
        UpdateAttachmentDropVisual(active: false);
    }

    private async void OnAttachmentInputPreviewDrop(object sender, System.Windows.DragEventArgs e)
    {
        _ = sender;
        e.Handled = true;
        UpdateAttachmentDropVisual(active: false);
        if (!CanSelectAttachments() || !SourceAllowsCopy(e))
        {
            SetLiveText(
                MessageComposerStatusText,
                "当前输入状态或拖拽来源不允许复制附件，本次未读取拖入路径。");
            return;
        }

        var expectedConversationId = displayedMessageSnapshot?.ConversationId;
        var expectedContextVersion = composerContextVersion;
        var expectedDraftIds = GetComposerAttachmentDraftIds();
        var hasExactFileDrop = HasExactFileDrop(e.Data);
        object? fileDropData = null;
        if (hasExactFileDrop)
        {
            try
            {
                fileDropData = e.Data.GetData(
                    System.Windows.DataFormats.FileDrop,
                    autoConvert: false);
            }
            catch (Exception exception) when (!IsCriticalException(exception))
            {
                SetLiveText(
                    MessageComposerStatusText,
                    "无法读取拖入的数据，请改用附件按钮选择文件。");
                return;
            }
        }

        var snapshot = ClientAttachmentFileDropPolicy.Capture(
            hasExactFileDrop,
            fileDropData,
            composerAttachments.Count);
        if (!CanSelectAttachments() ||
            !IsAttachmentComposerContextCurrent(
                expectedConversationId,
                expectedContextVersion,
                expectedDraftIds))
        {
            SetLiveText(
                MessageComposerStatusText,
                "附件输入上下文已变化，本次拖入未添加任何文件。");
            return;
        }

        if (!snapshot.IsSuccess)
        {
            SetLiveText(
                MessageComposerStatusText,
                snapshot.Status == ClientAttachmentFileDropSnapshotStatus.TooManyFiles
                    ? "一条消息最多选择 10 个附件，本次拖入未添加任何文件。"
                    : "拖入内容不是可接受的本地文件批次，本次未添加任何附件。");
            return;
        }

        await AddAttachmentFilesAsync(
            snapshot.Paths,
            expectedConversationId,
            expectedContextVersion,
            expectedDraftIds);
    }

    private async void OnAttachmentInputPreviewKeyDown(
        object sender,
        System.Windows.Input.KeyEventArgs e)
    {
        _ = sender;
        if (!ClientClipboardImageReader.IsExactImagePasteGesture(
                e.Key,
                Keyboard.Modifiers) ||
            !CanSelectAttachments())
        {
            return;
        }

        var expectedConversationId = displayedMessageSnapshot?.ConversationId;
        var expectedContextVersion = composerContextVersion;
        var expectedDraftIds = GetComposerAttachmentDraftIds();
        var clipboard = ClientClipboardImageReader.TryRead(
            suppressRepeatedImageRead: e.IsRepeat,
            System.Windows.Clipboard.ContainsText,
            System.Windows.Clipboard.ContainsImage,
            System.Windows.Clipboard.GetImage);
        if (!IsAttachmentComposerContextCurrent(
                expectedConversationId,
                expectedContextVersion,
                expectedDraftIds))
        {
            e.Handled = true;
            SetLiveText(
                MessageComposerStatusText,
                "剪贴板读取期间输入上下文已变化，本次粘贴未添加任何内容。");
            return;
        }

        if (clipboard.Status is ClientClipboardImageReadStatus.NoImage or
            ClientClipboardImageReadStatus.TextPreferred)
        {
            return;
        }

        e.Handled = true;
        if (clipboard.Status == ClientClipboardImageReadStatus.RepeatedImagePaste)
        {
            return;
        }

        if (clipboard.Status != ClientClipboardImageReadStatus.Success)
        {
            SetLiveText(
                MessageComposerStatusText,
                clipboard.Status == ClientClipboardImageReadStatus.ClipboardUnavailable
                    ? "剪贴板暂时不可用，请稍后重试。"
                    : "剪贴板图片无效，未添加附件。");
            return;
        }

        await AddClipboardImageAsync(
            clipboard.Image!,
            expectedConversationId,
            expectedContextVersion,
            expectedDraftIds);
    }

    private bool UpdateAttachmentFileDropEffect(System.Windows.DragEventArgs e)
    {
        var hasExactFileDrop = HasExactFileDrop(e.Data);
        var canAccept = ClientAttachmentFileDropPolicy.CanShowCopyEffect(
            CanSelectAttachments(),
            hasExactFileDrop,
            SourceAllowsCopy(e));
        e.Effects = canAccept
            ? System.Windows.DragDropEffects.Copy
            : System.Windows.DragDropEffects.None;
        e.Handled = true;
        return canAccept;
    }

    private static bool HasExactFileDrop(System.Windows.IDataObject data)
    {
        try
        {
            return data.GetDataPresent(
                System.Windows.DataFormats.FileDrop,
                autoConvert: false);
        }
        catch (Exception exception) when (!IsCriticalException(exception))
        {
            return false;
        }
    }

    private static bool SourceAllowsCopy(System.Windows.DragEventArgs e) =>
        (e.AllowedEffects & System.Windows.DragDropEffects.Copy) != 0;

    private void UpdateAttachmentDropVisual(bool active)
    {
        AttachmentInputDropTarget.Background = active
            ? System.Windows.Media.Brushes.AliceBlue
            : System.Windows.Media.Brushes.Transparent;
        AttachmentInputDropTarget.BorderBrush = active
            ? System.Windows.Media.Brushes.SteelBlue
            : System.Windows.Media.Brushes.Transparent;
    }

    private async Task AddAttachmentFilesAsync(
        IReadOnlyList<string> selectedPaths,
        Guid? expectedConversationId,
        long expectedContextVersion,
        IReadOnlyList<Guid> expectedDraftIds)
    {
        if (!CanSelectAttachments() ||
            !IsAttachmentComposerContextCurrent(
                expectedConversationId,
                expectedContextVersion,
                expectedDraftIds))
        {
            return;
        }

        var cancellationSource = BeginAttachmentInput();
        UpdateComposerState();
        SetLiveText(MessageComposerStatusText, "正在检查所选文件，不会把本地路径上传或记录到日志…");
        try
        {
            var outcome = await ClientAttachmentFileSourceFactory.CreateAsync(
                selectedPaths,
                composerAttachments,
                cancellationSource.Token);
            if (!composerAvailable ||
                !IsAttachmentComposerContextCurrent(
                    expectedConversationId,
                    expectedContextVersion,
                    expectedDraftIds))
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

            AddAttachmentDrafts(outcome.Selections);
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
            EndAttachmentInput(cancellationSource);
        }
    }

    private async Task AddClipboardImageAsync(
        BitmapSource image,
        Guid? expectedConversationId,
        long expectedContextVersion,
        IReadOnlyList<Guid> expectedDraftIds)
    {
        if (!CanSelectAttachments() ||
            !IsAttachmentComposerContextCurrent(
                expectedConversationId,
                expectedContextVersion,
                expectedDraftIds))
        {
            return;
        }

        var imageAdded = false;
        var cancellationSource = BeginAttachmentInput();
        UpdateComposerState();
        SetLiveText(
            MessageComposerStatusText,
            "正在安全编码剪贴板图片，不会记录图片内容或写入临时文件…");
        try
        {
            var outcome = await ClientAttachmentClipboardImageFactory.CreateAsync(
                image,
                composerAttachments,
                cancellationSource.Token);
            if (!composerAvailable ||
                !IsAttachmentComposerContextCurrent(
                    expectedConversationId,
                    expectedContextVersion,
                    expectedDraftIds))
            {
                return;
            }

            if (outcome.Status != ClientAttachmentClipboardImageSelectionStatus.Success)
            {
                SetLiveText(
                    MessageComposerStatusText,
                    DescribeClipboardImageSelectionOutcome(outcome.Status));
                return;
            }

            AddAttachmentDrafts([outcome.Selection!]);
            imageAdded = true;
        }
        finally
        {
            EndAttachmentInput(cancellationSource);
            if (imageAdded)
            {
                SelectAttachmentsButton.Focus();
            }
        }
    }

    private void AddAttachmentDrafts(IReadOnlyList<ClientAttachmentDraft> drafts)
    {
        composerAttachments.AddRange(drafts);
        AdvanceComposerContextVersion(cancelAttachmentInput: false);
        MentionPickerPanel.Visibility = Visibility.Collapsed;
        RefreshSelectedAttachmentPresentation();
        SetLiveText(
            MessageComposerStatusText,
            composerAttachments.All(static attachment => attachment.IsImage)
                ? $"已选择 {composerAttachments.Count} 个附件，将作为图片消息发送。"
                : $"已选择 {composerAttachments.Count} 个附件，将作为文件消息发送。");
    }

    private void OnRemoveAttachmentClicked(object sender, RoutedEventArgs e)
    {
        _ = e;
        if (composerSubmissionRunning ||
            attachmentInputRunning ||
            sender is not System.Windows.Controls.Button
            {
                DataContext: ClientAttachmentDraft selection,
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

        AdvanceComposerContextVersion();
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
                AdvanceComposerContextVersion();
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
        AdvanceComposerContextVersion();
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

        AdvanceComposerContextVersion();
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
        AdvanceComposerContextVersion();
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

    private async void OnAttachmentDownloadActionClicked(object sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not System.Windows.Controls.Button
            {
                DataContext: ClientMessageAttachmentPresentation
                {
                    DownloadState: { } state,
                } attachment,
            } ||
            accountShell is null ||
            !TryResolveCurrentAttachment(state, out _, out _))
        {
            return;
        }

        switch (state.Action)
        {
            case ClientAttachmentDownloadAction.Download:
                await StartAttachmentDownloadAsync(attachment, state);
                break;
            case ClientAttachmentDownloadAction.Cancel:
                CancelAttachmentDownload(state);
                break;
            case ClientAttachmentDownloadAction.ShowInFolder:
                await RevealAttachmentInFolderAsync(state);
                break;
        }
    }

    private async void OnAttachmentThumbnailLoaded(object sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not System.Windows.Controls.Image
            {
                DataContext: ClientMessageAttachmentPresentation
                {
                    ImageState: { } state,
                },
            })
        {
            return;
        }

        await StartAttachmentThumbnailAsync(state);
    }

    private void OnAttachmentThumbnailUnloaded(object sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is System.Windows.Controls.Image
            {
                DataContext: ClientMessageAttachmentPresentation
                {
                    ImageState: { } state,
                },
            })
        {
            CancelAttachmentThumbnailForRecycle(state);
        }
    }

    private void OnAttachmentThumbnailDataContextChanged(
        object sender,
        DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is ClientMessageAttachmentPresentation
            {
                ImageState: { } oldState,
            })
        {
            CancelAttachmentThumbnailForRecycle(oldState);
        }

        if (sender is System.Windows.Controls.Image { IsLoaded: true } &&
            e.NewValue is ClientMessageAttachmentPresentation
            {
                ImageState: { } newState,
            })
        {
            _ = StartAttachmentThumbnailAsync(newState);
        }
    }

    private async void OnAttachmentImageViewClicked(object sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not System.Windows.Controls.Button
            {
                DataContext: ClientMessageAttachmentPresentation
                {
                    ImageState: { CanView: true } state,
                },
            } button ||
            accountShell is null ||
            !TryResolveCurrentAttachment(state, out _, out _))
        {
            return;
        }

        CloseAttachmentImageViewer(restoreFocus: false);
        var operation = new ClientAttachmentImageViewerOperation(
            state.Context,
            state,
            new CancellationTokenSource());
        attachmentImageViewerOperation = operation;
        attachmentImageViewerRestoreFocus = button;
        AttachmentImageViewerTitleText.Text = state.DisplayName;
        AttachmentImageViewerImage.Source = null;
        AttachmentImageViewerStatusText.Text = "正在加载受限图片预览…";
        AttachmentImageViewerOverlay.Visibility = Visibility.Visible;
        CloseAttachmentImageViewerButton.Focus();

        try
        {
            var outcome = await accountShell.LoadAttachmentImageAsync(
                state.Context.AttachmentId,
                ClientAttachmentImageRendition.Viewer,
                operation.Cancellation.Token);
            if (!IsCurrentAttachmentImageViewerOperation(operation))
            {
                return;
            }

            if (outcome.Status == ClientAttachmentImageLoadStatus.Ready &&
                outcome.Image is { IsFrozen: true } image)
            {
                AttachmentImageViewerImage.Source = image;
                AttachmentImageViewerStatusText.Text = outcome.WasDownsampled
                    ? "受限预览：图片已按 2560 像素与 25 MiB 安全上限缩放。"
                    : "图片预览已加载；显示仍受 25 MiB 安全上限保护。";
            }
            else
            {
                AttachmentImageViewerImage.Source = null;
                AttachmentImageViewerStatusText.Text = DescribeAttachmentImageOutcome(
                    outcome.Status);
            }

            if (outcome.Status is ClientAttachmentImageLoadStatus.NotDownloaded or
                ClientAttachmentImageLoadStatus.AttachmentUnavailable or
                ClientAttachmentImageLoadStatus.ValidationFailed)
            {
                MarkAttachmentNoLongerDownloaded(state.Context);
            }
        }
        catch (OperationCanceledException)
        {
            if (IsCurrentAttachmentImageViewerOperation(operation))
            {
                AttachmentImageViewerImage.Source = null;
                AttachmentImageViewerStatusText.Text = "图片预览已取消。";
            }
        }
        catch (ObjectDisposedException)
        {
            if (IsCurrentAttachmentImageViewerOperation(operation))
            {
                AttachmentImageViewerImage.Source = null;
                AttachmentImageViewerStatusText.Text = "账户已结束，无法查看图片。";
            }
        }
        catch (Exception exception) when (!IsCriticalException(exception))
        {
            Debug.WriteLine(
                $"Attachment image viewer presentation failed: {exception.GetType().Name}.");
            if (IsCurrentAttachmentImageViewerOperation(operation))
            {
                AttachmentImageViewerImage.Source = null;
                AttachmentImageViewerStatusText.Text = "无法加载图片预览，请稍后重试。";
            }
        }
        finally
        {
            // Keep the viewer identity alive after a successful load so a later
            // snapshot change, revocation, or download-state loss can still close
            // the already-rendered image. Close/replacement owns disposal while
            // this operation remains current.
            if (!ReferenceEquals(attachmentImageViewerOperation, operation))
            {
                operation.Dispose();
            }
        }
    }

    private void OnCloseAttachmentImageViewerClicked(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        CloseAttachmentImageViewer(restoreFocus: true);
    }

    private void OnMainWindowPreviewKeyDown(
        object sender,
        System.Windows.Input.KeyEventArgs e)
    {
        _ = sender;
        if (e.Key != Key.Escape || AttachmentImageViewerOverlay.Visibility != Visibility.Visible)
        {
            return;
        }

        CloseAttachmentImageViewer(restoreFocus: true);
        e.Handled = true;
    }

    private async Task StartAttachmentThumbnailAsync(ClientAttachmentImageViewState state)
    {
        var shell = accountShell;
        if (shell is null ||
            !TryResolveCurrentAttachment(state, out _, out _) ||
            !state.TryBeginLoad())
        {
            return;
        }

        var key = ClientAttachmentViewKey.From(state.Context);
        if (attachmentThumbnailOperations.Remove(key, out var previousOperation))
        {
            previousOperation.Cancel();
            previousOperation.Dispose();
        }

        var operation = new ClientAttachmentImageOperation(
            state.Context,
            state,
            new CancellationTokenSource());
        attachmentThumbnailOperations.Add(key, operation);
        try
        {
            var retryDelay = AttachmentThumbnailRetryMinimumDelay;
            var inProgressRetries = 0;
            ClientAttachmentImageLoadOutcome outcome;
            while (true)
            {
                outcome = await shell.LoadAttachmentImageAsync(
                    state.Context.AttachmentId,
                    ClientAttachmentImageRendition.Thumbnail,
                    operation.Cancellation.Token);
                if (!IsCurrentAttachmentThumbnailOperation(key, state, operation))
                {
                    return;
                }

                if (outcome.Status != ClientAttachmentImageLoadStatus.InProgress)
                {
                    break;
                }

                if (++inProgressRetries >= MaximumAttachmentThumbnailInProgressRetries)
                {
                    outcome = ClientAttachmentImageLoadOutcome.Failure(
                        ClientAttachmentImageLoadStatus.TimedOut);
                    break;
                }

                await Task.Delay(retryDelay, operation.Cancellation.Token);
                retryDelay = TimeSpan.FromMilliseconds(Math.Min(
                    AttachmentThumbnailRetryMaximumDelay.TotalMilliseconds,
                    retryDelay.TotalMilliseconds * 2));
            }

            if (outcome.Status == ClientAttachmentImageLoadStatus.Ready &&
                outcome.Image is { IsFrozen: true } image)
            {
                _ = state.TryApplyLoaded(image);
            }
            else
            {
                _ = state.TryApplyFailure(DescribeAttachmentImageOutcome(outcome.Status));
            }

            if (outcome.Status is ClientAttachmentImageLoadStatus.NotDownloaded or
                ClientAttachmentImageLoadStatus.AttachmentUnavailable or
                ClientAttachmentImageLoadStatus.ValidationFailed)
            {
                MarkAttachmentNoLongerDownloaded(state.Context);
            }
        }
        catch (OperationCanceledException)
        {
            if (IsCurrentAttachmentThumbnailOperation(key, state, operation))
            {
                _ = state.TryApplyFailure("图片缩略图加载已取消。");
            }
        }
        catch (ObjectDisposedException)
        {
            if (IsCurrentAttachmentThumbnailOperation(key, state, operation))
            {
                _ = state.TryApplyFailure("账户已结束，无法加载图片缩略图。");
            }
        }
        catch (Exception exception) when (!IsCriticalException(exception))
        {
            Debug.WriteLine(
                $"Attachment thumbnail presentation failed: {exception.GetType().Name}.");
            if (IsCurrentAttachmentThumbnailOperation(key, state, operation))
            {
                _ = state.TryApplyFailure("无法加载图片缩略图，请稍后重试。");
            }
        }
        finally
        {
            CompleteAttachmentThumbnailOperation(key, operation);
        }
    }

    private static string DescribeAttachmentImageOutcome(
        ClientAttachmentImageLoadStatus status) =>
        status switch
        {
            ClientAttachmentImageLoadStatus.Ready => "图片预览已加载。",
            ClientAttachmentImageLoadStatus.InProgress => "图片正在其他任务中加载。",
            ClientAttachmentImageLoadStatus.NotDownloaded => "图片尚未下载，请重新下载。",
            ClientAttachmentImageLoadStatus.AttachmentUnavailable => "图片不可用或已被移除。",
            ClientAttachmentImageLoadStatus.AccessRevoked => "已失去此会话的访问权限。",
            ClientAttachmentImageLoadStatus.Stale => "图片上下文已变化，请重试。",
            ClientAttachmentImageLoadStatus.ValidationFailed =>
                "本地图片未通过完整性校验，请重新下载。",
            ClientAttachmentImageLoadStatus.UnsupportedFormat =>
                "此图片格式不在安全预览白名单中。",
            ClientAttachmentImageLoadStatus.SourceTooLarge =>
                "图片原始尺寸超过安全预览上限。",
            ClientAttachmentImageLoadStatus.OutputTooLarge =>
                "图片解码结果超过安全内存上限。",
            ClientAttachmentImageLoadStatus.DecodeFailed => "图片内容无法安全解码。",
            ClientAttachmentImageLoadStatus.TimedOut =>
                "图片解码超过 10 秒安全时限，已停止等待。",
            ClientAttachmentImageLoadStatus.TransientFailure =>
                "本地状态暂时繁忙，请稍后重试。",
            ClientAttachmentImageLoadStatus.LocalCacheFailure => "本地图片缓存不可用。",
            _ => "图片预览已取消。",
        };

    private void CancelAttachmentThumbnailForRecycle(ClientAttachmentImageViewState state)
    {
        var key = ClientAttachmentViewKey.From(state.Context);
        if (attachmentThumbnailOperations.Remove(key, out var operation))
        {
            operation.Cancel();
            operation.Dispose();
        }

        if (attachmentImageViewerOperation is { } viewerOperation &&
            ReferenceEquals(viewerOperation.State, state))
        {
            CloseAttachmentImageViewer(restoreFocus: false);
        }

        state.ClearForRecycle();
    }

    private bool IsCurrentAttachmentThumbnailOperation(
        ClientAttachmentViewKey key,
        ClientAttachmentImageViewState state,
        ClientAttachmentImageOperation operation) =>
        attachmentThumbnailOperations.TryGetValue(key, out var activeOperation) &&
        ReferenceEquals(activeOperation, operation) &&
        ReferenceEquals(operation.State, state) &&
        ReferenceEquals(operation.Context, state.Context) &&
        TryResolveCurrentAttachment(state, out _, out _);

    private void CompleteAttachmentThumbnailOperation(
        ClientAttachmentViewKey key,
        ClientAttachmentImageOperation operation)
    {
        if (attachmentThumbnailOperations.TryGetValue(key, out var activeOperation) &&
            ReferenceEquals(activeOperation, operation))
        {
            attachmentThumbnailOperations.Remove(key);
        }

        operation.Dispose();
    }

    private bool IsCurrentAttachmentImageViewerOperation(
        ClientAttachmentImageViewerOperation operation) =>
        ReferenceEquals(attachmentImageViewerOperation, operation) &&
        AttachmentImageViewerOverlay.Visibility == Visibility.Visible &&
        ReferenceEquals(operation.Context, operation.State.Context) &&
        TryResolveCurrentAttachment(operation.State, out _, out _);

    private void CloseAttachmentImageViewer(bool restoreFocus)
    {
        var viewerOwnedKeyboardFocus = AttachmentImageViewerOverlay.IsKeyboardFocusWithin;
        var operation = attachmentImageViewerOperation;
        attachmentImageViewerOperation = null;
        if (operation is not null)
        {
            operation.Cancel();
            operation.Dispose();
        }

        AttachmentImageViewerImage.Source = null;
        AttachmentImageViewerTitleText.Text = string.Empty;
        AttachmentImageViewerStatusText.Text = string.Empty;
        AttachmentImageViewerOverlay.Visibility = Visibility.Collapsed;
        var restoreTarget = attachmentImageViewerRestoreFocus;
        attachmentImageViewerRestoreFocus = null;
        if (!restoreFocus && !viewerOwnedKeyboardFocus)
        {
            return;
        }

        if (restoreFocus &&
            restoreTarget is UIElement { IsVisible: true, IsEnabled: true } element &&
            element.Focus())
        {
            return;
        }

        if (MessageList is { IsVisible: true, IsEnabled: true } && MessageList.Focus())
        {
            return;
        }

        if (MessageComposerTextBox is { IsVisible: true, IsEnabled: true })
        {
            MessageComposerTextBox.Focus();
        }
    }

    private async Task StartAttachmentDownloadAsync(
        ClientMessageAttachmentPresentation attachment,
        ClientAttachmentDownloadViewState state)
    {
        var shell = accountShell;
        if (shell is null ||
            !TryResolveCurrentAttachment(state, out var snapshot, out var currentAttachment) ||
            !state.TryBeginDownload(
                snapshot.Status == ClientMessageListStatus.Ready,
                snapshot.ConversationId,
                currentAttachment.MessageClientId,
                currentAttachment.AttachmentId,
                attachmentDownloadContextVersion,
                out var flight) ||
            flight is null)
        {
            return;
        }

        var key = ClientAttachmentViewKey.From(state.Context);
        var operation = new ClientAttachmentDownloadOperation(
            flight,
            new CancellationTokenSource());
        if (!attachmentDownloadOperations.TryAdd(key, operation))
        {
            operation.Dispose();
            _ = state.TryApplyOutcome(
                snapshot.Status == ClientMessageListStatus.Ready,
                snapshot.ConversationId,
                currentAttachment.MessageClientId,
                currentAttachment.AttachmentId,
                attachmentDownloadContextVersion,
                flight,
                flight,
                ClientAttachmentDownloadOutcome.Failure(
                    ClientAttachmentDownloadStatus.InProgress));
            return;
        }

        RaiseAttachmentDownloadLiveRegion(state);

        var progress = new Progress<ClientAttachmentDownloadProgress>(value =>
            ApplyAttachmentDownloadProgressSafely(key, state, operation, value));
        try
        {
            var outcome = await shell.DownloadAttachmentAsync(
                attachment.AttachmentId,
                operation.Cancellation.Token,
                progress);
            ApplyAttachmentDownloadOutcome(key, state, operation, outcome);
        }
        catch (OperationCanceledException)
        {
            ApplyAttachmentDownloadOutcome(
                key,
                state,
                operation,
                ClientAttachmentDownloadOutcome.Failure(
                    ClientAttachmentDownloadStatus.Canceled));
        }
        catch (ObjectDisposedException)
        {
            ApplyAttachmentDownloadOutcome(
                key,
                state,
                operation,
                ClientAttachmentDownloadOutcome.Failure(
                    ClientAttachmentDownloadStatus.Canceled));
        }
        catch (Exception exception) when (!IsCriticalException(exception))
        {
            Debug.WriteLine(
                $"Attachment download presentation failed: {exception.GetType().Name}.");
            ApplyAttachmentDownloadOutcome(
                key,
                state,
                operation,
                ClientAttachmentDownloadOutcome.Failure(
                    ClientAttachmentDownloadStatus.LocalCacheFailure));
        }
        finally
        {
            if (attachmentDownloadOperations.TryGetValue(key, out var currentOperation) &&
                ReferenceEquals(currentOperation, operation))
            {
                attachmentDownloadOperations.Remove(key);
            }

            operation.Dispose();
        }
    }

    private void CancelAttachmentDownload(ClientAttachmentDownloadViewState state)
    {
        var key = ClientAttachmentViewKey.From(state.Context);
        if (!attachmentDownloadOperations.TryGetValue(key, out var operation) ||
            !TryResolveCurrentAttachment(state, out var snapshot, out var currentAttachment) ||
            !state.TryCancel(
                snapshot.Status == ClientMessageListStatus.Ready,
                snapshot.ConversationId,
                currentAttachment.MessageClientId,
                currentAttachment.AttachmentId,
                attachmentDownloadContextVersion,
                operation.Flight,
                operation.Flight))
        {
            return;
        }

        RaiseAttachmentDownloadLiveRegion(state);
        operation.Cancel();
    }

    private async Task RevealAttachmentInFolderAsync(
        ClientAttachmentDownloadViewState state)
    {
        var shell = accountShell;
        var key = ClientAttachmentViewKey.From(state.Context);
        if (shell is null || !TryResolveCurrentAttachment(state, out _, out _))
        {
            return;
        }

        var operation = new ClientAttachmentRevealOperation(
            state.Context,
            state,
            new CancellationTokenSource());
        if (!attachmentRevealOperations.TryAdd(key, operation))
        {
            operation.Dispose();
            return;
        }

        SetLiveText(MessageComposerStatusText, "正在验证并定位已下载附件…");
        try
        {
            var outcome = await shell.RevealAttachmentInFolderAsync(
                state.Context.AttachmentId,
                operation.Cancellation.Token);
            if (!IsCurrentAttachmentRevealOperation(key, state, operation))
            {
                return;
            }

            if (outcome.Status is ClientAttachmentRevealStatus.NotDownloaded or
                ClientAttachmentRevealStatus.AttachmentUnavailable or
                ClientAttachmentRevealStatus.ValidationFailed)
            {
                MarkAttachmentNoLongerDownloaded(state.Context);
            }

            SetLiveText(
                MessageComposerStatusText,
                DescribeAttachmentRevealOutcome(outcome.Status));
        }
        catch (OperationCanceledException)
        {
            if (IsCurrentAttachmentRevealOperation(key, state, operation))
            {
                SetLiveText(MessageComposerStatusText, "附件定位已取消。");
            }
        }
        catch (ObjectDisposedException)
        {
            if (IsCurrentAttachmentRevealOperation(key, state, operation))
            {
                SetLiveText(MessageComposerStatusText, "账户已结束，无法定位附件。");
            }
        }
        catch (Exception exception) when (!IsCriticalException(exception))
        {
            Debug.WriteLine(
                $"Attachment reveal presentation failed: {exception.GetType().Name}.");
            if (IsCurrentAttachmentRevealOperation(key, state, operation))
            {
                SetLiveText(MessageComposerStatusText, "无法定位附件，请稍后重试。");
            }
        }
        finally
        {
            CompleteAttachmentRevealOperation(key, operation);
        }
    }

    private static string DescribeAttachmentRevealOutcome(
        ClientAttachmentRevealStatus status) =>
        status switch
        {
            ClientAttachmentRevealStatus.Revealed => "已在文件夹中选中附件。",
            ClientAttachmentRevealStatus.NotDownloaded => "附件尚未下载，请重新下载。",
            ClientAttachmentRevealStatus.AttachmentUnavailable => "附件不可用或已被移除。",
            ClientAttachmentRevealStatus.AccessRevoked => "已失去此会话的访问权限。",
            ClientAttachmentRevealStatus.Stale => "附件上下文已变化，请重试。",
            ClientAttachmentRevealStatus.ValidationFailed =>
                "本地附件未通过完整性校验，请重新下载。",
            ClientAttachmentRevealStatus.TransientFailure => "本地状态暂时繁忙，请稍后重试。",
            ClientAttachmentRevealStatus.LocalCacheFailure => "本地附件缓存不可用。",
            ClientAttachmentRevealStatus.ShellUnavailable =>
                "无法打开文件夹，请检查 Windows 文件资源管理器。",
            _ => "附件定位已取消。",
        };

    private void ApplyAttachmentDownloadProgressSafely(
        ClientAttachmentViewKey key,
        ClientAttachmentDownloadViewState state,
        ClientAttachmentDownloadOperation operation,
        ClientAttachmentDownloadProgress progress)
    {
        try
        {
            if (!attachmentDownloadOperations.TryGetValue(key, out var activeOperation) ||
                !ReferenceEquals(activeOperation, operation) ||
                !TryResolveCurrentAttachment(state, out var snapshot, out var currentAttachment))
            {
                return;
            }

            var previousStatus = state.StatusText;
            var applied = state.TryApplyProgress(
                snapshot.Status == ClientMessageListStatus.Ready,
                snapshot.ConversationId,
                currentAttachment.MessageClientId,
                currentAttachment.AttachmentId,
                attachmentDownloadContextVersion,
                operation.Flight,
                activeOperation.Flight,
                progress);
            if (applied &&
                !string.Equals(previousStatus, state.StatusText, StringComparison.Ordinal))
            {
                RaiseAttachmentDownloadLiveRegion(state);
            }
        }
        catch (Exception exception) when (!IsCriticalException(exception))
        {
            Debug.WriteLine(
                $"Attachment download progress presentation failed: {exception.GetType().Name}.");
        }
    }

    private void ApplyAttachmentDownloadOutcome(
        ClientAttachmentViewKey key,
        ClientAttachmentDownloadViewState state,
        ClientAttachmentDownloadOperation operation,
        ClientAttachmentDownloadOutcome outcome)
    {
        if (!attachmentDownloadOperations.TryGetValue(key, out var activeOperation) ||
            !ReferenceEquals(activeOperation, operation) ||
            !attachmentDownloadStates.TryGetValue(key, out var entry) ||
            !ReferenceEquals(entry.State, state) ||
            !TryResolveCurrentAttachment(state, out var snapshot, out var currentAttachment))
        {
            return;
        }

        if (state.TryApplyOutcome(
            snapshot.Status == ClientMessageListStatus.Ready,
            snapshot.ConversationId,
            currentAttachment.MessageClientId,
            currentAttachment.AttachmentId,
            attachmentDownloadContextVersion,
                operation.Flight,
                activeOperation.Flight,
                outcome))
        {
            if (outcome.Status is ClientAttachmentDownloadStatus.Completed or
                ClientAttachmentDownloadStatus.AlreadyDownloaded)
            {
                // A successful download is newer than a snapshot captured while its
                // flight was still active. Let a later persisted projection correct it.
                entry.PersistedDownloaded = true;
                entry.PendingPersistedDownloaded = null;
                SynchronizeAttachmentImageEligibility(
                    entry,
                    isEligible: currentAttachment.IsImage);
                _ = StartAttachmentThumbnailAsync(entry.ImageState);
            }
            else if (entry.PendingPersistedDownloaded is { } pendingDownloaded)
            {
                entry.PendingPersistedDownloaded = null;
                entry.PersistedDownloaded = pendingDownloaded;
                _ = state.SynchronizePersistedDownloaded(pendingDownloaded);
            }

            if (outcome.Status is not (ClientAttachmentDownloadStatus.Completed or
                    ClientAttachmentDownloadStatus.AlreadyDownloaded))
            {
                SynchronizeAttachmentImageEligibility(
                    entry,
                    currentAttachment.IsImage &&
                    state.Phase == ClientAttachmentDownloadPhase.Downloaded);
            }

            RaiseAttachmentDownloadLiveRegion(state);
        }
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
            attachmentInputRunning ||
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

    private CancellationTokenSource BeginAttachmentInput()
    {
        if (attachmentInputCancellationSource is not null || attachmentInputRunning)
        {
            throw new InvalidOperationException("An attachment input operation is already active.");
        }

        var cancellationSource = new CancellationTokenSource();
        attachmentInputCancellationSource = cancellationSource;
        attachmentInputRunning = true;
        return cancellationSource;
    }

    private void EndAttachmentInput(CancellationTokenSource cancellationSource)
    {
        if (ReferenceEquals(attachmentInputCancellationSource, cancellationSource))
        {
            attachmentInputCancellationSource = null;
            attachmentInputRunning = false;
        }

        cancellationSource.Dispose();
        UpdateComposerState();
    }

    private void AdvanceComposerContextVersion(bool cancelAttachmentInput = true)
    {
        composerContextVersion++;
        if (!cancelAttachmentInput || attachmentInputCancellationSource is not { } cancellationSource)
        {
            return;
        }

        if (!cancellationSource.IsCancellationRequested)
        {
            SetLiveText(
                MessageComposerStatusText,
                "附件输入上下文已变化，旧的文件检查或截图编码已取消。");
            cancellationSource.Cancel();
        }
    }

    private void UpdateComposerState()
    {
        var hasAttachments = composerAttachments.Count != 0;
        SendMessageButton.IsEnabled = composerAvailable &&
            !composerSubmissionRunning &&
            !attachmentInputRunning &&
            (hasAttachments ||
             ClientTextMessageContentValidator.IsValid(MessageComposerTextBox.Text));
        MessageComposerTextBox.IsEnabled = composerAvailable &&
            !attachmentInputRunning &&
            !hasAttachments;
        SelectAttachmentsButton.IsEnabled = CanSelectAttachments();
        MentionPickerButton.IsEnabled = composerAvailable &&
            !composerSubmissionRunning &&
            !attachmentInputRunning &&
            !hasAttachments;
        MentionSearchTextBox.IsEnabled = composerAvailable &&
            !mentionSearchRunning &&
            !attachmentInputRunning &&
            !hasAttachments;
        MentionSearchButton.IsEnabled = composerAvailable &&
            !mentionSearchRunning &&
            !attachmentInputRunning &&
            !hasAttachments &&
            ClientMentionPolicy.IsValidQuery(MentionSearchTextBox.Text);
        SelectedAttachmentPanel.IsEnabled = !composerSubmissionRunning &&
            !attachmentInputRunning;
        ReplyComposerPanel.IsEnabled = !hasAttachments || !composerSubmissionRunning;
        if (!CanSelectAttachments())
        {
            UpdateAttachmentDropVisual(active: false);
        }
    }

    private bool CanSelectAttachments() =>
        composerAvailable &&
        !composerSubmissionRunning &&
        !attachmentInputRunning &&
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
            AdvanceComposerContextVersion();
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

    private static string DescribeClipboardImageSelectionOutcome(
        ClientAttachmentClipboardImageSelectionStatus status) =>
        status switch
        {
            ClientAttachmentClipboardImageSelectionStatus.TooManyFiles =>
                "一条消息最多选择 10 个附件。",
            ClientAttachmentClipboardImageSelectionStatus.AggregateMemoryTooLarge =>
                "剪贴板 PNG 的内存总量超过 25 MiB，请先移除其他截图。",
            ClientAttachmentClipboardImageSelectionStatus.RawPixelsTooLarge =>
                "剪贴板图片的像素数据超过 100 MiB，未添加附件。",
            ClientAttachmentClipboardImageSelectionStatus.OutputTooLarge =>
                "剪贴板图片编码后超过 25 MiB，未添加附件。",
            ClientAttachmentClipboardImageSelectionStatus.Canceled =>
                "剪贴板图片编码已取消。",
            ClientAttachmentClipboardImageSelectionStatus.NoImage or
                ClientAttachmentClipboardImageSelectionStatus.InvalidImage =>
                "剪贴板图片无效，未添加附件。",
            _ => "无法编码剪贴板图片，请稍后重试。",
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
            AdvanceComposerContextVersion();
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
            AdvanceComposerContextVersion();
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
        AdvanceComposerContextVersion();
        ClearComposerMentions(closePicker: true);
        ClearComposerAttachments();
    }

    private ClientMessageListSnapshot ReconcileAttachmentDownloadSnapshot(
        ClientMessageListSnapshot snapshot)
    {
        if (snapshot.Status != ClientMessageListStatus.Ready ||
            snapshot.ConversationId is not { } conversationId)
        {
            if (attachmentDownloadConversationId.HasValue ||
                attachmentDownloadStates.Count != 0 ||
                attachmentDownloadOperations.Count != 0 ||
                attachmentRevealOperations.Count != 0 ||
                attachmentThumbnailOperations.Count != 0 ||
                attachmentImageViewerOperation is not null ||
                AttachmentImageViewerOverlay.Visibility == Visibility.Visible)
            {
                ResetAttachmentDownloadContext(conversationId: null);
            }

            return snapshot;
        }

        if (attachmentDownloadConversationId != conversationId)
        {
            ResetAttachmentDownloadContext(conversationId);
        }

        var currentKeys = new HashSet<ClientAttachmentViewKey>();
        var messages = new List<ClientMessageListItemPresentation>(snapshot.Messages.Count);
        foreach (var message in snapshot.Messages)
        {
            if (message.Attachments.Count == 0)
            {
                messages.Add(message);
                continue;
            }

            var attachments = new List<ClientMessageAttachmentPresentation>(
                message.Attachments.Count);
            foreach (var attachment in message.Attachments)
            {
                var key = new ClientAttachmentViewKey(
                    attachment.MessageClientId,
                    attachment.AttachmentId);
                currentKeys.Add(key);
                if (!attachmentDownloadStates.TryGetValue(key, out var entry))
                {
                    var context = new ClientAttachmentDownloadContext(
                        conversationId,
                        attachment.MessageClientId,
                        attachment.AttachmentId,
                        attachmentDownloadContextVersion);
                    entry = new ClientAttachmentDownloadStateEntry(
                        new ClientAttachmentDownloadViewState(
                            context,
                            attachment.DisplayName,
                            attachment.IsDownloaded),
                        new ClientAttachmentImageViewState(
                            context,
                            attachment.DisplayName,
                            attachment.IsImage && attachment.IsDownloaded),
                        attachment.IsDownloaded);
                    attachmentDownloadStates.Add(key, entry);
                }
                else if (entry.PersistedDownloaded != attachment.IsDownloaded ||
                         entry.PendingPersistedDownloaded.HasValue)
                {
                    if (entry.State.SynchronizePersistedDownloaded(
                            attachment.IsDownloaded))
                    {
                        RaiseAttachmentDownloadLiveRegion(entry.State);
                        entry.PersistedDownloaded = attachment.IsDownloaded;
                        entry.PendingPersistedDownloaded = null;
                    }
                    else
                    {
                        // Do not lose a durable projection merely because an owned
                        // download flight is still rendering. Its outcome settles
                        // this value after the flight leaves Downloading/Canceling.
                        entry.PendingPersistedDownloaded = attachment.IsDownloaded;
                    }
                }

                SynchronizeAttachmentImageEligibility(
                    entry,
                    attachment.IsImage &&
                    entry.State.Phase == ClientAttachmentDownloadPhase.Downloaded);

                attachments.Add(attachment with
                {
                    DownloadState = entry.State,
                    ImageState = entry.ImageState,
                });
            }

            messages.Add(message with { Attachments = attachments.AsReadOnly() });
        }

        foreach (var removedKey in attachmentDownloadStates.Keys
            .Where(key => !currentKeys.Contains(key))
            .ToArray())
        {
            if (attachmentDownloadOperations.Remove(removedKey, out var operation))
            {
                operation.Cancel();
            }

            if (attachmentRevealOperations.Remove(removedKey, out var revealOperation))
            {
                revealOperation.Cancel();
            }

            if (attachmentThumbnailOperations.Remove(removedKey, out var imageOperation))
            {
                imageOperation.Cancel();
                imageOperation.Dispose();
            }

            if (attachmentImageViewerOperation is { } viewerOperation &&
                ClientAttachmentViewKey.From(viewerOperation.Context) == removedKey)
            {
                CloseAttachmentImageViewer(restoreFocus: false);
            }

            attachmentDownloadStates.Remove(removedKey);
        }

        return snapshot with { Messages = messages.AsReadOnly() };
    }

    private bool TryResolveCurrentAttachment(
        ClientAttachmentDownloadViewState state,
        out ClientMessageListSnapshot snapshot,
        out ClientMessageAttachmentPresentation attachment)
    {
        ArgumentNullException.ThrowIfNull(state);
        snapshot = displayedMessageSnapshot ?? ClientMessageListSnapshot.Initial;
        attachment = null!;
        if (snapshot.Status != ClientMessageListStatus.Ready ||
            snapshot.ConversationId is not { } conversationId ||
            conversationId != attachmentDownloadConversationId ||
            conversationId != state.Context.ConversationId ||
            state.Context.ContextVersion != attachmentDownloadContextVersion)
        {
            return false;
        }

        var message = snapshot.Messages.FirstOrDefault(candidate =>
            candidate.ClientMessageId == state.Context.MessageClientId);
        attachment = message?.Attachments.FirstOrDefault(candidate =>
            candidate.AttachmentId == state.Context.AttachmentId)!;
        return attachment is not null &&
            attachment.MessageClientId == state.Context.MessageClientId &&
            ReferenceEquals(attachment.DownloadState, state);
    }

    private bool TryResolveCurrentAttachment(
        ClientAttachmentImageViewState state,
        out ClientMessageListSnapshot snapshot,
        out ClientMessageAttachmentPresentation attachment)
    {
        ArgumentNullException.ThrowIfNull(state);
        snapshot = displayedMessageSnapshot ?? ClientMessageListSnapshot.Initial;
        attachment = null!;
        if (snapshot.Status != ClientMessageListStatus.Ready ||
            snapshot.ConversationId is not { } conversationId ||
            conversationId != attachmentDownloadConversationId ||
            conversationId != state.Context.ConversationId ||
            state.Context.ContextVersion != attachmentDownloadContextVersion)
        {
            return false;
        }

        var message = snapshot.Messages.FirstOrDefault(candidate =>
            candidate.ClientMessageId == state.Context.MessageClientId);
        attachment = message?.Attachments.FirstOrDefault(candidate =>
            candidate.AttachmentId == state.Context.AttachmentId)!;
        return attachment is not null &&
            attachment.MessageClientId == state.Context.MessageClientId &&
            ReferenceEquals(attachment.ImageState, state);
    }

    private void SynchronizeAttachmentImageEligibility(
        ClientAttachmentDownloadStateEntry entry,
        bool isEligible)
    {
        if (!isEligible && entry.ImageState.IsEligible)
        {
            CancelAttachmentThumbnailForRecycle(entry.ImageState);
            if (attachmentImageViewerOperation is { } operation &&
                ReferenceEquals(operation.State, entry.ImageState))
            {
                CloseAttachmentImageViewer(restoreFocus: false);
            }
        }

        entry.ImageState.SynchronizeEligibility(isEligible);
    }

    private void MarkAttachmentNoLongerDownloaded(ClientAttachmentDownloadContext context)
    {
        var key = ClientAttachmentViewKey.From(context);
        if (!attachmentDownloadStates.TryGetValue(key, out var entry) ||
            !ReferenceEquals(entry.State.Context, context) ||
            !ReferenceEquals(entry.ImageState.Context, context))
        {
            return;
        }

        entry.PersistedDownloaded = false;
        entry.PendingPersistedDownloaded = null;
        _ = entry.State.SynchronizePersistedDownloaded(isDownloaded: false);
        SynchronizeAttachmentImageEligibility(entry, isEligible: false);
        SetLiveText(
            MessageComposerStatusText,
            "本地图片状态已失效，请重新下载后再预览。");
    }

    private bool IsCurrentAttachmentRevealOperation(
        ClientAttachmentViewKey key,
        ClientAttachmentDownloadViewState state,
        ClientAttachmentRevealOperation operation) =>
        attachmentRevealOperations.TryGetValue(key, out var activeOperation) &&
        ReferenceEquals(activeOperation, operation) &&
        ReferenceEquals(operation.State, state) &&
        ReferenceEquals(operation.Context, state.Context) &&
        TryResolveCurrentAttachment(state, out _, out _);

    private void CompleteAttachmentRevealOperation(
        ClientAttachmentViewKey key,
        ClientAttachmentRevealOperation operation)
    {
        if (attachmentRevealOperations.TryGetValue(key, out var activeOperation) &&
            ReferenceEquals(activeOperation, operation))
        {
            attachmentRevealOperations.Remove(key);
        }

        operation.Dispose();
    }

    private void ResetAttachmentDownloadContext(Guid? conversationId)
    {
        attachmentDownloadContextVersion++;
        attachmentDownloadConversationId = conversationId;
        foreach (var operation in attachmentDownloadOperations.Values)
        {
            operation.Cancel();
        }

        attachmentDownloadOperations.Clear();
        foreach (var operation in attachmentRevealOperations.Values)
        {
            operation.Cancel();
        }

        attachmentRevealOperations.Clear();
        foreach (var operation in attachmentThumbnailOperations.Values)
        {
            operation.Cancel();
            operation.Dispose();
        }

        attachmentThumbnailOperations.Clear();
        CloseAttachmentImageViewer(restoreFocus: false);
        attachmentDownloadStates.Clear();
    }

    private void RaiseAttachmentDownloadLiveRegion(
        ClientAttachmentDownloadViewState state)
    {
        foreach (var textBlock in FindVisualDescendants<TextBlock>(MessageList))
        {
            if (textBlock.DataContext is not ClientMessageAttachmentPresentation attachment ||
                !ReferenceEquals(attachment.DownloadState, state) ||
                AutomationProperties.GetLiveSetting(textBlock) == AutomationLiveSetting.Off)
            {
                continue;
            }

            RaiseLiveRegionChanged(textBlock);
        }
    }

    private static void RaiseLiveRegionChanged(TextBlock textBlock)
    {
        ArgumentNullException.ThrowIfNull(textBlock);
        var peer = UIElementAutomationPeer.FromElement(textBlock) ??
            UIElementAutomationPeer.CreatePeerForElement(textBlock);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private readonly record struct ClientAttachmentViewKey(
        Guid MessageClientId,
        Guid AttachmentId)
    {
        public static ClientAttachmentViewKey From(
            ClientAttachmentDownloadContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            return new ClientAttachmentViewKey(
                context.MessageClientId,
                context.AttachmentId);
        }
    }

    private sealed class ClientAttachmentDownloadStateEntry(
        ClientAttachmentDownloadViewState state,
        ClientAttachmentImageViewState imageState,
        bool persistedDownloaded)
    {
        public ClientAttachmentDownloadViewState State { get; } = state;

        public ClientAttachmentImageViewState ImageState { get; } = imageState;

        public bool PersistedDownloaded { get; set; } = persistedDownloaded;

        public bool? PendingPersistedDownloaded { get; set; }
    }

    private sealed class ClientAttachmentDownloadOperation(
        ClientAttachmentDownloadFlight flight,
        CancellationTokenSource cancellation) : IDisposable
    {
        private int disposed;

        public ClientAttachmentDownloadFlight Flight { get; } = flight;

        public CancellationTokenSource Cancellation { get; } = cancellation;

        public void Cancel()
        {
            try
            {
                Cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                Cancellation.Dispose();
            }
        }
    }

    private sealed class ClientAttachmentRevealOperation(
        ClientAttachmentDownloadContext context,
        ClientAttachmentDownloadViewState state,
        CancellationTokenSource cancellation) : IDisposable
    {
        private int disposed;

        public ClientAttachmentDownloadContext Context { get; } = context ??
            throw new ArgumentNullException(nameof(context));

        public ClientAttachmentDownloadViewState State { get; } = state ??
            throw new ArgumentNullException(nameof(state));

        public CancellationTokenSource Cancellation { get; } = cancellation ??
            throw new ArgumentNullException(nameof(cancellation));

        public void Cancel()
        {
            try
            {
                Cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                Cancellation.Dispose();
            }
        }
    }

    private sealed class ClientAttachmentImageOperation(
        ClientAttachmentDownloadContext context,
        ClientAttachmentImageViewState state,
        CancellationTokenSource cancellation) : IDisposable
    {
        private int disposed;

        public ClientAttachmentDownloadContext Context { get; } = context ??
            throw new ArgumentNullException(nameof(context));

        public ClientAttachmentImageViewState State { get; } = state ??
            throw new ArgumentNullException(nameof(state));

        public CancellationTokenSource Cancellation { get; } = cancellation ??
            throw new ArgumentNullException(nameof(cancellation));

        public void Cancel()
        {
            try
            {
                Cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                Cancellation.Dispose();
            }
        }
    }

    private sealed class ClientAttachmentImageViewerOperation(
        ClientAttachmentDownloadContext context,
        ClientAttachmentImageViewState state,
        CancellationTokenSource cancellation) : IDisposable
    {
        private int disposed;

        public ClientAttachmentDownloadContext Context { get; } = context ??
            throw new ArgumentNullException(nameof(context));

        public ClientAttachmentImageViewState State { get; } = state ??
            throw new ArgumentNullException(nameof(state));

        public CancellationTokenSource Cancellation { get; } = cancellation ??
            throw new ArgumentNullException(nameof(cancellation));

        public void Cancel()
        {
            try
            {
                Cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                Cancellation.Dispose();
            }
        }
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

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in FindVisualDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }
}
