using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RelayCove.Client.Accounts;
using RelayCove.Client.Admin;
using RelayCove.Client.Attachments;
using RelayCove.Client.Controls;
using RelayCove.Client.Mentions;
using RelayCove.Client.Notifications;
using RelayCove.Client.Presentation;
using RelayCove.Client.Search;
using RelayCove.Client.Storage;
using RelayCove.Client.Sync;
using RelayCove.Client.Updates;
using RelayCove.Shared.Conversations;
using RelayCove.Shared.Messages;
using RelayCove.Shared.Users;

namespace RelayCove.Client;

public partial class MainWindow : Window
{
    private const double ComposerInputMinimumHeight = 58;
    private const double ComposerInputMaximumHeight = 200;
    private const double ComposerMessageListMinimumHeight = 120;
    private const int MaximumAttachmentThumbnailInProgressRetries = 15;
    private const int MaximumSearchHighlightMaterializationAttempts = 5;
    private static readonly TimeSpan AttachmentThumbnailRetryMinimumDelay =
        TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan AttachmentThumbnailRetryMaximumDelay =
        TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SearchHighlightDuration = TimeSpan.FromSeconds(2);
    private static readonly SolidColorBrush SearchHighlightBackground =
        CreateFrozenBrush(0xFF, 0xF3, 0xC4);
    private static readonly SolidColorBrush SearchHighlightBorder =
        CreateFrozenBrush(0xE5, 0x9A, 0x13);
    private static readonly SolidColorBrush MessageCardBackground =
        CreateFrozenBrush(0x00, 0x00, 0x00, 0x00);
    private static readonly SolidColorBrush MessageCardBorder =
        CreateFrozenBrush(0x00, 0x00, 0x00, 0x00);
    private readonly IReadOnlyList<ClientSearchScopeOption> messageSearchScopeOptions =
    [
        new(ClientSearchScope.Global, "全部会话"),
        new(ClientSearchScope.CurrentConversation, "当前会话"),
    ];
    private readonly ClientUpdateLoginPreflight loginPreflightAttemptGate = new();
    private ClientAccountShellCoordinator? accountShell;
    private Func<string, Task<bool>>? loginUpdatePreflight;
    private Func<Task>? checkForUpdates;
    private Func<Task>? downloadUpdate;
    private Action? cancelUpdateDownload;
    private Func<Task>? applyUpdate;
    private Action? requestExplicitExit;
    private bool mandatoryUpdateGate;
    private bool optionalUpdateActionApplies;
    private bool optionalUpdateActionCancels;
    private string? updateHandoffFailure;
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
        ClientAttachmentOpenOperation> attachmentOpenOperations = [];
    private readonly Dictionary<
        ClientAttachmentViewKey,
        ClientAttachmentImageOperation> attachmentThumbnailOperations = [];
    private readonly HashSet<string> collapsedConversationGroups =
        new(StringComparer.Ordinal);
    private ClientAttachmentImageViewerOperation? attachmentImageViewerOperation;
    private IInputElement? attachmentImageViewerRestoreFocus;
    private IInputElement? channelOverlayRestoreFocus;
    private IInputElement? settingsOverlayRestoreFocus;
    private IInputElement? searchOverlayRestoreFocus;
    private IReadOnlyList<ClientSearchResultPresentation> messageSearchResults =
        Array.Empty<ClientSearchResultPresentation>();
    private CancellationTokenSource? messageSearchCancellationSource;
    private CancellationTokenSource? searchNavigationCancellationSource;
    private CancellationTokenSource? mentionSearchCancellationSource;
    private CancellationTokenSource? channelParticipantCancellationSource;
    private SearchHighlightLease? searchHighlightLease;
    private DispatcherTimer? searchHighlightTimer;
    private long mentionSearchVersion;
    private long messageSearchVersion;
    private long searchNavigationVersion;
    private long attachmentSubmissionVersion;
    private long attachmentDownloadContextVersion;
    private Guid? attachmentDownloadConversationId;
    private ConversationParticipantListResponse? channelParticipants;
    private IReadOnlyList<UserDirectoryEntryDto> channelUserDirectory =
        Array.Empty<UserDirectoryEntryDto>();
    private bool composerContextReady;
    private bool suppressSelectionRequest;
    private bool applyingMessageSnapshot;
    private bool composerAvailable;
    private bool composerSubmissionRunning;
    private bool mentionSearchRunning;
    private bool suppressMentionSearchInputChanges;
    private bool messageSearchRunning;
    private bool searchNavigationRunning;
    private bool suppressMessageSearchInputChanges;
    private bool attachmentInputRunning;
    private bool channelOperationRunning;
    private CancellationTokenSource? attachmentInputCancellationSource;
    private int lastAnnouncedAttachmentIndex;
    private int lastAnnouncedAttachmentProgressBucket = -1;
    private IReadOnlyList<ClientConversationListItemPresentation> conversationItems =
        Array.Empty<ClientConversationListItemPresentation>();
    private ClientConversationFilter conversationFilter = ClientConversationFilter.All;
    private LocalCacheOperationStatus conversationListStatus =
        LocalCacheOperationStatus.AuthoritativeSnapshotRequired;
    private Guid? selectedConversationId;

    public MainWindow()
    {
        InitializeComponent();
        ApplicationTitleBar.IsMaximized = WindowState == WindowState.Maximized;
        UpdateConversationFilterPresentation();
        MessageSearchScopeComboBox.ItemsSource = messageSearchScopeOptions;
        MessageSearchScopeComboBox.SelectedIndex = 0;
        ChannelTypeComboBox.ItemsSource = new[]
        {
            ConversationType.PublicChannel,
            ConversationType.PrivateChannel,
        };
        ChannelTypeComboBox.SelectedIndex = 0;
        UpdateMessageSearchState();
        SynchronizeSettingsPanelPresentation();
        Loaded += OnMainWindowLoaded;
    }

    internal void BindAccountShell(ClientAccountShellCoordinator coordinator)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        if (accountShell is not null)
        {
            accountShell.SearchResultsInvalidated -= OnSearchResultsInvalidated;
        }

        ClearMessageSearchPresentation(closePanel: true, clearKeyword: true);
        accountShell = coordinator;
        coordinator.SearchResultsInvalidated += OnSearchResultsInvalidated;
        ApplyAccountShellSnapshot(coordinator.Snapshot);
        ApplyConversationListSnapshot(coordinator.ConversationList);
        ApplyMessageListSnapshot(coordinator.MessageList);
    }

    internal void BindUpdateActions(
        Func<string, Task<bool>> loginUpdatePreflight,
        Func<Task> checkForUpdates,
        Func<Task> downloadUpdate,
        Action cancelUpdateDownload,
        Func<Task> applyUpdate,
        Action requestExplicitExit)
    {
        this.loginUpdatePreflight = loginUpdatePreflight ??
            throw new ArgumentNullException(nameof(loginUpdatePreflight));
        this.checkForUpdates = checkForUpdates ??
            throw new ArgumentNullException(nameof(checkForUpdates));
        this.downloadUpdate = downloadUpdate ??
            throw new ArgumentNullException(nameof(downloadUpdate));
        this.cancelUpdateDownload = cancelUpdateDownload ??
            throw new ArgumentNullException(nameof(cancelUpdateDownload));
        this.applyUpdate = applyUpdate ?? throw new ArgumentNullException(nameof(applyUpdate));
        this.requestExplicitExit = requestExplicitExit ??
            throw new ArgumentNullException(nameof(requestExplicitExit));
    }

    internal void ApplyUpdateState(ClientUpdateState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var wasMandatoryUpdateGate = mandatoryUpdateGate;
        if (state.IsMandatory)
        {
            mandatoryUpdateGate = true;
        }
        else if (state.Phase is ClientUpdatePhase.NoUpdate or ClientUpdatePhase.OptionalAvailable)
        {
            mandatoryUpdateGate = false;
            updateHandoffFailure = null;
        }

        var version = state.Manifest?.Version;
        var status = state.Phase switch
        {
            ClientUpdatePhase.Idle => "更新：尚未检查",
            ClientUpdatePhase.Checking => "更新：正在检查…",
            ClientUpdatePhase.NoUpdate => "更新：已是最新版本",
            ClientUpdatePhase.OptionalAvailable => $"更新：可升级到 {version}" +
                (string.IsNullOrWhiteSpace(state.Manifest?.ReleaseNotes)
                    ? string.Empty
                    : $"\n更新说明：{state.Manifest.ReleaseNotes}"),
            ClientUpdatePhase.MandatoryAvailable => $"更新：必须升级到 {version}",
            ClientUpdatePhase.Downloading => "更新：正在下载…",
            ClientUpdatePhase.Downloaded => $"更新：{version} 已下载，等待安装",
            ClientUpdatePhase.Failed => "更新：检查或下载失败，可重试",
            _ => "更新：状态未知",
        };
        SetLiveText(UpdateStatusText, status);
        CheckForUpdatesButton.IsEnabled = checkForUpdates is not null && !mandatoryUpdateGate;
        SynchronizeSettingsPanelPresentation();
        optionalUpdateActionApplies = state.Phase == ClientUpdatePhase.Downloaded &&
            !mandatoryUpdateGate;
        optionalUpdateActionCancels = state.Phase == ClientUpdatePhase.Downloading &&
            !mandatoryUpdateGate;
        OptionalUpdateActionButton.Visibility = !mandatoryUpdateGate && state.Phase is
            ClientUpdatePhase.OptionalAvailable or ClientUpdatePhase.Downloading or ClientUpdatePhase.Downloaded
            ? Visibility.Visible
            : Visibility.Collapsed;
        OptionalUpdateActionButton.Content = optionalUpdateActionCancels
            ? "取消下载"
            : optionalUpdateActionApplies
                ? "关闭并更新"
                : "下载更新";
        OptionalUpdateActionButton.IsEnabled = optionalUpdateActionCancels
            ? cancelUpdateDownload is not null
            : optionalUpdateActionApplies
            ? applyUpdate is not null
            : downloadUpdate is not null;

        MandatoryUpdateOverlay.Visibility = mandatoryUpdateGate
            ? Visibility.Visible
            : Visibility.Collapsed;
        LoginPanel.IsEnabled = !mandatoryUpdateGate;
        AccountPanel.IsEnabled = !mandatoryUpdateGate;
        if (!mandatoryUpdateGate)
        {
            return;
        }

        // A mandatory update is the only modal surface allowed to outlive the
        // normal client UI. Close every other overlay before it receives focus so
        // a dismissed/replaced update never reveals a stale modal below it.
        CloseTransientOverlaysForMandatoryUpdate();

        var notes = state.Manifest?.ReleaseNotes;
        SetLiveText(
            MandatoryUpdateDetailText,
            string.IsNullOrWhiteSpace(version)
                ? "此客户端版本已不再受支持。请重新检查并下载更新后继续。"
                : $"当前客户端需要升级到 {version} 后才能继续使用。" +
                    (string.IsNullOrWhiteSpace(notes) ? string.Empty : $"\n\n更新说明：{notes}"));
        var progress = state.Progress;
        MandatoryUpdateProgressBar.Visibility = progress is null
            ? Visibility.Collapsed
            : Visibility.Visible;
        MandatoryUpdateProgressBar.Value = progress?.Percent ?? 0;
        SetLiveText(
            MandatoryUpdateProgressText,
            progress is null
                ? string.Empty
                : $"已下载 {progress.BytesWritten:N0} / {progress.TotalBytes:N0} 字节（{progress.Percent:F0}%）。");
        SetLiveText(
            MandatoryUpdateErrorText,
            updateHandoffFailure ?? DescribeUpdateFailure(state.Failure));
        RetryMandatoryUpdateButton.IsEnabled = checkForUpdates is not null &&
            state.Phase is not ClientUpdatePhase.Checking and not ClientUpdatePhase.Downloading;
        DownloadMandatoryUpdateButton.Visibility = state.Phase == ClientUpdatePhase.Downloaded
            ? Visibility.Collapsed
            : Visibility.Visible;
        DownloadMandatoryUpdateButton.IsEnabled = downloadUpdate is not null &&
            state.Manifest is not null && state.Phase is not ClientUpdatePhase.Checking and
            not ClientUpdatePhase.Downloading;
        CancelMandatoryUpdateButton.Visibility = state.Phase == ClientUpdatePhase.Downloading
            ? Visibility.Visible
            : Visibility.Collapsed;
        CancelMandatoryUpdateButton.IsEnabled = cancelUpdateDownload is not null;
        ApplyMandatoryUpdateButton.Visibility = state.Phase == ClientUpdatePhase.Downloaded
            ? Visibility.Visible
            : Visibility.Collapsed;
        ApplyMandatoryUpdateButton.IsEnabled = applyUpdate is not null;
        if (!wasMandatoryUpdateGate || !MandatoryUpdateOverlay.IsKeyboardFocusWithin)
        {
            _ = Dispatcher.BeginInvoke(
                FocusMandatoryUpdateAction,
                DispatcherPriority.Input);
        }
    }

    internal void ShowUpdateHandoffFailure(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        updateHandoffFailure = message;
        SetLiveText(MandatoryUpdateErrorText, message);
        RetryMandatoryUpdateButton.IsEnabled = checkForUpdates is not null;
        ApplyMandatoryUpdateButton.IsEnabled = applyUpdate is not null;
        OptionalUpdateActionButton.IsEnabled = applyUpdate is not null;
        FocusMandatoryUpdateAction();
    }

    internal void ShowUpdateHandoffConfirming()
    {
        updateHandoffFailure = null;
        SetLiveText(UpdateStatusText, "更新：正在确认交接…");
        SettingsOverlay.UpdateStatus = UpdateStatusText.Text;
        SetLiveText(MandatoryUpdateErrorText, string.Empty);
        SetLiveText(MandatoryUpdateProgressText, "更新程序正在确认交接，请稍候。");
        RetryMandatoryUpdateButton.IsEnabled = false;
        DownloadMandatoryUpdateButton.IsEnabled = false;
        CancelMandatoryUpdateButton.IsEnabled = false;
        ApplyMandatoryUpdateButton.IsEnabled = false;
        OptionalUpdateActionButton.IsEnabled = false;
        FocusMandatoryUpdateAction();
    }

    internal Guid? SelectedConversationId => selectedConversationId;

    internal System.Windows.Controls.Button CloseSettingsButton => SettingsOverlay.CloseButton;

    // Keep the existing internal presentation seam stable while the visual header
    // moves into a dedicated control.
    internal TextBlock ConversationHeadingText => ChatHeader.HeadingText;

    internal TextBlock NavigationNoticeText => ChatHeader.NoticeText;

    internal TextBlock ConversationMembersSummaryText => ChatHeader.MembersSummaryText;

    internal System.Windows.Controls.Button OpenChannelPanelButton => ChatHeader.MembersButton;

    internal System.Windows.Controls.Button OpenSearchButton => ChatHeader.SearchButton;

    internal void CancelAttachmentInputForShutdown()
    {
        if (attachmentInputCancellationSource is { IsCancellationRequested: false } cancellationSource)
        {
            cancellationSource.Cancel();
        }

        ResetAttachmentDownloadContext(conversationId: null);
        InvalidateMessageSearchFromUi(closePanel: true, clearKeyword: true);
        CloseChannelPanel(clearPresentation: true);
        if (accountShell is not null)
        {
            accountShell.SearchResultsInvalidated -= OnSearchResultsInvalidated;
        }
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
            ClearMessageSearchPresentation(closePanel: true, clearKeyword: true);
            CloseChannelPanel(clearPresentation: true);
            CloseSettingsOverlay(restoreFocus: false);
        }

        var presentation = ClientAccountShellPresenter.Present(snapshot);
        SetLiveText(HeadingText, presentation.Heading);
        SetLiveText(DetailText, presentation.Detail);
        SetLiveText(SidebarConnectionText, presentation.ConnectionLabel);
        SetLiveText(SidebarSyncText, presentation.SyncLabel);
        SetLiveText(SidebarDisplayNameText, string.IsNullOrWhiteSpace(presentation.DisplayName)
            ? "尚未登录"
            : presentation.DisplayName);
        ApplicationNavigation.AvatarText = string.IsNullOrWhiteSpace(presentation.DisplayName)
            ? "RC"
            : GetAvatarText(presentation.DisplayName, presentation.DisplayName);
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
        LoginBrandPanel.Visibility = presentation.ShowLogin
            ? Visibility.Visible
            : Visibility.Collapsed;
        LoginBrandColumn.Width = presentation.ShowLogin
            ? new GridLength(270)
            : new GridLength(0);
        HeadingText.Visibility = presentation.ShowLogin
            ? Visibility.Visible
            : Visibility.Collapsed;
        DetailText.Visibility = presentation.ShowLogin
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateResponsiveContentMargins();
        UpdateResponsiveShellColumns();
        UpdateChatContentWidth();
        BusyIndicator.Visibility = presentation.IsBusy
            ? Visibility.Visible
            : Visibility.Collapsed;
        LoginButton.IsEnabled = !presentation.IsBusy;
        ServerAddressTextBox.IsEnabled = !presentation.IsBusy;
        UserNameTextBox.IsEnabled = !presentation.IsBusy;
        PasswordInput.IsEnabled = !presentation.IsBusy;
        RetryButton.IsEnabled = presentation.CanRetry;
        LogoutButton.IsEnabled = presentation.CanLogout;
        OpenSearchButton.IsEnabled = snapshot.HasActiveAccount && !presentation.IsBusy;
        ChatHeader.IsMembersEnabled = snapshot.HasActiveAccount && !presentation.IsBusy;
        CheckForUpdatesButton.IsEnabled = checkForUpdates is not null &&
            snapshot.ServerBaseUri is not null && !mandatoryUpdateGate;
        SynchronizeSettingsPanelPresentation();
        UpdateMessageSearchState();
    }

    internal void ApplyConversationListSnapshot(LocalConversationListReadOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (outcome.Revision < lastConversationRevision)
        {
            return;
        }

        lastConversationRevision = outcome.Revision;
        conversationListStatus = outcome.Status;
        var previousSelectionId = SelectedConversationId;
        var items = ClientConversationListPresenter.Present(outcome);
        conversationItems = items;
        suppressSelectionRequest = true;
        try
        {
            var groupedItems = CollectionViewSource.GetDefaultView(items);
            groupedItems.Filter = candidate => candidate is
                ClientConversationListItemPresentation item && MatchesConversationFilter(item);
            groupedItems.GroupDescriptions.Clear();
            var groupDescription = new PropertyGroupDescription(
                nameof(ClientConversationListItemPresentation.GroupTitle));
            groupDescription.GroupNames.Add("公开频道");
            groupDescription.GroupNames.Add("私有频道");
            groupDescription.GroupNames.Add("私聊");
            groupedItems.GroupDescriptions.Add(groupDescription);
            groupedItems.Refresh();
            ConversationList.ItemsSource = groupedItems;
            ConversationList.IsEnabled = outcome.Status == LocalCacheOperationStatus.Ready;
            ConversationEmptyText.Visibility = groupedItems.IsEmpty
                ? Visibility.Visible
                : Visibility.Collapsed;
            SetLiveText(ConversationEmptyText, outcome.Status switch
            {
                LocalCacheOperationStatus.Ready when items.Count > 0 =>
                    "没有符合当前搜索或筛选条件的会话。",
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

            selectedConversationId = selection.Selection?.Id;
            ConversationList.SelectedItem = null;
            if (selection.Selection is not null &&
                MatchesConversationFilter(selection.Selection))
            {
                ConversationList.SelectedItem = selection.Selection;
                ConversationList.ScrollIntoView(selection.Selection);
            }

            ApplySelectedConversation(selection.Selection);
        }
        finally
        {
            suppressSelectionRequest = false;
        }

        accountShell?.SelectConversation(SelectedConversationId);
        UpdateMessageSearchState();
    }

    private bool MatchesConversationFilter(ClientConversationListItemPresentation item) =>
        ClientConversationFilterPolicy.Matches(
            item,
            conversationFilter,
            ConversationSearchTextBox.Text);

    private void OnConversationSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        RefreshConversationFilter();
    }

    private void OnConversationFilterClicked(object sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not System.Windows.Controls.Button { Tag: string filterName } ||
            !Enum.TryParse<ClientConversationFilter>(filterName, out var requestedFilter))
        {
            return;
        }

        SetConversationFilter(requestedFilter);
        ApplicationNavigation.SelectedSection = requestedFilter == ClientConversationFilter.Channels
            ? ClientNavigationSection.Channels
            : ClientNavigationSection.Chat;
    }

    private void SetConversationFilter(ClientConversationFilter requestedFilter)
    {
        if (conversationFilter == requestedFilter)
        {
            return;
        }

        conversationFilter = requestedFilter;
        UpdateConversationFilterPresentation();
        RefreshConversationFilter();
    }

    private void RefreshConversationFilter()
    {
        if (ConversationList?.ItemsSource is not ICollectionView view)
        {
            return;
        }

        var selected = selectedConversationId is { } selectedId
            ? conversationItems.FirstOrDefault(item => item.Id == selectedId)
            : null;
        suppressSelectionRequest = true;
        try
        {
            view.Refresh();
            ConversationList.SelectedItem = null;
            if (selected is not null && MatchesConversationFilter(selected))
            {
                ConversationList.SelectedItem = selected;
                ConversationList.ScrollIntoView(selected);
            }

            ConversationEmptyText.Visibility = view.IsEmpty
                ? Visibility.Visible
                : Visibility.Collapsed;
            if (view.IsEmpty)
            {
                SetLiveText(ConversationEmptyText, conversationListStatus switch
                {
                    LocalCacheOperationStatus.Ready when conversationItems.Count > 0 =>
                        "没有符合当前搜索或筛选条件的会话。",
                    LocalCacheOperationStatus.Ready => "暂无会话",
                    LocalCacheOperationStatus.AuthoritativeSnapshotRequired =>
                        "正在等待权威会话同步…",
                    LocalCacheOperationStatus.FatalScope =>
                        "本地会话缓存不可用；不会显示旧账户数据。",
                    _ => "会话列表暂时不可用，请稍后重试。",
                });
            }
        }
        finally
        {
            suppressSelectionRequest = false;
        }
    }

    private void UpdateConversationFilterPresentation()
    {
        if (AllConversationFilterButton is null)
        {
            return;
        }

        UpdateFilterButton(AllConversationFilterButton, ClientConversationFilter.All);
        UpdateFilterButton(UnreadConversationFilterButton, ClientConversationFilter.Unread);
        UpdateFilterButton(ChannelConversationFilterButton, ClientConversationFilter.Channels);
        UpdateFilterButton(DirectConversationFilterButton, ClientConversationFilter.Direct);
    }

    private void UpdateFilterButton(
        System.Windows.Controls.Button button,
        ClientConversationFilter filter)
    {
        button.SetResourceReference(
            System.Windows.Controls.Control.BackgroundProperty,
            conversationFilter == filter ? "RcPrimarySoftBrush" : "RcTransparentBrush");
        button.SetResourceReference(
            System.Windows.Controls.Control.ForegroundProperty,
            conversationFilter == filter ? "RcPrimaryBrush" : "RcTextSecondaryBrush");
        button.SetResourceReference(
            System.Windows.Controls.Control.BorderBrushProperty,
            conversationFilter == filter ? "RcPrimaryBrush" : "RcTransparentBrush");
    }

    private void OnNavigationRequested(
        object? sender,
        ClientNavigationRequestedEventArgs e)
    {
        _ = sender;
        switch (e.Section)
        {
            case ClientNavigationSection.Chat:
                ApplicationNavigation.SelectedSection = ClientNavigationSection.Chat;
                SetConversationFilter(ClientConversationFilter.All);
                break;
            case ClientNavigationSection.Channels:
                ApplicationNavigation.SelectedSection = ClientNavigationSection.Channels;
                SetConversationFilter(ClientConversationFilter.Channels);
                break;
            case ClientNavigationSection.Settings:
                var restoreTarget = Keyboard.FocusedElement;
                ApplicationNavigation.SelectedSection = ClientNavigationSection.Settings;
                CloseSearchPanelForOverlayTransition();
                CloseChannelPanel(clearPresentation: false);
                CloseAttachmentImageViewer(restoreFocus: false);
                OpenSettingsOverlay(restoreTarget);
                _ = CloseSettingsButton.Focus();
                break;
        }
    }

    private void OnCloseSettingsClicked(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        CloseSettingsOverlay(restoreFocus: true);
    }

    private void OnSettingsCloseRequested(object sender, RoutedEventArgs e)
    {
        OnCloseSettingsClicked(sender, e);
    }

    private void OnSettingsCheckForUpdatesRequested(object sender, RoutedEventArgs e)
    {
        OnCheckForUpdatesClicked(sender, e);
    }

    private void OnSettingsReconnectRequested(object sender, RoutedEventArgs e)
    {
        OnRetryClicked(sender, e);
    }

    private void OnSettingsExitAccountRequested(object sender, RoutedEventArgs e)
    {
        OnLogoutClicked(sender, e);
    }

    private void OnUnavailableFeatureRequested(
        object? sender,
        ClientUnavailableFeatureRequestedEventArgs e)
    {
        _ = sender;
        UnavailableFeatureNotice.ShowUnavailableFeature(e.DisplayName);
    }

    private void OnUnavailableFeatureButtonClicked(object sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not System.Windows.Controls.Button
            {
                Tag: ClientUiFeatureId featureId,
            })
        {
            return;
        }

        UnavailableFeatureNotice.ShowUnavailableFeature(featureId switch
        {
            ClientUiFeatureId.ConversationPin => "置顶会话",
            ClientUiFeatureId.ConversationNotifications => "会话通知",
            ClientUiFeatureId.ConversationMore => "更多会话操作",
            ClientUiFeatureId.Emoji => "表情回应",
            ClientUiFeatureId.VoiceInput => "语音输入",
            ClientUiFeatureId.ScreenCapture => "主动截图",
            ClientUiFeatureId.SendOptions => "发送选项",
            ClientUiFeatureId.MessageReaction => "表情回应",
            ClientUiFeatureId.MessageForward => "转发消息",
            ClientUiFeatureId.MessageBookmark => "收藏消息",
            ClientUiFeatureId.MessagePin => "置顶消息",
            ClientUiFeatureId.MessageDelete => "删除消息",
            ClientUiFeatureId.MessageMore => "更多消息操作",
            _ => "该功能",
        });
    }

    private void OnConversationGroupExpanderLoaded(object sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not Expander { Tag: string groupName } expander)
        {
            return;
        }

        expander.IsExpanded = !collapsedConversationGroups.Contains(groupName);
        AutomationProperties.SetName(
            expander,
            $"{groupName}{(expander.IsExpanded ? "，已展开" : "，已折叠")}分组");
    }

    private void OnConversationGroupExpanded(object sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not Expander { Tag: string groupName } expander)
        {
            return;
        }

        collapsedConversationGroups.Remove(groupName);
        AutomationProperties.SetName(expander, $"{groupName}，已展开分组");
    }

    private void OnConversationGroupCollapsed(object sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not Expander { Tag: string groupName } expander)
        {
            return;
        }

        collapsedConversationGroups.Add(groupName);
        AutomationProperties.SetName(expander, $"{groupName}，已折叠分组");
    }

    internal void ShowAuthorizedNotificationTarget(
        ClientNotificationActivationTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Kind == ClientNotificationActivationKind.Message &&
            target.ConversationId is { } conversationId)
        {
            _ = SelectConversationForAuthoritativeNavigation(conversationId);

            accountShell?.SelectConversation(conversationId, target.MessageId);
        }
        else
        {
            ConversationList.Focus();
        }

        SetChatHeaderNotice(target.Kind switch
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
        if (!searchNavigationRunning &&
            searchHighlightLease is { } searchLease &&
            (snapshot.ConversationId != searchLease.ConversationId ||
             snapshot.TargetMessageId != searchLease.MessageId ||
             snapshot.Status is not ClientMessageListStatus.Loading and
                 not ClientMessageListStatus.Ready))
        {
            ClearSearchHighlight();
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
        var searchTargetOwnsAcknowledgment = IsSearchHighlightTarget(snapshot);
        var searchTargetMaterializedNow = false;

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
            MessageStatusText.Visibility = snapshot.Status == ClientMessageListStatus.Ready
                ? Visibility.Collapsed
                : Visibility.Visible;
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

            if (searchTargetOwnsAcknowledgment)
            {
                searchTargetMaterializedNow = TryMaterializeSearchHighlight(snapshot);
                if (!searchTargetMaterializedNow)
                {
                    ScheduleSearchHighlightMaterialization();
                }
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
            var observedThroughMessageId = decision.ObservedThroughMessageId;
            if (searchTargetOwnsAcknowledgment)
            {
                observedThroughMessageId = searchTargetMaterializedNow
                    ? snapshot.TargetMessageId
                    : null;
                if (searchTargetMaterializedNow && searchHighlightLease is { } lease)
                {
                    lease.TargetAcknowledged = true;
                }
            }

            accountShell?.AcknowledgeMessageSnapshotApplied(
                conversationId,
                snapshot.Revision,
                observedThroughMessageId,
                searchTargetOwnsAcknowledgment && !searchTargetMaterializedNow
                    ? false
                    : IsNearBottom(scrollViewer));
        }

        UpdateMessageSearchState();
    }

    internal void SetNotificationAvailability(bool? isAvailable)
    {
        SetLiveText(
            SidebarNotificationText,
            ClientAccountShellPresenter.DescribeNotificationAvailability(isAvailable));
        SettingsOverlay.NotificationStatus = SidebarNotificationText.Text;
    }

    private void OnChatHeaderMembersRequested(
        object? sender,
        ChatHeaderMembersRequestedEventArgs e) =>
        OnOpenChannelPanelClicked(ChatHeader.MembersButton, e);

    private void OnChatHeaderSearchRequested(
        object? sender,
        ChatHeaderSearchRequestedEventArgs e) =>
        OnOpenSearchClicked(ChatHeader.SearchButton, e);

    private async void OnOpenChannelPanelClicked(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (ActualWidth < 1400)
        {
            CloseChannelPanel(clearPresentation: false);
            const string narrowMemberNotice = "窗口较窄时请扩大窗口后再管理成员。";
            SetLiveText(ChannelLiveRegionText, narrowMemberNotice);
            UnavailableFeatureNotice.ShowNotice(narrowMemberNotice);
            return;
        }

        if (accountShell?.AdminCoordinator is null || !accountShell.Snapshot.HasActiveAccount)
        {
            SetLiveText(ChannelLiveRegionText, "请先登录后再管理频道。");
            return;
        }

        var restoreTarget = sender as IInputElement ?? Keyboard.FocusedElement;
        CloseSearchPanelForOverlayTransition();
        CloseSettingsOverlay(restoreFocus: false);
        CloseAttachmentImageViewer(restoreFocus: false);

        if (sender is System.Windows.Controls.Button
            {
                DataContext: CollectionViewGroup { Name: string groupTitle },
            })
        {
            ChannelTypeComboBox.SelectedItem = groupTitle == "私有频道"
                ? ConversationType.PrivateChannel
                : ConversationType.PublicChannel;
            CreateChannelExpander.IsExpanded = true;
        }

        OpenChannelOverlay(restoreTarget);
        UpdateMemberDrawerLayout();
        ChannelMemberSearchTextBox.Clear();
        UpdateChannelPanelState();
        await LoadChannelUserDirectoryAsync();
        if (SelectedConversationId is { } conversationId)
        {
            await LoadConversationParticipantsAsync(conversationId);
        }

        ChannelNameInput.Focus();
    }

    private void OnCloseChannelPanelClicked(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var restoreTarget = channelOverlayRestoreFocus;
        CloseChannelPanel(clearPresentation: false);
        RestoreOverlayFocus(restoreTarget, OpenChannelPanelButton);
    }

    private async void OnCreateChannelClicked(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var coordinator = accountShell?.AdminCoordinator;
        if (coordinator is null ||
            ChannelTypeComboBox.SelectedItem is not ConversationType type ||
            channelOperationRunning)
        {
            return;
        }

        channelOperationRunning = true;
        UpdateChannelPanelState();
        SetLiveText(ChannelLiveRegionText, "正在创建频道…");
        try
        {
            var result = await coordinator.CreateConversationForChatAsync(
                new CreateConversationRequest(type, ChannelNameInput.Text));
            if (result.Status != ClientAdminRequestStatus.Completed || result.Value is null)
            {
                SetLiveText(
                    ChannelLiveRegionText,
                    $"创建失败：{DescribeAdminStatus(result.Status)}。");
                return;
            }

            var created = result.Value;
            ChannelNameInput.Clear();
            pendingConversationSelectionId = created.Id;
            SetLiveText(
                ChannelLiveRegionText,
                type == ConversationType.PrivateChannel
                    ? "私有频道已创建；正在同步，随后可从左侧目录拉入成员。"
                    : "公开频道已创建；所有正常成员会自动看到它。");
            if (accountShell is not null)
            {
                await accountShell.RefreshConversationsAsync();
            }

            SelectSearchConversation(created.Id);
            await LoadConversationParticipantsAsync(created.Id);
        }
        finally
        {
            channelOperationRunning = false;
            UpdateChannelPanelState();
        }
    }

    private void OnChannelNameTextChanged(object sender, TextChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        UpdateChannelPanelState();
    }

    private void OnChannelMemberSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        ApplyChannelParticipantPresentation();
    }

    private void OnToggleAccountDiagnosticsClicked(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var restoreTarget = Keyboard.FocusedElement;
        CloseSearchPanelForOverlayTransition();
        CloseChannelPanel(clearPresentation: false);
        CloseAttachmentImageViewer(restoreFocus: false);
        OpenSettingsOverlay(restoreTarget);
        ApplicationNavigation.SelectedSection = ClientNavigationSection.Settings;
        _ = CloseSettingsButton.Focus();
    }

    private async void OnInviteChannelMemberClicked(object sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not System.Windows.Controls.Button { DataContext: ChannelUserPresentation user } ||
            !user.CanInvite ||
            channelParticipants is not
            {
                Type: ConversationType.PrivateChannel,
                CanManageMembers: true,
            } participants ||
            accountShell?.AdminCoordinator is not { } coordinator ||
            channelOperationRunning)
        {
            return;
        }

        channelOperationRunning = true;
        UpdateChannelPanelState();
        SetLiveText(ChannelLiveRegionText, $"正在把 {user.DisplayName} 拉入私有频道…");
        try
        {
            var result = await coordinator.UpsertConversationMemberForChatAsync(
                participants.ConversationId,
                new UpsertConversationMemberRequest(
                    user.UserId,
                    ConversationMemberRole.Member));
            SetLiveText(
                ChannelLiveRegionText,
                result.Status == ClientAdminRequestStatus.Completed
                    ? $"已拉入 {user.DisplayName}；对方在线时会立即同步显示频道。"
                    : $"拉入失败：{DescribeAdminStatus(result.Status)}。");
            if (result.Status == ClientAdminRequestStatus.Completed)
            {
                await LoadConversationParticipantsAsync(participants.ConversationId);
            }
        }
        finally
        {
            channelOperationRunning = false;
            UpdateChannelPanelState();
        }
    }

    private async void OnRemoveChannelMemberClicked(object sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not System.Windows.Controls.Button { DataContext: ChannelUserPresentation user } ||
            !user.CanRemove ||
            channelParticipants is not
            {
                Type: ConversationType.PrivateChannel,
                CanManageMembers: true,
            } participants ||
            accountShell?.AdminCoordinator is not { } coordinator ||
            channelOperationRunning)
        {
            return;
        }

        channelOperationRunning = true;
        UpdateChannelPanelState();
        SetLiveText(ChannelLiveRegionText, $"正在移除 {user.DisplayName}…");
        try
        {
            var result = await coordinator.RemoveConversationMemberForChatAsync(
                participants.ConversationId,
                user.UserId);
            SetLiveText(
                ChannelLiveRegionText,
                result.Status == ClientAdminRequestStatus.Completed
                    ? $"已移除 {user.DisplayName}。"
                    : $"移除失败：{DescribeAdminStatus(result.Status)}。");
            if (result.Status == ClientAdminRequestStatus.Completed)
            {
                await LoadConversationParticipantsAsync(participants.ConversationId);
            }
        }
        finally
        {
            channelOperationRunning = false;
            UpdateChannelPanelState();
        }
    }

    private async Task LoadChannelUserDirectoryAsync()
    {
        var coordinator = accountShell?.AdminCoordinator;
        if (coordinator is null)
        {
            return;
        }

        var result = await coordinator.GetUserDirectoryAsync();
        if (!ReferenceEquals(accountShell?.AdminCoordinator, coordinator))
        {
            return;
        }

        if (result.Status == ClientAdminRequestStatus.Completed && result.Value is not null)
        {
            channelUserDirectory = result.Value;
            ApplyChannelParticipantPresentation();
        }
        else if (ChannelOverlay.Visibility == Visibility.Visible)
        {
            SetLiveText(
                ChannelLiveRegionText,
                $"团队成员目录加载失败：{DescribeAdminStatus(result.Status)}。");
        }
    }

    private async Task LoadConversationParticipantsAsync(Guid conversationId)
    {
        var coordinator = accountShell?.AdminCoordinator;
        if (coordinator is null || conversationId == Guid.Empty)
        {
            return;
        }

        channelParticipantCancellationSource?.Cancel();
        using var cancellationSource = new CancellationTokenSource();
        channelParticipantCancellationSource = cancellationSource;
        SetChatHeaderMembersSummary("成员：正在加载…");
        var result = await coordinator.GetConversationParticipantsAsync(
            conversationId,
            cancellationSource.Token);
        if (cancellationSource.IsCancellationRequested ||
            !ReferenceEquals(channelParticipantCancellationSource, cancellationSource) ||
            SelectedConversationId != conversationId ||
            !ReferenceEquals(accountShell?.AdminCoordinator, coordinator))
        {
            return;
        }

        channelParticipantCancellationSource = null;
        if (result.Status == ClientAdminRequestStatus.Completed && result.Value is not null)
        {
            channelParticipants = result.Value;
            SetChatHeaderMembersSummary(
                result.Value.Participants.Count == 0
                    ? "成员：暂无正常成员"
                    : $"成员（{result.Value.Participants.Count}）：" +
                      string.Join("、", result.Value.Participants.Select(user => user.DisplayName)));
            ApplyChannelParticipantPresentation();
        }
        else
        {
            channelParticipants = null;
            SetChatHeaderMembersSummary("成员：暂时无法加载");
            ApplyChannelParticipantPresentation();
        }
    }

    private void ApplyChannelParticipantPresentation()
    {
        var coordinator = accountShell?.AdminCoordinator;
        var currentUserId = coordinator?.CurrentUserId ?? Guid.Empty;
        var participantIds = channelParticipants?.Participants
            .Select(user => user.UserId)
            .ToHashSet() ?? [];
        var canManage = channelParticipants is
        {
            Type: ConversationType.PrivateChannel,
            CanManageMembers: true,
        };
        var isDirect = channelParticipants?.Type == ConversationType.Direct;
        var filter = ChannelMemberSearchTextBox.Text.Trim();
        var participants = channelParticipants?.Participants
            .Where(user => MatchesChannelMemberFilter(user, filter))
            .Select(user => new ChannelUserPresentation(
                user.UserId,
                user.UserName,
                user.DisplayName,
                GetAvatarText(user.DisplayName, user.UserName),
                canManage && user.UserId == currentUserId ? "可管理成员" : "频道成员",
                CanInvite: false,
                CanRemove: canManage && user.UserId != currentUserId))
            .ToArray() ?? [];
        ChannelParticipantList.ItemsSource = participants;
        ChannelUserDirectorySection.Visibility = isDirect
            ? Visibility.Collapsed
            : Visibility.Visible;
        ChannelUserDirectoryList.ItemsSource = channelUserDirectory
            .Where(user => MatchesChannelMemberFilter(user, filter))
            .Select(user => new ChannelUserPresentation(
                user.UserId,
                user.UserName,
                user.DisplayName,
                GetAvatarText(user.DisplayName, user.UserName),
                participantIds.Contains(user.UserId) ? "已在频道" : "可添加",
                CanInvite: canManage &&
                    user.UserId != currentUserId &&
                    !participantIds.Contains(user.UserId),
                CanRemove: false))
            .ToArray();
        SetLiveText(
            ChannelCurrentHeadingText,
            channelParticipants is null
                ? "当前会话成员"
                : isDirect
                    ? $"私聊成员（{channelParticipants.Participants.Count}）"
                    : $"当前会话成员（{channelParticipants.Participants.Count}）");
        ChannelPanelSubtitleText.Text = isDirect
            ? "显示此私聊的全部参与成员"
            : "查看成员并管理私有频道";
        SetLiveText(
            ChannelMemberHelpText,
            channelParticipants switch
            {
                null => "选择会话后显示成员。",
                { Type: ConversationType.PublicChannel } => "公开频道显示全部正常成员。",
                { Type: ConversationType.Direct } => "私聊显示全部参与成员。",
                { CanManageMembers: true } => "你可以从左侧拉人，或从这里移除成员。",
                _ => "私有频道只有频道管理员可以增删成员。",
            });
        UpdateChannelPanelState();
    }

    private static bool MatchesChannelMemberFilter(
        UserDirectoryEntryDto user,
        string filter) =>
        filter.Length == 0 ||
        user.DisplayName.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
        user.UserName.Contains(filter, StringComparison.OrdinalIgnoreCase);

    private static string GetAvatarText(string displayName, string userName)
    {
        var source = (string.IsNullOrWhiteSpace(displayName) ? userName : displayName).Trim();
        return source.Length == 0
            ? "?"
            : StringInfo.GetNextTextElement(source).ToUpperInvariant();
    }

    private void UpdateChannelPanelState()
    {
        if (CreateChannelButton is null)
        {
            return;
        }

        var available = accountShell?.Snapshot.HasActiveAccount == true &&
            accountShell.AdminCoordinator is not null;
        ChannelNameInput.IsEnabled = available && !channelOperationRunning;
        ChannelTypeComboBox.IsEnabled = available && !channelOperationRunning;
        CreateChannelButton.IsEnabled = available &&
            !channelOperationRunning &&
            !string.IsNullOrWhiteSpace(ChannelNameInput.Text);
        ChannelParticipantList.IsEnabled = !channelOperationRunning;
        ChannelUserDirectoryList.IsEnabled = !channelOperationRunning;
    }

    private void OpenChannelOverlay(IInputElement? restoreTarget)
    {
        if (ChannelOverlay.Visibility != Visibility.Visible)
        {
            channelOverlayRestoreFocus = restoreTarget ?? Keyboard.FocusedElement;
        }

        ChannelOverlay.Visibility = Visibility.Visible;
    }

    private void CloseChannelPanel(bool clearPresentation)
    {
        ChannelOverlay.Visibility = Visibility.Collapsed;
        channelOverlayRestoreFocus = null;
        UpdateMemberDrawerLayout();
        if (!clearPresentation)
        {
            return;
        }

        channelParticipantCancellationSource?.Cancel();
        channelParticipantCancellationSource = null;
        channelParticipants = null;
        channelUserDirectory = Array.Empty<UserDirectoryEntryDto>();
        ChannelParticipantList.ItemsSource = null;
        ChannelUserDirectoryList.ItemsSource = null;
        SetChatHeaderMembersSummary("成员：请选择会话");
        SetLiveText(ChannelLiveRegionText, string.Empty);
    }

    private void OpenSettingsOverlay(IInputElement? restoreTarget)
    {
        if (SettingsOverlay.Visibility != Visibility.Visible)
        {
            settingsOverlayRestoreFocus = restoreTarget ?? Keyboard.FocusedElement;
        }

        SettingsOverlay.Visibility = Visibility.Visible;
    }

    private void CloseSettingsOverlay(bool restoreFocus)
    {
        if (SettingsOverlay.Visibility != Visibility.Visible)
        {
            return;
        }

        var restoreTarget = settingsOverlayRestoreFocus;
        settingsOverlayRestoreFocus = null;
        SettingsOverlay.Visibility = Visibility.Collapsed;
        if (ApplicationNavigation.SelectedSection == ClientNavigationSection.Settings)
        {
            ApplicationNavigation.SelectedSection = ClientNavigationSection.Chat;
        }

        if (restoreFocus)
        {
            RestoreOverlayFocus(restoreTarget, ApplicationNavigation);
        }
    }

    private void SynchronizeSettingsPanelPresentation()
    {
        SettingsOverlay.DisplayName = SidebarDisplayNameText.Text;
        SettingsOverlay.ServerAddress = SidebarServerText.Text;
        SettingsOverlay.ConnectionStatus = SidebarConnectionText.Text;
        SettingsOverlay.SyncStatus = SidebarSyncText.Text;
        SettingsOverlay.NotificationStatus = SidebarNotificationText.Text;
        SettingsOverlay.UpdateStatus = UpdateStatusText.Text;
        SettingsOverlay.CanCheckForUpdates = CheckForUpdatesButton.IsEnabled;
        SettingsOverlay.CanReconnect = RetryButton.IsEnabled;
        SettingsOverlay.CanExitAccount = LogoutButton.IsEnabled;
    }

    private void CloseSearchPanelForOverlayTransition()
    {
        if (SearchPanel.Visibility == Visibility.Visible)
        {
            InvalidateMessageSearchFromUi(closePanel: true, clearKeyword: true);
        }
    }

    private void CloseSearchPanelAfterNavigation()
    {
        SearchPanel.Visibility = Visibility.Collapsed;
        SetSearchModalState(isOpen: false);
        searchOverlayRestoreFocus = null;
        _ = MessageList.Focus();
    }

    private void CloseTransientOverlaysForMandatoryUpdate()
    {
        CloseAttachmentImageViewer(restoreFocus: false);
        CloseSearchPanelForOverlayTransition();
        CloseChannelPanel(clearPresentation: false);
        CloseSettingsOverlay(restoreFocus: false);
    }

    private static void RestoreOverlayFocus(IInputElement? restoreTarget, UIElement fallback)
    {
        if (restoreTarget is UIElement { IsVisible: true, IsEnabled: true } element &&
            element.Focus())
        {
            return;
        }

        if (restoreTarget is ContentElement { IsEnabled: true } contentElement &&
            contentElement.Focus())
        {
            return;
        }

        if (fallback.IsVisible && fallback.IsEnabled)
        {
            _ = fallback.Focus();
        }
    }

    private void OnMainWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _ = sender;
        UpdateResponsiveContentMargins();
        UpdateResponsiveShellColumns();
        UpdateChatContentWidth();
        if (e.NewSize.Width < 1400 && ChannelOverlay.Visibility == Visibility.Visible)
        {
            var restoreTarget = channelOverlayRestoreFocus;
            CloseChannelPanel(clearPresentation: false);
            RestoreOverlayFocus(restoreTarget, OpenChannelPanelButton);
            return;
        }

        UpdateMemberDrawerLayout();
    }

    private void OnMainWindowStateChanged(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        ApplicationTitleBar.IsMaximized = WindowState == WindowState.Maximized;
    }

    private void OnTitleBarDragRequested(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        DragMove();
    }

    private void OnTitleBarMinimizeRequested(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        SystemCommands.MinimizeWindow(this);
    }

    private void OnTitleBarMaximizeRestoreRequested(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (WindowState == WindowState.Maximized)
        {
            SystemCommands.RestoreWindow(this);
            return;
        }

        SystemCommands.MaximizeWindow(this);
    }

    private void OnTitleBarCloseRequested(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        Close();
    }

    private void OnTitleBarSystemMenuRequested(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        ShowWindowSystemMenu();
    }

    private void ShowWindowSystemMenu()
    {
        var menuPoint = PointToScreen(new System.Windows.Point(8, 48));
        SystemCommands.ShowSystemMenu(this, menuPoint);
    }

    private void UpdateResponsiveContentMargins()
    {
        if (MainContentPanel is null || MainWorkspace is null || LoginPanel is null)
        {
            return;
        }

        if (LoginPanel.Visibility == Visibility.Visible)
        {
            var compactLoginLayout = ActualHeight > 0 && ActualHeight <= 560;
            MainContentPanel.Margin = compactLoginLayout
                ? new Thickness(24, 12, 24, 10)
                : ActualWidth > 0 && ActualWidth < 1100
                    ? new Thickness(24, 20, 24, 20)
                    : new Thickness(42, 34, 42, 34);
            MainWorkspace.Margin = compactLoginLayout
                ? new Thickness(0, 8, 0, 4)
                : ActualHeight > 0 && ActualHeight <= 720
                    ? new Thickness(0, 16, 0, 12)
                    : new Thickness(0, 30, 0, 24);
            LoginPanel.Padding = compactLoginLayout
                ? new Thickness(28, 18, 28, 18)
                : new Thickness(34);
            return;
        }

        MainContentPanel.Margin = ActualHeight > 0 && ActualHeight <= 720
            ? new Thickness(12, 4, 12, 4)
            : new Thickness(16);
        MainWorkspace.Margin = ActualHeight > 0 && ActualHeight <= 720
            ? new Thickness(0)
            : new Thickness(0, 0, 0, 10);
    }

    private void UpdateResponsiveShellColumns()
    {
        if (NavigationRailColumn is null || ConversationPanelColumn is null)
        {
            return;
        }

        NavigationRailColumn.Width = new GridLength(ActualWidth > 0 && ActualWidth < 1100
            ? 64
            : 72);
        ConversationPanelColumn.Width = new GridLength(ActualWidth switch
        {
            > 0 and < 1100 => 280,
            < 1400 => 320,
            _ => 340,
        });
        if (ComposerSupplementaryActionsPanel is not null)
        {
            ComposerSupplementaryActionsPanel.Visibility = ActualWidth > 0 && ActualWidth < 1100
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        if (LoginPanel.Visibility == Visibility.Visible)
        {
            var showBrandPanel = ActualWidth <= 0 || ActualWidth >= 1100;
            LoginBrandPanel.Visibility = showBrandPanel
                ? Visibility.Visible
                : Visibility.Collapsed;
            LoginBrandColumn.Width = showBrandPanel
                ? new GridLength(270)
                : new GridLength(0);
        }
    }

    private void OnMainWindowLoaded(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        UpdateChatContentWidth();
    }

    private void UpdateChatContentWidth()
    {
        if (ConversationChatPanel is null ||
            MessageList is null ||
            ChatHeader is null ||
            ComposerSurface is null)
        {
            return;
        }

        var availableWidth = Math.Max(0, ConversationChatPanel.ActualWidth - 56);
        if (availableWidth < 1)
        {
            return;
        }

        var contentWidth = Math.Min(1400, availableWidth);
        MessageList.Width = contentWidth;
        ChatHeader.Width = contentWidth;
        ComposerSurface.Width = contentWidth;
    }

    private void UpdateMemberDrawerLayout()
    {
        if (ConversationChatPanel is null)
        {
            return;
        }

        // The shell remains exactly three columns. Member management is a root-level
        // drawer, like settings, and must never reflow or compress the composer.
        ConversationChatPanel.Margin = new Thickness(0);
    }

    private static string DescribeAdminStatus(ClientAdminRequestStatus status) => status switch
    {
        ClientAdminRequestStatus.ValidationFailed => "输入未通过服务端验证",
        ClientAdminRequestStatus.TransientFailure => "服务器暂时不可用",
        ClientAdminRequestStatus.ProtocolError => "服务器响应无效",
        ClientAdminRequestStatus.Canceled => "已有操作正在进行",
        _ => "远端操作失败",
    };

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

    private void SetChatHeaderHeading(string value)
    {
        if (string.Equals(ChatHeader.Heading, value, StringComparison.Ordinal))
        {
            return;
        }

        ChatHeader.Heading = value;
        RaiseLiveRegionChanged(ChatHeader.HeadingText);
    }

    private void SetChatHeaderNotice(string value)
    {
        if (string.Equals(ChatHeader.Notice, value, StringComparison.Ordinal))
        {
            return;
        }

        ChatHeader.Notice = value;
        RaiseLiveRegionChanged(ChatHeader.NoticeText);
    }

    private void SetChatHeaderMembersSummary(string value)
    {
        if (string.Equals(ChatHeader.MembersSummary, value, StringComparison.Ordinal))
        {
            return;
        }

        ChatHeader.MembersSummary = value;
        RaiseLiveRegionChanged(ChatHeader.MembersSummaryText);
    }

    private void OnOpenSearchClicked(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (SearchPanel.Visibility != Visibility.Visible)
        {
            searchOverlayRestoreFocus = Keyboard.FocusedElement;
        }

        CloseChannelPanel(clearPresentation: false);
        CloseSettingsOverlay(restoreFocus: false);
        CloseAttachmentImageViewer(restoreFocus: false);
        SearchPanel.Visibility = Visibility.Visible;
        SetSearchModalState(isOpen: true);
        UpdateMessageSearchState();
        MessageSearchTextBox.Focus();
        MessageSearchTextBox.SelectAll();
    }

    private void OnCloseSearchClicked(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        var restoreTarget = searchOverlayRestoreFocus;
        InvalidateMessageSearchFromUi(closePanel: true, clearKeyword: true);
        RestoreOverlayFocus(restoreTarget, ConversationList);
    }

    private void SetSearchModalState(bool isOpen)
    {
        var isInteractive = !isOpen && !mandatoryUpdateGate;
        ApplicationNavigation.IsEnabled = isInteractive;
        ConversationPanelContainer.IsEnabled = isInteractive;
    }

    private void OnMessageSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (suppressMessageSearchInputChanges)
        {
            return;
        }

        InvalidateMessageSearchFromUi(closePanel: false, clearKeyword: false);
    }

    private async void OnMessageSearchPreviewKeyDown(
        object sender,
        System.Windows.Input.KeyEventArgs e)
    {
        _ = sender;
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None)
        {
            return;
        }

        e.Handled = true;
        await RunMessageSearchAsync();
    }

    private void OnMessageSearchScopeChanged(object sender, SelectionChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (suppressMessageSearchInputChanges)
        {
            return;
        }

        InvalidateMessageSearchFromUi(closePanel: false, clearKeyword: false);
    }

    private async void OnRunSearchClicked(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        await RunMessageSearchAsync();
    }

    private async Task RunMessageSearchAsync()
    {
        var coordinator = accountShell;
        var scope = (MessageSearchScopeComboBox.SelectedItem as ClientSearchScopeOption)?.Scope ??
            ClientSearchScope.Global;
        if (coordinator is null ||
            !ClientSearchPolicy.TryNormalizeKeyword(
                MessageSearchTextBox.Text,
                out var normalizedKeyword))
        {
            SetLiveText(
                MessageSearchStatusText,
                "请输入 1–64 个有效字符的关键词后再搜索。");
            UpdateMessageSearchState();
            return;
        }

        if (scope == ClientSearchScope.CurrentConversation &&
            (displayedMessageSnapshot?.Status != ClientMessageListStatus.Ready ||
             displayedMessageSnapshot.ConversationId != SelectedConversationId))
        {
            SetLiveText(MessageSearchStatusText, "请先选择并打开一个可用会话。");
            UpdateMessageSearchState();
            return;
        }

        CancelMessageSearchOperation(messageSearchCancellationSource);
        messageSearchCancellationSource = new CancellationTokenSource();
        var cancellationSource = messageSearchCancellationSource;
        var version = ++messageSearchVersion;
        ++searchNavigationVersion;
        CancelMessageSearchOperation(searchNavigationCancellationSource);
        searchNavigationCancellationSource = null;
        searchNavigationRunning = false;
        ClearSearchHighlight();
        messageSearchResults = Array.Empty<ClientSearchResultPresentation>();
        MessageSearchResultList.ItemsSource = null;
        messageSearchRunning = true;
        SetLiveText(MessageSearchStatusText, "正在搜索已授权的聊天记录…");
        UpdateMessageSearchState();

        ClientSearchOutcome outcome;
        try
        {
            outcome = await coordinator.SearchMessagesAsync(
                normalizedKeyword,
                scope,
                cancellationToken: cancellationSource.Token);
        }
        catch (OperationCanceledException)
        {
            outcome = ClientSearchOutcome.Failure(ClientSearchStatus.Canceled);
        }
        catch (ObjectDisposedException)
        {
            outcome = ClientSearchOutcome.Failure(ClientSearchStatus.Stale);
        }
        finally
        {
            cancellationSource.Dispose();
        }

        if (version != messageSearchVersion ||
            !ReferenceEquals(accountShell, coordinator) ||
            !ReferenceEquals(messageSearchCancellationSource, cancellationSource))
        {
            if (ReferenceEquals(messageSearchCancellationSource, cancellationSource))
            {
                messageSearchCancellationSource = null;
                messageSearchRunning = false;
                UpdateMessageSearchState();
            }

            return;
        }

        messageSearchCancellationSource = null;
        messageSearchRunning = false;
        if (outcome.Status == ClientSearchStatus.Completed)
        {
            messageSearchResults = outcome.Results
                .Select(ClientSearchResultPresentation.Create)
                .ToList()
                .AsReadOnly();
            MessageSearchResultList.ItemsSource = messageSearchResults;
            SetLiveText(
                MessageSearchStatusText,
                messageSearchResults.Count == 0
                    ? "没有找到匹配的消息。"
                    : outcome.HasMore
                        ? $"已显示 {messageSearchResults.Count} 条结果；还有更多结果，请缩小关键词范围。"
                        : $"找到 {messageSearchResults.Count} 条结果。");
        }
        else
        {
            messageSearchResults = Array.Empty<ClientSearchResultPresentation>();
            MessageSearchResultList.ItemsSource = null;
            SetLiveText(MessageSearchStatusText, DescribeMessageSearchOutcome(outcome));
        }

        UpdateMessageSearchState();
    }

    private async void OnSearchResultClicked(object sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not FrameworkElement { DataContext: ClientSearchResultPresentation item } ||
            !messageSearchResults.Any(candidate => ReferenceEquals(candidate, item)))
        {
            return;
        }

        var coordinator = accountShell;
        if (coordinator is null)
        {
            return;
        }

        CancelMessageSearchOperation(searchNavigationCancellationSource);
        searchNavigationCancellationSource = new CancellationTokenSource();
        var cancellationSource = searchNavigationCancellationSource;
        var version = ++searchNavigationVersion;
        ClearSearchHighlight();
        searchHighlightLease = new SearchHighlightLease(
            item.Result.ConversationId,
            item.Result.MessageId,
            version);
        searchNavigationRunning = true;
        SetLiveText(
            MessageSearchStatusText,
            "正在向服务端重新确认访问权限并读取消息上下文…");
        UpdateMessageSearchState();

        ClientSearchNavigationOutcome outcome;
        try
        {
            outcome = await coordinator.NavigateSearchResultAsync(
                item.Result,
                cancellationSource.Token);
        }
        catch (OperationCanceledException)
        {
            outcome = ClientSearchNavigationOutcome.Failure(
                ClientSearchNavigationStatus.Canceled);
        }
        catch (ObjectDisposedException)
        {
            outcome = ClientSearchNavigationOutcome.Failure(
                ClientSearchNavigationStatus.Stale);
        }
        finally
        {
            cancellationSource.Dispose();
        }

        if (version != searchNavigationVersion ||
            !ReferenceEquals(accountShell, coordinator) ||
            !ReferenceEquals(searchNavigationCancellationSource, cancellationSource) ||
            searchHighlightLease is not { NavigationVersion: var leaseVersion } ||
            leaseVersion != version)
        {
            if (ReferenceEquals(searchNavigationCancellationSource, cancellationSource))
            {
                searchNavigationCancellationSource = null;
                searchNavigationRunning = false;
                ClearSearchHighlight();
                UpdateMessageSearchState();
            }

            return;
        }

        searchNavigationCancellationSource = null;
        searchNavigationRunning = false;
        if (outcome.Status == ClientSearchNavigationStatus.Completed)
        {
            messageSearchResults = Array.Empty<ClientSearchResultPresentation>();
            MessageSearchResultList.ItemsSource = null;
            SelectSearchConversation(item.Result.ConversationId);
            CloseSearchPanelAfterNavigation();
            SetLiveText(
                NavigationNoticeText,
                "访问权限已重新确认；正在定位并短暂高亮目标消息。");
            ScheduleSearchHighlightMaterialization();
        }
        else
        {
            ClearSearchHighlight();
            SetLiveText(
                MessageSearchStatusText,
                DescribeSearchNavigationOutcome(outcome.Status));
        }

        UpdateMessageSearchState();
    }

    private void SelectSearchConversation(Guid conversationId)
    {
        _ = SelectConversationForAuthoritativeNavigation(conversationId);
    }

    private ClientConversationListItemPresentation? SelectConversationForAuthoritativeNavigation(
        Guid conversationId)
    {
        pendingConversationSelectionId = conversationId;
        if (!string.IsNullOrWhiteSpace(ConversationSearchTextBox.Text))
        {
            ConversationSearchTextBox.Clear();
        }

        SetConversationFilter(ClientConversationFilter.All);
        ApplicationNavigation.SelectedSection = ClientNavigationSection.Chat;
        var selected = conversationItems.FirstOrDefault(item => item.Id == conversationId);
        if (selected is null)
        {
            return null;
        }

        suppressSelectionRequest = true;
        try
        {
            ConversationList.SelectedItem = selected;
            ConversationList.ScrollIntoView(selected);
            selectedConversationId = selected.Id;
            pendingConversationSelectionId = null;
            ApplySelectedConversation(selected);
        }
        finally
        {
            suppressSelectionRequest = false;
        }

        return selected;
    }

    private void OnSearchResultsInvalidated()
    {
        if (!Dispatcher.CheckAccess())
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            {
                return;
            }

            try
            {
                Dispatcher.Invoke(
                    ApplySearchResultsInvalidated,
                    DispatcherPriority.Send);
            }
            catch (InvalidOperationException) when (
                Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            {
            }

            return;
        }

        ApplySearchResultsInvalidated();
    }

    internal void ApplySearchResultsInvalidated()
    {
        messageSearchResults = Array.Empty<ClientSearchResultPresentation>();
        MessageSearchResultList.ItemsSource = null;
        ++searchNavigationVersion;
        searchNavigationCancellationSource = null;
        searchNavigationRunning = false;
        ClearSearchHighlight();
        ++messageSearchVersion;
        messageSearchCancellationSource = null;
        messageSearchRunning = false;

        if (MessageSearchResultList.IsKeyboardFocusWithin)
        {
            MessageSearchTextBox.Focus();
        }

        if (SearchPanel.Visibility == Visibility.Visible)
        {
            SetLiveText(
                MessageSearchStatusText,
                "搜索结果已因账户、会话或消息状态变化而清除。");
        }

        UpdateMessageSearchState();
    }

    private void InvalidateMessageSearchFromUi(bool closePanel, bool clearKeyword)
    {
        ClearMessageSearchPresentation(closePanel, clearKeyword);
        try
        {
            accountShell?.InvalidateSearchResults();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void ClearMessageSearchPresentation(bool closePanel, bool clearKeyword)
    {
        ++messageSearchVersion;
        ++searchNavigationVersion;
        CancelMessageSearchOperation(messageSearchCancellationSource);
        CancelMessageSearchOperation(searchNavigationCancellationSource);
        messageSearchCancellationSource = null;
        searchNavigationCancellationSource = null;
        messageSearchRunning = false;
        searchNavigationRunning = false;
        messageSearchResults = Array.Empty<ClientSearchResultPresentation>();
        MessageSearchResultList.ItemsSource = null;
        ClearSearchHighlight();
        if (clearKeyword)
        {
            suppressMessageSearchInputChanges = true;
            try
            {
                MessageSearchTextBox.Clear();
            }
            finally
            {
                suppressMessageSearchInputChanges = false;
            }
        }

        if (closePanel)
        {
            SearchPanel.Visibility = Visibility.Collapsed;
            SetSearchModalState(isOpen: false);
            searchOverlayRestoreFocus = null;
        }

        SetLiveText(MessageSearchStatusText, "输入关键词并选择搜索范围。");
        UpdateMessageSearchState();
    }

    private void UpdateMessageSearchState()
    {
        var scope = (MessageSearchScopeComboBox.SelectedItem as ClientSearchScopeOption)?.Scope ??
            ClientSearchScope.Global;
        var hasValidKeyword = ClientSearchPolicy.TryNormalizeKeyword(
            MessageSearchTextBox.Text,
            out _);
        var hasCurrentConversation = displayedMessageSnapshot?.Status ==
                ClientMessageListStatus.Ready &&
            displayedMessageSnapshot.ConversationId == SelectedConversationId;
        MessageSearchTextBox.IsEnabled = !searchNavigationRunning;
        MessageSearchScopeComboBox.IsEnabled = !searchNavigationRunning;
        RunSearchButton.IsEnabled = accountShell is not null &&
            !messageSearchRunning &&
            !searchNavigationRunning &&
            hasValidKeyword &&
            (scope == ClientSearchScope.Global || hasCurrentConversation);
        CloseSearchButton.IsEnabled = true;
    }

    private static string DescribeUpdateFailure(ClientUpdateFailure failure) => failure switch
    {
        ClientUpdateFailure.None => string.Empty,
        ClientUpdateFailure.Canceled => "下载已取消。请重新检查或再次下载更新。",
        ClientUpdateFailure.CurrentVersionInvalid => "当前客户端版本无效，无法安全更新。",
        ClientUpdateFailure.ManifestUnavailable => "暂时无法检查更新，请确认服务器可访问后重试。",
        ClientUpdateFailure.ManifestInvalid => "服务器返回的更新信息无效，已拒绝继续。",
        ClientUpdateFailure.DownloadFailed => "更新包下载或校验失败，当前客户端未被修改。请重试。",
        ClientUpdateFailure.NoUpdateAvailable => "当前没有可下载的更新。",
        _ => "更新失败，请重试。",
    };

    private static string DescribeMessageSearchOutcome(ClientSearchOutcome outcome) =>
        outcome.Status switch
        {
            ClientSearchStatus.ValidationFailed =>
                "关键词或搜索范围无效，请检查后重试。",
            ClientSearchStatus.AuthenticationRequired => "登录已失效，请重新登录。",
            ClientSearchStatus.AccessRevoked => "会话访问已撤销，相关本地内容已隐藏。",
            ClientSearchStatus.AccessDenied => "当前账户无权搜索该会话。",
            ClientSearchStatus.RateLimited => outcome.RetryAfterSeconds is { } seconds
                ? $"搜索过于频繁，请约 {seconds} 秒后重试。"
                : "搜索过于频繁，请稍后重试。",
            ClientSearchStatus.Timeout or ClientSearchStatus.TransientFailure =>
                "网络暂时不可用，请稍后重试。",
            ClientSearchStatus.ProtocolError => "搜索响应不符合协议，已拒绝显示。",
            ClientSearchStatus.Canceled => "搜索已取消。",
            ClientSearchStatus.Stale => "账户或会话已变化，旧搜索结果已丢弃。",
            ClientSearchStatus.Unavailable => "当前没有可用的搜索上下文。",
            _ => "搜索失败，请稍后重试。",
        };

    private static string DescribeSearchNavigationOutcome(
        ClientSearchNavigationStatus status) =>
        status switch
        {
            ClientSearchNavigationStatus.AuthenticationRequired => "登录已失效，请重新登录。",
            ClientSearchNavigationStatus.AccessRevoked =>
                "会话访问已撤销，未打开本地缓存内容。",
            ClientSearchNavigationStatus.AccessDenied =>
                "服务端拒绝访问该消息，未打开本地缓存内容。",
            ClientSearchNavigationStatus.TransientFailure =>
                "网络暂时不可用，未打开本地缓存内容。",
            ClientSearchNavigationStatus.ProtocolError =>
                "消息上下文响应无效，未打开本地缓存内容。",
            ClientSearchNavigationStatus.Canceled => "定位已取消。",
            ClientSearchNavigationStatus.Stale => "结果已过期，请重新搜索。",
            ClientSearchNavigationStatus.Unavailable => "该结果已不可用，请重新搜索。",
            _ => "无法定位该消息，请稍后重试。",
        };

    private static void CancelMessageSearchOperation(CancellationTokenSource? cancellationSource)
    {
        if (cancellationSource is null)
        {
            return;
        }

        try
        {
            cancellationSource.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void OnMessageCardLoaded(object sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Border card && IsSearchTargetCard(card))
        {
            ScheduleSearchHighlightMaterialization();
        }
    }

    private void OnMessageCardUnloaded(object sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is Border card &&
            ReferenceEquals(searchHighlightLease?.HighlightedCard, card))
        {
            ClearSearchHighlight();
        }
    }

    private void OnMessageCardDataContextChanged(
        object sender,
        DependencyPropertyChangedEventArgs e)
    {
        if (sender is not Border card)
        {
            return;
        }

        if (ReferenceEquals(searchHighlightLease?.HighlightedCard, card) &&
            !IsSearchTargetMessage(e.NewValue))
        {
            ClearSearchHighlight();
            return;
        }

        if (IsSearchTargetMessage(e.NewValue))
        {
            ScheduleSearchHighlightMaterialization();
        }
    }

    private bool IsSearchHighlightTarget(ClientMessageListSnapshot snapshot) =>
        searchHighlightLease is { } lease &&
        !lease.IsMaterialized &&
        snapshot.Status == ClientMessageListStatus.Ready &&
        snapshot.ConversationId == lease.ConversationId &&
        snapshot.TargetMessageId == lease.MessageId;

    private bool IsSearchTargetCard(Border card) =>
        string.Equals(card.Tag as string, "MessageCard", StringComparison.Ordinal) &&
        IsSearchTargetMessage(card.DataContext);

    private bool IsSearchTargetMessage(object? value) =>
        searchHighlightLease is { } lease &&
        value is ClientMessageListItemPresentation item &&
        item.ServerMessageId == lease.MessageId;

    private bool TryMaterializeSearchHighlight(ClientMessageListSnapshot snapshot)
    {
        var lease = searchHighlightLease;
        if (lease is null || lease.IsMaterialized ||
            snapshot.Status != ClientMessageListStatus.Ready ||
            snapshot.ConversationId != lease.ConversationId ||
            snapshot.TargetMessageId != lease.MessageId)
        {
            return false;
        }

        var targetItem = snapshot.Messages.FirstOrDefault(
            item => item.ServerMessageId == lease.MessageId);
        if (targetItem is null)
        {
            return false;
        }

        MessageList.ScrollIntoView(targetItem);
        MessageList.UpdateLayout();
        if (MessageList.ItemContainerGenerator.ContainerFromItem(targetItem) is not
                ListBoxItem container)
        {
            return false;
        }

        var card = FindVisualDescendants<Border>(container)
            .FirstOrDefault(candidate =>
                string.Equals(candidate.Tag as string, "MessageCard", StringComparison.Ordinal) &&
                candidate.DataContext is ClientMessageListItemPresentation presentation &&
                presentation.ServerMessageId == lease.MessageId);
        if (card is null || !IsActuallyVisibleWithin(card, MessageList))
        {
            return false;
        }

        card.Background = SearchHighlightBackground;
        card.BorderBrush = SearchHighlightBorder;
        card.BorderThickness = new Thickness(2);
        lease.HighlightedCard = card;
        lease.IsMaterialized = true;
        lease.MaterializationScheduled = false;
        StartSearchHighlightTimer();
        SetLiveText(
            NavigationNoticeText,
            "已定位目标消息；高亮将在约 2 秒后自动消失。");
        return true;
    }

    private void ScheduleSearchHighlightMaterialization()
    {
        var lease = searchHighlightLease;
        var snapshot = displayedMessageSnapshot;
        if (lease is null || lease.IsMaterialized || lease.MaterializationScheduled ||
            snapshot is null || snapshot.Status != ClientMessageListStatus.Ready ||
            snapshot.ConversationId != lease.ConversationId ||
            snapshot.TargetMessageId != lease.MessageId)
        {
            return;
        }

        lease.MaterializationScheduled = true;
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            () => TryCompleteScheduledSearchHighlight(lease.NavigationVersion));
    }

    private void TryCompleteScheduledSearchHighlight(long navigationVersion)
    {
        var lease = searchHighlightLease;
        if (lease is null || lease.NavigationVersion != navigationVersion ||
            lease.IsMaterialized)
        {
            return;
        }

        lease.MaterializationScheduled = false;
        lease.MaterializationAttempts++;
        var snapshot = displayedMessageSnapshot;
        if (snapshot is not null && TryMaterializeSearchHighlight(snapshot))
        {
            AcknowledgeMaterializedSearchTarget(snapshot, lease);
            return;
        }

        if (lease.MaterializationAttempts < MaximumSearchHighlightMaterializationAttempts)
        {
            ScheduleSearchHighlightMaterialization();
            return;
        }

        ClearSearchHighlight();
        SetLiveText(
            NavigationNoticeText,
            "已打开会话，但目标消息未能在可见窗口中定位；未推进该目标的已读位置。");
    }

    private void AcknowledgeMaterializedSearchTarget(
        ClientMessageListSnapshot snapshot,
        SearchHighlightLease lease)
    {
        if (lease.TargetAcknowledged ||
            !ReferenceEquals(searchHighlightLease, lease) ||
            snapshot.Status != ClientMessageListStatus.Ready ||
            snapshot.ConversationId != lease.ConversationId ||
            snapshot.TargetMessageId != lease.MessageId)
        {
            return;
        }

        lease.TargetAcknowledged = true;
        accountShell?.AcknowledgeMessageSnapshotApplied(
            lease.ConversationId,
            snapshot.Revision,
            lease.MessageId,
            IsNearBottom(FindVisualChild<ScrollViewer>(MessageList)));
    }

    private void StartSearchHighlightTimer()
    {
        searchHighlightTimer?.Stop();
        if (searchHighlightTimer is not null)
        {
            searchHighlightTimer.Tick -= OnSearchHighlightTimerTick;
        }

        searchHighlightTimer = new DispatcherTimer(
            DispatcherPriority.Background,
            Dispatcher)
        {
            Interval = SearchHighlightDuration,
        };
        searchHighlightTimer.Tick += OnSearchHighlightTimerTick;
        searchHighlightTimer.Start();
    }

    private void OnSearchHighlightTimerTick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        ClearSearchHighlight();
    }

    private void ClearSearchHighlight()
    {
        if (searchHighlightTimer is not null)
        {
            searchHighlightTimer.Stop();
            searchHighlightTimer.Tick -= OnSearchHighlightTimerTick;
            searchHighlightTimer = null;
        }

        if (searchHighlightLease?.HighlightedCard is { } card)
        {
            card.Background = MessageCardBackground;
            card.BorderBrush = MessageCardBorder;
            card.BorderThickness = new Thickness(0);
        }

        searchHighlightLease = null;
    }

    private static bool IsActuallyVisibleWithin(FrameworkElement element, FrameworkElement host)
    {
        if (!element.IsVisible || !host.IsVisible ||
            element.ActualWidth <= 0 || element.ActualHeight <= 0 ||
            host.ActualWidth <= 0 || host.ActualHeight <= 0)
        {
            return false;
        }

        try
        {
            var bounds = element
                .TransformToAncestor(host)
                .TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            return bounds.IntersectsWith(new Rect(0, 0, host.ActualWidth, host.ActualHeight));
        }
        catch (InvalidOperationException)
        {
            return false;
        }
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
        var serverAddress = ServerAddressTextBox.Text;
        var userName = UserNameTextBox.Text;
        PasswordInput.Clear();
        try
        {
            if (mandatoryUpdateGate ||
                !await loginPreflightAttemptGate.RunAsync(
                    serverAddress,
                    loginUpdatePreflight,
                    address => coordinator.LoginAsync(
                        address,
                        userName,
                        password)))
            {
                return;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async void OnCheckForUpdatesClicked(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (checkForUpdates is not null)
        {
            await checkForUpdates();
        }
    }

    private async void OnRetryMandatoryUpdateClicked(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (checkForUpdates is not null)
        {
            await checkForUpdates();
        }
    }

    private async void OnDownloadUpdateClicked(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (downloadUpdate is not null)
        {
            await downloadUpdate();
        }
    }

    private void OnCancelUpdateDownloadClicked(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        cancelUpdateDownload?.Invoke();
    }

    private async void OnApplyUpdateClicked(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (applyUpdate is not null)
        {
            await applyUpdate();
        }
    }

    private async void OnOptionalUpdateActionClicked(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (optionalUpdateActionCancels)
        {
            cancelUpdateDownload?.Invoke();
        }
        else if (optionalUpdateActionApplies)
        {
            if (applyUpdate is not null)
            {
                await applyUpdate();
            }
        }
        else if (downloadUpdate is not null)
        {
            await downloadUpdate();
        }
    }

    private void OnExitForMandatoryUpdateClicked(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        requestExplicitExit?.Invoke();
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
        if (suppressSelectionRequest)
        {
            return;
        }

        var selected = ConversationList.SelectedItem as
            ClientConversationListItemPresentation;
        selectedConversationId = selected?.Id;
        if (searchNavigationRunning)
        {
            ++searchNavigationVersion;
            CancelMessageSearchOperation(searchNavigationCancellationSource);
            searchNavigationCancellationSource = null;
            searchNavigationRunning = false;
            ClearSearchHighlight();
            SetLiveText(MessageSearchStatusText, "会话已切换，本次定位已取消。");
        }

        if (searchHighlightLease is { } lease &&
            selected?.Id != lease.ConversationId)
        {
            ClearSearchHighlight();
        }

        ApplySelectedConversation(selected);
        accountShell?.SelectConversation(selected?.Id);

        UpdateMessageSearchState();
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

    private void OnComposerResizeDragDelta(object sender, DragDeltaEventArgs e)
    {
        _ = sender;

        var currentHeight = double.IsNaN(MessageComposerTextBox.Height)
            ? Math.Max(ComposerInputMinimumHeight, MessageComposerTextBox.ActualHeight)
            : MessageComposerTextBox.Height;
        var availableExpansion = Math.Max(
            0,
            MessageList.ActualHeight - ComposerMessageListMinimumHeight);
        var maximumHeight = Math.Min(
            ComposerInputMaximumHeight,
            currentHeight + availableExpansion);
        var desiredHeight = Math.Clamp(
            currentHeight - e.VerticalChange,
            ComposerInputMinimumHeight,
            maximumHeight);

        MessageComposerTextBox.Height = desiredHeight;
        e.Handled = true;
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

    private async void OnMentionPickerClicked(object sender, RoutedEventArgs e)
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
            await SearchMentionCandidatesAsync(debounce: false);
        }
        else
        {
            CancelMentionSearch();
        }

        UpdateComposerState();
    }

    private void OnCloseMentionPickerClicked(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        CancelMentionSearch();
        MentionPickerPanel.Visibility = Visibility.Collapsed;
        MessageComposerTextBox.Focus();
    }

    private async void OnMentionSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        UpdateComposerState();
        if (!suppressMentionSearchInputChanges &&
            MentionPickerPanel.Visibility == Visibility.Visible)
        {
            await SearchMentionCandidatesAsync(debounce: true);
        }
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
        await SearchMentionCandidatesAsync(debounce: false);
    }

    private async void OnMentionSearchClicked(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        await SearchMentionCandidatesAsync(debounce: false);
    }

    private async Task SearchMentionCandidatesAsync(bool debounce)
    {
        var query = MentionSearchTextBox.Text;
        var conversationId = composerContextConversationId;
        if (accountShell is null ||
            !composerAvailable ||
            !conversationId.HasValue ||
            !ClientMentionPolicy.IsValidQuery(query))
        {
            SetLiveText(
                MentionSearchStatusText,
                "请输入 0–64 位 ASCII 字母、数字、点、下划线或连字符前缀。");
            UpdateComposerState();
            return;
        }

        mentionSearchCancellationSource?.Cancel();
        using var cancellationSource = new CancellationTokenSource();
        mentionSearchCancellationSource = cancellationSource;
        var searchVersion = ++mentionSearchVersion;
        mentionSearchRunning = true;
        MentionCandidateList.ItemsSource = null;
        SetLiveText(
            MentionSearchStatusText,
            query.Length == 0
                ? "正在加载当前会话全部成员…"
                : "正在自动筛选当前会话成员…");
        UpdateComposerState();
        try
        {
            if (debounce)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(120), cancellationSource.Token);
            }

            var outcome = await accountShell.SearchMentionCandidatesAsync(
                query,
                cancellationToken: cancellationSource.Token);
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
                        ? "当前会话没有匹配成员。"
                        : outcome.HasMore
                            ? $"显示前 {outcome.Candidates.Count} 个候选，请继续缩小前缀。"
                            : query.Length == 0
                                ? $"已列出 {outcome.Candidates.Count} 个成员。"
                                : $"找到 {outcome.Candidates.Count} 个成员。");
            }
            else
            {
                MentionCandidateList.ItemsSource = null;
                SetLiveText(MentionSearchStatusText, DescribeMentionSearchOutcome(outcome));
            }
        }
        catch (OperationCanceledException)
        {
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
                if (ReferenceEquals(mentionSearchCancellationSource, cancellationSource))
                {
                    mentionSearchCancellationSource = null;
                }

                UpdateComposerState();
            }
        }
    }

    private void CancelMentionSearch()
    {
        mentionSearchVersion++;
        mentionSearchRunning = false;
        var cancellationSource = mentionSearchCancellationSource;
        mentionSearchCancellationSource = null;
        cancellationSource?.Cancel();
        UpdateComposerState();
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

    private async void OnAttachmentOpenClicked(object sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not System.Windows.Controls.Button
            {
                DataContext: ClientMessageAttachmentPresentation
                {
                    DownloadState: { } state,
                },
            } ||
            !state.CanOpen)
        {
            return;
        }

        await OpenAttachmentAsync(state);
    }

    private async void OnAttachmentThumbnailLoaded(object sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not System.Windows.Controls.Image
            {
                DataContext: ClientMessageAttachmentPresentation
                {
                    DownloadState: { } downloadState,
                    ImageState: { } state,
                } attachment,
            })
        {
            return;
        }

        await StartAttachmentPreviewAsync(attachment, downloadState, state);
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
                DownloadState: { } downloadState,
                ImageState: { } newState,
            } attachment)
        {
            _ = StartAttachmentPreviewAsync(attachment, downloadState, newState);
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
        CloseSearchPanelForOverlayTransition();
        CloseChannelPanel(clearPresentation: false);
        CloseSettingsOverlay(restoreFocus: false);
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
        if (mandatoryUpdateGate && !MandatoryUpdateOverlay.IsKeyboardFocusWithin)
        {
            e.Handled = true;
            FocusMandatoryUpdateAction();
            return;
        }

        if (e.SystemKey == Key.Space &&
            (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt)
        {
            ShowWindowSystemMenu();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.K &&
            (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control &&
            AccountPanel.Visibility == Visibility.Visible)
        {
            FocusConversationSearch();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && ChannelOverlay.Visibility == Visibility.Visible)
        {
            var restoreTarget = channelOverlayRestoreFocus;
            CloseChannelPanel(clearPresentation: false);
            RestoreOverlayFocus(restoreTarget, OpenChannelPanelButton);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && SettingsOverlay.Visibility == Visibility.Visible)
        {
            OnCloseSettingsClicked(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && SearchPanel.Visibility == Visibility.Visible)
        {
            var restoreTarget = searchOverlayRestoreFocus;
            InvalidateMessageSearchFromUi(closePanel: true, clearKeyword: true);
            RestoreOverlayFocus(restoreTarget, ConversationList);
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Escape || AttachmentImageViewerOverlay.Visibility != Visibility.Visible)
        {
            return;
        }

        CloseAttachmentImageViewer(restoreFocus: true);
        e.Handled = true;
    }

    internal void FocusConversationSearch()
    {
        _ = Keyboard.Focus(ConversationSearchTextBox);
        ConversationSearchTextBox.SelectAll();
    }

    private void FocusMandatoryUpdateAction()
    {
        if (!mandatoryUpdateGate || MandatoryUpdateOverlay.Visibility != Visibility.Visible)
        {
            return;
        }

        System.Windows.Controls.Button target =
            ApplyMandatoryUpdateButton.IsVisible && ApplyMandatoryUpdateButton.IsEnabled
            ? ApplyMandatoryUpdateButton
            : CancelMandatoryUpdateButton.IsVisible && CancelMandatoryUpdateButton.IsEnabled
                ? CancelMandatoryUpdateButton
                : DownloadMandatoryUpdateButton.IsVisible && DownloadMandatoryUpdateButton.IsEnabled
                    ? DownloadMandatoryUpdateButton
                    : RetryMandatoryUpdateButton.IsEnabled
                        ? RetryMandatoryUpdateButton
                        : ExitMandatoryUpdateButton;
        _ = Keyboard.Focus(target);
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

    private async Task StartAttachmentPreviewAsync(
        ClientMessageAttachmentPresentation attachment,
        ClientAttachmentDownloadViewState downloadState,
        ClientAttachmentImageViewState imageState)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        ArgumentNullException.ThrowIfNull(downloadState);
        ArgumentNullException.ThrowIfNull(imageState);

        if (!attachment.IsImage || !imageState.IsEligible)
        {
            return;
        }

        if (downloadState.Phase != ClientAttachmentDownloadPhase.Downloaded)
        {
            await StartAttachmentDownloadAsync(attachment, downloadState);
            if (downloadState.Phase != ClientAttachmentDownloadPhase.Downloaded)
            {
                return;
            }
        }

        await StartAttachmentThumbnailAsync(imageState);
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

    private async Task OpenAttachmentAsync(ClientAttachmentDownloadViewState state)
    {
        var shell = accountShell;
        var key = ClientAttachmentViewKey.From(state.Context);
        if (shell is null || !state.CanOpen || !TryResolveCurrentAttachment(state, out _, out _))
        {
            return;
        }

        var ownerWindow = new WindowInteropHelper(this).Handle;
        if (ownerWindow == IntPtr.Zero)
        {
            SetLiveText(MessageComposerStatusText, "无法使用当前窗口安全打开附件。");
            return;
        }

        var operation = new ClientAttachmentOpenOperation(
            state.Context,
            state,
            new CancellationTokenSource());
        if (!attachmentOpenOperations.TryAdd(key, operation))
        {
            operation.Dispose();
            return;
        }

        SetLiveText(MessageComposerStatusText, "正在验证附件并交给 Windows 安全打开…");
        try
        {
            var outcome = await shell.OpenAttachmentAsync(
                state.Context.AttachmentId,
                ownerWindow,
                operation.Cancellation.Token);
            if (!IsCurrentAttachmentOpenOperation(key, state, operation))
            {
                return;
            }

            if (outcome.Status is ClientAttachmentOpenStatus.NotDownloaded or
                ClientAttachmentOpenStatus.AttachmentUnavailable or
                ClientAttachmentOpenStatus.ValidationFailed)
            {
                MarkAttachmentNoLongerDownloaded(state.Context);
            }

            SetLiveText(
                MessageComposerStatusText,
                DescribeAttachmentOpenOutcome(outcome.Status));
        }
        catch (OperationCanceledException)
        {
            if (IsCurrentAttachmentOpenOperation(key, state, operation))
            {
                SetLiveText(MessageComposerStatusText, "附件打开已取消。");
            }
        }
        catch (ObjectDisposedException)
        {
            if (IsCurrentAttachmentOpenOperation(key, state, operation))
            {
                SetLiveText(MessageComposerStatusText, "账户已结束，无法打开附件。");
            }
        }
        catch (Exception exception) when (!IsCriticalException(exception))
        {
            Debug.WriteLine($"Attachment open presentation failed: {exception.GetType().Name}.");
            if (IsCurrentAttachmentOpenOperation(key, state, operation))
            {
                SetLiveText(MessageComposerStatusText, "无法安全打开附件，请稍后重试。");
            }
        }
        finally
        {
            CompleteAttachmentOpenOperation(key, operation);
        }
    }

    private static string DescribeAttachmentOpenOutcome(ClientAttachmentOpenStatus status) =>
        status switch
        {
            ClientAttachmentOpenStatus.HandedToWindows => "已交给 Windows 打开。",
            ClientAttachmentOpenStatus.InProgress => "附件正在准备打开，请稍后重试。",
            ClientAttachmentOpenStatus.NotDownloaded => "附件尚未下载，请重新下载。",
            ClientAttachmentOpenStatus.AttachmentUnavailable => "附件不可用或已被移除。",
            ClientAttachmentOpenStatus.AccessRevoked => "已失去此会话的访问权限。",
            ClientAttachmentOpenStatus.Stale => "附件上下文已变化，请重试。",
            ClientAttachmentOpenStatus.ValidationFailed =>
                "本地附件未通过完整性校验，请重新下载。",
            ClientAttachmentOpenStatus.InvalidFileName => "附件名称不满足安全打开要求。",
            ClientAttachmentOpenStatus.StoreFull => "安全打开空间不足，请稍后重试。",
            ClientAttachmentOpenStatus.PolicyRejected => "Windows 安全策略阻止了打开。",
            ClientAttachmentOpenStatus.UserCanceled => "已取消 Windows 打开操作。",
            ClientAttachmentOpenStatus.NoAssociation => "Windows 未找到可用的关联应用。",
            ClientAttachmentOpenStatus.LocalFailure => "无法安全打开附件，请稍后重试。",
            _ => "附件打开已取消。",
        };

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
                if (!pendingDownloaded)
                {
                    CancelAttachmentOpenForNoLongerDownloaded(state);
                }
            }

            if (outcome.Status is not (ClientAttachmentDownloadStatus.Completed or
                    ClientAttachmentDownloadStatus.AlreadyDownloaded))
            {
                SynchronizeAttachmentImageEligibility(
                    entry,
                    currentAttachment.IsImage);
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

        SetChatHeaderNotice(item.IsReplyTargetAvailable
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
            if (ClientTextComposerContextPolicy.ShouldClearCommittedDraft(
                    outcome.PendingCommitted,
                    submittedConversationId,
                    displayedMessageSnapshot?.ConversationId,
                    submittedContent,
                    MessageComposerTextBox.Text,
                    replyContextUnchanged,
                    mentionContextUnchanged))
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
            if (searchHighlightLease is { IsMaterialized: false } lease &&
                snapshot.ConversationId == lease.ConversationId &&
                snapshot.TargetMessageId == lease.MessageId)
            {
                return;
            }

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

        ClearSearchHighlight();
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

        SetChatHeaderHeading(
            selected is null ? "请选择会话" : selected.Name);
        SetChatHeaderNotice(
            selected is null
                ? "选择左侧真实会话以查看消息。"
                : $"已选择{selected.TypeLabel}；正在读取账户隔离的真实消息。");
        if (selected is null)
        {
            channelParticipantCancellationSource?.Cancel();
            channelParticipants = null;
            SetChatHeaderMembersSummary("成员：请选择会话");
            ApplyChannelParticipantPresentation();
        }
        else
        {
            _ = LoadConversationParticipantsAsync(selected.Id);
        }
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
        CancelMentionSearch();
        composerMentions.Clear();
        MentionCandidateList.ItemsSource = null;
        SelectedMentionList.ItemsSource = null;
        suppressMentionSearchInputChanges = true;
        try
        {
            MentionSearchTextBox.Clear();
        }
        finally
        {
            suppressMentionSearchInputChanges = false;
        }
        SetLiveText(SelectedMentionHeadingText, "已选 0/20");
        SetLiveText(
            MentionSearchStatusText,
            "打开后自动列出成员；输入用户名字符会即时筛选。");
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
                attachmentOpenOperations.Count != 0 ||
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
                            attachment.IsImage),
                        attachment.IsDownloaded);
                    attachmentDownloadStates.Add(key, entry);
                }
                else if (entry.PersistedDownloaded != attachment.IsDownloaded ||
                         entry.PendingPersistedDownloaded.HasValue)
                {
                    if (entry.State.SynchronizePersistedDownloaded(
                            attachment.IsDownloaded))
                    {
                        if (!attachment.IsDownloaded)
                        {
                            CancelAttachmentOpenForNoLongerDownloaded(entry.State);
                        }

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
                    attachment.IsImage);

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

            if (attachmentOpenOperations.Remove(removedKey, out var openOperation))
            {
                openOperation.Cancel();
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
        CancelAttachmentOpenForNoLongerDownloaded(entry.State);
        CancelAttachmentThumbnailForRecycle(entry.ImageState);
        var isImage = TryResolveCurrentAttachment(
            entry.ImageState,
            out _,
            out var currentAttachment) && currentAttachment.IsImage;
        SynchronizeAttachmentImageEligibility(entry, isEligible: isImage);
        entry.ImageState.ClearForRecycle();
        SetLiveText(
            MessageComposerStatusText,
            "本地附件状态已失效，请重新下载后再打开或预览。");
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

    private bool IsCurrentAttachmentOpenOperation(
        ClientAttachmentViewKey key,
        ClientAttachmentDownloadViewState state,
        ClientAttachmentOpenOperation operation) =>
        attachmentOpenOperations.TryGetValue(key, out var activeOperation) &&
        ReferenceEquals(activeOperation, operation) &&
        ReferenceEquals(operation.State, state) &&
        ReferenceEquals(operation.Context, state.Context) &&
        state.CanOpen &&
        TryResolveCurrentAttachment(state, out _, out _);

    private void CancelAttachmentOpenForNoLongerDownloaded(
        ClientAttachmentDownloadViewState state)
    {
        var key = ClientAttachmentViewKey.From(state.Context);
        if (attachmentOpenOperations.Remove(key, out var operation))
        {
            operation.Cancel();
        }

        if (attachmentImageViewerOperation is { } viewerOperation &&
            ReferenceEquals(viewerOperation.State.Context, state.Context))
        {
            CloseAttachmentImageViewer(restoreFocus: false);
        }
    }

    private void CompleteAttachmentOpenOperation(
        ClientAttachmentViewKey key,
        ClientAttachmentOpenOperation operation)
    {
        if (attachmentOpenOperations.TryGetValue(key, out var activeOperation) &&
            ReferenceEquals(activeOperation, operation))
        {
            attachmentOpenOperations.Remove(key);
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
        foreach (var operation in attachmentOpenOperations.Values)
        {
            operation.Cancel();
        }

        attachmentOpenOperations.Clear();
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

    private sealed class SearchHighlightLease(
        Guid conversationId,
        long messageId,
        long navigationVersion)
    {
        public Guid ConversationId { get; } = conversationId;

        public long MessageId { get; } = messageId;

        public long NavigationVersion { get; } = navigationVersion;

        public Border? HighlightedCard { get; set; }

        public int MaterializationAttempts { get; set; }

        public bool IsMaterialized { get; set; }

        public bool MaterializationScheduled { get; set; }

        public bool TargetAcknowledged { get; set; }
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

    private sealed class ClientAttachmentOpenOperation(
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

    private static SolidColorBrush CreateFrozenBrush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(
            System.Windows.Media.Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush CreateFrozenBrush(
        byte alpha,
        byte red,
        byte green,
        byte blue)
    {
        var brush = new SolidColorBrush(
            System.Windows.Media.Color.FromArgb(alpha, red, green, blue));
        brush.Freeze();
        return brush;
    }

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

    private sealed record ChannelUserPresentation(
        Guid UserId,
        string UserName,
        string DisplayName,
        string AvatarText,
        string RoleLabel,
        bool CanInvite,
        bool CanRemove);
}
