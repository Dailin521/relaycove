using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Maui;
using RelayCove.App.Services;
using RelayCove.Core;

namespace RelayCove.App.ViewModels;

public sealed partial class ShellViewModel : ObservableObject, IDisposable
{
    private readonly IReadOnlyList<EmojiChoice> _unicodeReactionChoices = EmojiCatalog.CreateChoices();
    private static readonly Brush OnlinePresenceBrush = new SolidColorBrush(Color.FromArgb("#22C55E"));
    private static readonly Brush IdlePresenceBrush = new SolidColorBrush(Color.FromArgb("#F59E0B"));
    private static readonly Brush OfflinePresenceBrush = new SolidColorBrush(Color.FromArgb("#9CA3AF"));
    private const double WideLayoutMinimum = 1121d;
    private const double IntermediateLayoutMaximum = 820d;
    private const double NarrowLayoutMaximum = 720d;
    // The native resize handle and toolbar together consume 64 DIP. Keep a
    // full 64 DIP editor row at the minimum so text never overflows the
    // composer surface when the user drags it down or presses Home.
    private const double DefaultComposerHeight = 128d;
    private const double MinimumComposerHeight = 128d;
    private const double MaximumComposerHeight = 300d;
    private const int MessageItemConversationCacheLimit = 12;
    private const int MessagePresentationCacheLimit = 6;
    private const int RecentDownloadLimit = 20;

    private readonly IClientSession _session;
    private readonly TimeProvider _timeProvider;
    private ITimer? _messageActionTimer;
    private readonly ILastRealmStore _lastRealmStore;
    private readonly IUiDispatcher _dispatcher;
    private readonly IAppearanceService _appearanceService;
    private readonly IUiPreferencesService _uiPreferencesService;
    private readonly IStartupService? _startupService;
    private bool _startWithWindows;
    private readonly INotificationPreferencesService _notificationPreferencesService;
    private readonly IAppNotificationService _appNotificationService;
    private readonly INotificationAvatarFileStore? _notificationAvatarFileStore;
    private readonly IWindowShellAdapter? _windowShellAdapter;
    private readonly IPlatformInteractionService _platformInteractions;
    private readonly IFileSelectionService _fileSelectionService;
    private readonly IRealmMediaService _realmMediaService;
    private readonly IFileSaveService _fileSaveService;
    private readonly IDownloadHistoryStore _downloadHistoryStore;
    private readonly IConversationPreferencesStore _conversationPreferencesStore;
    private CancellationTokenSource? _conversationMenuActionCancellation;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleConversationPinnedCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleConversationMutedCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteConversationCommand))]
    public partial bool IsConversationMenuActionBusy { get; set; }
    private readonly Dictionary<string, string> _drafts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<AttachmentDraftItem>> _attachmentDrafts = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _attachmentUploadGate = new(1, 1);
    private readonly Dictionary<string, CancellationTokenSource> _activeAttachmentUploads = [];
    private readonly Dictionary<string, OutboxEntry> _unsubmittedMessages = new(StringComparer.Ordinal);
    private readonly List<ConversationContactChoice> _allNewConversationChoices = [];
    private readonly Dictionary<long, IReadOnlyList<UserProfile>> _privateGroupMembers = [];
    private readonly HashSet<long> _privateGroupRosterLoadAttempts = [];
    private readonly Dictionary<long, string> _lastSelectedTopicByChannel = [];
    private readonly ResettableObservableCollection<MessageItem> _emptyMessages = [];
    private readonly ResettableObservableCollection<EmojiChoice> _visibleEmojiChoices = [];
    private readonly Dictionary<string, Dictionary<string, MessageItem>> _messageItemsByConversation = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _messageItemConversationLru = [];
    private readonly Dictionary<string, ConversationMessagePresentation> _messagePresentationsByConversation = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _messagePresentationLru = [];
    private readonly object _projectionGate = new();
    private readonly object _autoMarkReadSync = new();
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private CancellationTokenSource _attachmentUploadsCancellation = new();
    private readonly Dictionary<string, long> _autoMarkReadAttemptedThrough = new(StringComparer.Ordinal);
    private ClientState? _pendingProjectionState;
    private bool _projectionDispatchScheduled;
    private IReadOnlyList<SearchResultItem> _serverSearchResults = [];
    private ConversationKey? _searchConversation;
    private IReadOnlyDictionary<long, ChatMessage> _conversationFilterServerMatches =
        new Dictionary<long, ChatMessage>();
    private CancellationTokenSource? _navigationCancellation;
    private CancellationTokenSource? _autoMarkReadCancellation;
    private string? _autoMarkReadAttemptKey;
    private long _autoMarkReadMessageId;
    private long _navigationGeneration;
    private string? _navigationConversationKey;
    private bool _hasAuthoritativeTopics;
    private long? _expandedChannelId;
    private string? _displayedConversationKey;
    private string? _deferredInitialMessageProjectionConversationKey;
    private string? _pendingActivationScrollConversationKey;
    private long _pendingActivationScrollGeneration;
    private MessageScrollReason? _pendingActivationScrollReason;
    private string? _lastActivationScrollConversationKey;
    private long _lastActivationScrollGeneration;
    private long _lastActivationScrollTargetMessageId;
    private string? _retainedActivationConversationKey;
    private long _retainedActivationLatestMessageId;
    private long _messageScrollSequence;
    private (AccountId? AccountId, string ConversationKey, long Generation)? _messageAnchorHistory;
    private CancellationTokenSource? _searchInputCancellation;
    private long _searchInputGeneration;
    private bool _hasSubmittedSearch;
    private long? _searchBeforeMessageId;
    private AccountId? _searchAccountId;
    private CancellationTokenSource? _conversationFilterCancellation;
    private long _conversationFilterGeneration;
    private AccountId? _conversationFilterAccountId;
    private string? _conversationFilterServerQuery;
    private long? _conversationFilterBeforeMessageId;
    private long? _savedBeforeMessageId;
    private CancellationTokenSource? _savedLoadCancellation;
    private CancellationTokenSource? _mediaStatusClearCancellation;
    private MessageAttachmentItem? _failedMediaDownloadAttachment;
    private AccountId? _downloadHistoryAccountId;
    private long _savedLoadGeneration;
    private AccountId? _savedAccountId;
    private AccountId? _avatarUploadAccountId;
    private AccountId? _messageItemCacheAccountId;
    private AccountId? _messagePresentationAccountId;
    private AccountId? _privateGroupRosterAccountId;
    private ConversationMessagePresentation? _activeMessagePresentation;
    private ClientState _projectedState = ClientState.Empty;
    private IReadOnlyList<TopicSummary> _loadedTopics = [];
    private long? _loadedTopicsChannelId;
    private string? _activeDraftKey;
    private double _composerHeight = DefaultComposerHeight;
    private double _persistedComposerHeight = DefaultComposerHeight;
    private double _viewportWidth = 1440d;
    private double _viewportHeight = 900d;
    private long? _channelUnsubscribeTargetId;
    private long _channelBrowserGeneration;
    private AccountId? _channelBrowserAccountId;
    private CancellationTokenSource? _channelBrowserCancellation;
    private CancellationTokenSource? _detailsLoadCancellation;
    private long _detailsLoadGeneration;
    private string? _projectedConversationKey;
    private long? _newestProjectedMessageId;
    private string? _transientUnreadDividerSuppressionConversationKey;
    private long? _transientUnreadDividerSuppressionAfterMessageId;
    private long _lastAutomaticLoadOlderMilliseconds = long.MinValue;
    private int _automaticLoadOlderInFlight;
    private bool _isMessageViewportNearBottom = true;
    private bool _isMessageViewportBeyondJumpThreshold;
    private bool _isWindowActive;
    private bool _autoMarkReadInFlight;
    private bool _autoMarkReadPending;
    private int _initialized;
    private bool _activateInitialConversationWhenAvailable;
    private int _loginInFlight;
    private bool _suppressDraftTracking;
    private bool _suppressUiPreferenceSave = true;
    private bool _suppressNotificationPreferenceSave = true;
    private bool _preserveContinuousPreference;
    private bool _nativePreviewCacheSwitchStarted;
    private double _fontSize = 14d;
    private double _conversationPaneWidth = 310d;
    private bool _disposed;

    public ShellViewModel(
        IClientSession session,
        ILastRealmStore lastRealmStore,
        IUiDispatcher dispatcher,
        IAppearanceService appearanceService,
        IUiPreferencesService uiPreferencesService,
        IPlatformInteractionService platformInteractions,
        IFileSelectionService fileSelectionService,
        IRealmMediaService realmMediaService,
        IFileSaveService fileSaveService,
        IConversationPreferencesStore? conversationPreferencesStore = null,
        INotificationPreferencesService? notificationPreferencesService = null,
        IAppNotificationService? appNotificationService = null,
        IWindowShellAdapter? windowShellAdapter = null,
        INotificationAvatarFileStore? notificationAvatarFileStore = null,
        IDownloadHistoryStore? downloadHistoryStore = null,
        TimeProvider? timeProvider = null,
        IStartupService? startupService = null,
        AppUpdateViewModel? updates = null,
        StickerPickerViewModel? stickers = null)
    {
        Updates = updates;
        Stickers = stickers;
        if (Stickers is not null) Stickers.Sent += OnStickerSent;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _startupService = startupService;
        RefreshStartupSettings();
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _lastRealmStore = lastRealmStore ?? throw new ArgumentNullException(nameof(lastRealmStore));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _appearanceService = appearanceService ?? throw new ArgumentNullException(nameof(appearanceService));
        _uiPreferencesService = uiPreferencesService ?? throw new ArgumentNullException(nameof(uiPreferencesService));
        _platformInteractions = platformInteractions ?? throw new ArgumentNullException(nameof(platformInteractions));
        _fileSelectionService = fileSelectionService ?? throw new ArgumentNullException(nameof(fileSelectionService));
        _realmMediaService = realmMediaService ?? throw new ArgumentNullException(nameof(realmMediaService));
        _fileSaveService = fileSaveService ?? throw new ArgumentNullException(nameof(fileSaveService));
        _downloadHistoryStore = downloadHistoryStore ?? new InMemoryDownloadHistoryStore();
        _conversationPreferencesStore = conversationPreferencesStore ?? new InMemoryConversationPreferencesStore();
        _notificationPreferencesService = notificationPreferencesService ?? new InMemoryNotificationPreferencesService();
        _appNotificationService = appNotificationService ?? new NullAppNotificationService();
        _windowShellAdapter = windowShellAdapter;
        _notificationAvatarFileStore = notificationAvatarFileStore;
        Realm = _lastRealmStore.Get();
        AskWhereToSaveDownloads = _fileSaveService.AskWhereToSave;
        AppearanceMode = _appearanceService.Current;
        ApplyUiPreferences(_uiPreferencesService.Current);
        ApplyNotificationPreferences(_notificationPreferencesService.Current);
        _suppressUiPreferenceSave = false;
        _suppressNotificationPreferenceSave = false;
        _session.StateChanged += OnStateChanged;
        if (_session is IMessageMutationObserver observer) observer.MessageMutationObserved += OnMessageMutationObserved;
        if (_session is IRealtimeMessageObserver realtimeObserver) realtimeObserver.RealtimeMessageReceived += OnRealtimeMessageReceived;
        _appNotificationService.StateChanged += OnAppNotificationStateChanged;
        _appNotificationService.NotificationActivated += OnAppNotificationActivated;
        ChannelSettings = new ChannelSettingsViewModel(_session, _platformInteractions, OpenChannelFromSettingsAsync);
        ChannelSettings.PropertyChanged += OnChannelSettingsPropertyChanged;
        SelectEmojiCategory(EmojiCategories[0]);
        SelectSearchCategory(SearchCategories[0]);
        Project(_session.State);
    }

    public ObservableCollection<ChannelItem> Channels { get; } = [];
    public ObservableCollection<ChannelItem> FilteredChannels { get; } = [];
    public ObservableCollection<TopicItem> Topics { get; } = [];
    public ObservableCollection<NavigationItem> DirectMessages { get; } = [];
    public ObservableCollection<NavigationItem> FilteredDirectMessages { get; } = [];
    public ObservableCollection<ConversationListItem> Conversations { get; } = [];
    public ObservableCollection<ConversationListItem> PrivateGroupConversations { get; } = [];
    public ObservableCollection<ConversationListItem> FilteredConversations { get; } = [];
    public ObservableCollection<ConversationMessagePresentation> MessagePresentations { get; } = [];
    public ObservableCollection<ContactItem> KnownContacts { get; } = [];
    public ObservableCollection<MessageItem> Messages => _activeMessagePresentation?.Messages ?? _emptyMessages;
    public ObservableCollection<SearchResultItem> SearchResults { get; } = [];
    public ObservableCollection<SavedMessageItem> SavedMessages { get; } = [];
    public ObservableCollection<AvailableChannelItem> AvailableChannels { get; } = [];
    public ObservableCollection<AttachmentDraftItem> Attachments { get; } = [];
    public ObservableCollection<DownloadHistoryItem> RecentDownloads { get; } = [];
    public ObservableCollection<ConversationContactChoice> NewConversationChoices { get; } = [];
    public ObservableCollection<ConversationSettingsMemberItem> DetailsMembers { get; } = [];
    public ObservableCollection<ConversationSettingsMemberItem> GroupInviteCandidates { get; } = [];
    public ObservableCollection<ConversationSettingsMemberItem> GroupMemberActionCandidates { get; } = [];
    public ChannelSettingsViewModel ChannelSettings { get; }
    public AppUpdateViewModel? Updates { get; }
    public StickerPickerViewModel? Stickers { get; }
    public double StickerPickerHeight => Math.Min(440d, Math.Max(0d, _viewportHeight - 24d));
    public int StickerPickerColumns => Math.Max(1, (int)((EmojiPickerContentWidth - 16d) / 94d));
    public IReadOnlyList<EmojiChoice> EmojiChoices { get; private set; } = [];
    public IReadOnlyList<EmojiCategoryChoice> EmojiCategories { get; } = [new("custom", "自定义")];
    public double EmojiPickerWidth => Math.Min(420d, Math.Max(0d, _viewportWidth - 24d));
    public double EmojiPickerHeight => Math.Min(300d, Math.Max(0d, _viewportHeight - 24d));
    public double DefaultEmojiPickerHeight => Math.Min(392d, Math.Max(0d, _viewportHeight - 24d));
    public double EmojiPickerContentWidth => Math.Max(0d, EmojiPickerWidth - 14d);
    public IReadOnlyDictionary<string, RealmEmoji> RealmEmojis => _session.State.RealmEmojis;
    // Zero native grid spacing; reserve 26 for the cell, 2 for its margin,
    // and 16 for the vertical scrollbar.
    public int EmojiPickerColumns => Math.Max(1, (int)((EmojiPickerContentWidth - 16d) / 28d));
    public int ComposerEmojiPickerColumns => Math.Max(1, (int)((EmojiPickerContentWidth - 16d) / 34d));
    public IReadOnlyList<SearchCategoryChoice> SearchCategories { get; } =
    [
        new(null, "全部"),
        new(MessageSearchFilter.Messages, "消息"),
        new(MessageSearchFilter.Files, "文件"),
        new(MessageSearchFilter.Images, "图片"),
        new(MessageSearchFilter.Videos, "视频"),
        new(MessageSearchFilter.Links, "链接")
    ];
    public ObservableCollection<EmojiChoice> VisibleEmojiChoices => _visibleEmojiChoices;

    [ObservableProperty]
    public partial string Realm { get; set; } = PreferencesLastRealmStore.DefaultRealm;

    [ObservableProperty]
    public partial string Email { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Password { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ComposerText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? LoginError { get; set; }

    [ObservableProperty]
    public partial string ConnectionStatus { get; set; } = "已注销";

    [ObservableProperty]
    public partial bool IsLoggedIn { get; set; }

    [ObservableProperty]
    public partial bool ClearCacheConfirmationVisible { get; set; }

    [ObservableProperty]
    public partial bool LogoutConfirmationVisible { get; set; }

    [ObservableProperty]
    public partial string SearchQuery { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ConversationFilterQuery { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsConversationFilterBusy { get; set; }

    [ObservableProperty]
    public partial string? ConversationFilterError { get; set; }

    [ObservableProperty]
    public partial bool IsNewConversationOpen { get; set; }

    [ObservableProperty]
    public partial bool IsNewChannelConversationMode { get; set; }

    [ObservableProperty]
    public partial ChannelItem? NewConversationChannel { get; set; }

    [ObservableProperty]
    public partial bool IsNewConversationChannelLocked { get; set; }

    [ObservableProperty]
    public partial string NewConversationTopic { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NewPrivateGroupName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? NewConversationError { get; set; }

    [ObservableProperty]
    public partial bool IsChannelBrowserOpen { get; set; }

    [ObservableProperty]
    public partial bool IsChannelBrowserLoading { get; set; }

    [ObservableProperty]
    public partial string? ChannelBrowserError { get; set; }

    [ObservableProperty]
    public partial bool IsAccountMenuOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AvatarUploadButtonText))]
    [NotifyCanExecuteChangedFor(nameof(UploadAvatarCommand))]
    public partial bool IsAvatarUploadBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAvatarUploadStatus))]
    public partial string? AvatarUploadStatus { get; set; }

    public string AvatarUploadButtonText => IsAvatarUploadBusy ? "上传中…" : "上传头像";
    public bool HasAvatarUploadStatus => !string.IsNullOrWhiteSpace(AvatarUploadStatus);
    public bool CanUploadAvatar => !IsAvatarUploadBusy && _session.AccountId is not null &&
        _projectedState.Connection.Status == RelayCove.Core.ConnectionStatus.Connected;

    private AccountId? _nameEditAccountId;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveOwnNameCommand))]
    public partial bool IsOwnNameEditing { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveOwnNameCommand))]
    public partial string OwnNameDraft { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChangeOwnNameDraft))]
    [NotifyPropertyChangedFor(nameof(OwnNameSaveButtonText))]
    [NotifyCanExecuteChangedFor(nameof(EditOwnNameCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveOwnNameCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelOwnNameEditCommand))]
    public partial bool IsOwnNameSaveBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOwnNameEditStatus))]
    public partial string? OwnNameEditStatus { get; set; }

    public bool HasOwnNameEditStatus => !string.IsNullOrWhiteSpace(OwnNameEditStatus);
    public bool CanChangeOwnNameDraft => !IsOwnNameSaveBusy;
    public string OwnNameSaveButtonText => IsOwnNameSaveBusy ? "保存中…" : "保存";
    public bool CanEditOwnName => !IsOwnNameSaveBusy && _session.AccountId is not null &&
        _projectedState.Connection.Status == RelayCove.Core.ConnectionStatus.Connected;
    public bool CanSaveOwnName => CanEditOwnName && IsOwnNameEditing && _session.AccountId == _nameEditAccountId &&
        !string.IsNullOrWhiteSpace(OwnNameDraft) && OwnNameDraft.Trim() != CurrentUserDisplayName;

    [ObservableProperty]
    public partial string NewConversationQuery { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsSearchOpen { get; set; }

    [ObservableProperty]
    public partial SearchResultItem? SelectedSearchResult { get; set; }

    [ObservableProperty]
    public partial bool IsSearchBusy { get; set; }

    [ObservableProperty]
    public partial string? SearchError { get; set; }

    [ObservableProperty]
    public partial bool IsSavedLoading { get; set; }

    [ObservableProperty]
    public partial string? SavedError { get; set; }

    [ObservableProperty]
    public partial bool SavedRefreshSuggested { get; set; }

    [ObservableProperty]
    public partial bool IsComposerEmojiPickerOpen { get; set; }

    [ObservableProperty]
    public partial double ComposerEmojiAnchorX { get; set; }

    [ObservableProperty]
    public partial double ComposerEmojiAnchorY { get; set; }

    [ObservableProperty]
    public partial bool IsReactionPickerOpen { get; set; }

    [ObservableProperty]
    public partial double ReactionPickerAnchorX { get; set; }

    [ObservableProperty]
    public partial double ReactionPickerAnchorY { get; set; }

    [ObservableProperty]
    public partial MessageItem? ActiveMessageAction { get; set; }

    [ObservableProperty]
    public partial MessageAttachmentItem? ActiveMessageAttachment { get; set; }

    private string? _activeMessageSelection;

    [ObservableProperty]
    public partial bool IsMessageMenuOpen { get; set; }

    [ObservableProperty]
    public partial double MessageMenuAnchorX { get; set; }

    [ObservableProperty]
    public partial double MessageMenuAnchorY { get; set; }

    [ObservableProperty]
    public partial bool IsChannelMenuOpen { get; set; }

    [ObservableProperty]
    public partial ChannelItem? ActiveChannelAction { get; set; }

    [ObservableProperty]
    public partial double ChannelMenuAnchorX { get; set; }

    [ObservableProperty]
    public partial double ChannelMenuAnchorY { get; set; }

    [ObservableProperty]
    public partial int ChannelMenuFocusRequest { get; set; }

    [ObservableProperty]
    public partial bool IsTopicMenuOpen { get; set; }

    [ObservableProperty]
    public partial TopicItem? ActiveTopicAction { get; set; }

    [ObservableProperty]
    public partial double TopicMenuAnchorX { get; set; }

    [ObservableProperty]
    public partial double TopicMenuAnchorY { get; set; }

    [ObservableProperty]
    public partial int TopicMenuFocusRequest { get; set; }

    [ObservableProperty]
    public partial bool IsTopicMoveDialogOpen { get; set; }

    [ObservableProperty]
    public partial ChannelItem? TopicMoveDestinationChannel { get; set; }

    [ObservableProperty]
    public partial string TopicMoveDestinationName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsTopicDeleteConfirmationOpen { get; set; }

    [ObservableProperty]
    public partial bool IsTopicResolutionConfirmationOpen { get; set; }

    [ObservableProperty]
    public partial bool IsTopicActionBusy { get; set; }

    [ObservableProperty]
    public partial string? TopicActionStatus { get; set; }

    [ObservableProperty]
    public partial bool IsEditDialogOpen { get; set; }

    [ObservableProperty]
    public partial bool IsDeleteConfirmationOpen { get; set; }

    [ObservableProperty]
    public partial bool IsChannelUnsubscribeConfirmationOpen { get; set; }

    [ObservableProperty]
    public partial bool IsChannelUnsubscribeBusy { get; set; }

    [ObservableProperty]
    public partial string ChannelUnsubscribeTargetName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? ChannelUnsubscribeError { get; set; }

    [ObservableProperty]
    public partial string EditMessageText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int ComposerCursorPosition { get; set; }

    [ObservableProperty]
    public partial int ComposerSelectionLength { get; set; }

    [ObservableProperty]
    public partial int ComposerFocusRequest { get; set; }

    [ObservableProperty]
    public partial string? AttachmentError { get; set; }

    [ObservableProperty]
    public partial bool IsFileDragActive { get; set; }

    [ObservableProperty]
    public partial MessageAttachmentItem? ActiveImageAttachment { get; set; }

    [ObservableProperty]
    public partial bool IsImageViewerOpen { get; set; }

    [ObservableProperty]
    public partial bool IsMediaActionBusy { get; set; }

    [ObservableProperty]
    public partial string? MediaActionStatus { get; set; }

    [ObservableProperty]
    public partial string? MediaDownloadFileName { get; set; }

    [ObservableProperty]
    public partial double MediaDownloadProgress { get; set; }

    [ObservableProperty]
    public partial bool HasKnownMediaDownloadLength { get; set; }

    [ObservableProperty]
    public partial string? MediaDownloadProgressText { get; set; }

    [ObservableProperty]
    public partial bool AskWhereToSaveDownloads { get; set; }

    [ObservableProperty]
    public partial string? DownloadSettingsStatus { get; set; }

    [ObservableProperty]
    public partial bool CanChangeStartup { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStartupSettingsStatus))]
    public partial string? StartupSettingsStatus { get; set; }

    public bool HasStartupSettingsStatus => !string.IsNullOrEmpty(StartupSettingsStatus);

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (!CanChangeStartup || value == _startWithWindows) return;
            try
            {
                _startupService!.SetEnabled(value);
                RefreshStartupSettings();
            }
            catch
            {
                RefreshStartupSettings();
                StartupSettingsStatus = "无法更改开机启动，请稍后重试。";
            }

            // Notify even after a failed write so the native switch rolls back.
            OnPropertyChanged();
        }
    }

    private void RefreshStartupSettings()
    {
        try
        {
            if (_startupService is null)
            {
                CanChangeStartup = false;
                StartupSettingsStatus = "当前环境不支持开机启动。";
                return;
            }

            var state = _startupService.GetState();
            SetProperty(ref _startWithWindows, state != StartupState.Disabled, nameof(StartWithWindows));
            CanChangeStartup = true;
            StartupSettingsStatus = state switch
            {
                StartupState.DifferentExecutable => "开机启动指向其他位置，如需启动当前版本，请关闭后重新开启。",
                StartupState.DisabledByWindows => "已被 Windows 禁用，请在系统“设置 → 应用 → 启动”中允许 RichChat 启动。",
                StartupState.UnknownWindowsApproval => "无法确认 Windows 是否允许启动，请检查系统“设置 → 应用 → 启动”。",
                _ => null
            };
        }
        catch
        {
            CanChangeStartup = false;
            StartupSettingsStatus = "无法读取开机启动状态，请重新打开通用设置重试。";
        }
    }

    [ObservableProperty]
    public partial bool IsDownloadCenterOpen { get; set; }

    [ObservableProperty]
    public partial bool HasUnseenCompletedDownloads { get; set; }

    [ObservableProperty]
    public partial bool HasUnseenDownloadFailure { get; set; }

    [ObservableProperty]
    public partial string? DownloadCenterStatus { get; set; }

    [ObservableProperty]
    public partial bool IsConversationLoading { get; set; }

    [ObservableProperty]
    public partial bool IsLoadingOlder { get; set; }

    [ObservableProperty]
    public partial string? MessageLoadError { get; set; }

    [ObservableProperty]
    public partial bool HasReachedOldestMessage { get; set; }

    [ObservableProperty]
    public partial int NewMessageCount { get; set; }

    [ObservableProperty]
    public partial MessageScrollRequest? PendingMessageScrollRequest { get; set; }

    [ObservableProperty]
    public partial bool IsNavigationPending { get; set; }

    [ObservableProperty]
    public partial bool IsAuthoritativeEmptyChannel { get; set; }

    [ObservableProperty]
    public partial bool HasConversationActivationError { get; set; }

    [ObservableProperty]
    public partial int MessageActionFocusRequest { get; set; }

    [ObservableProperty]
    public partial EmojiChoice? SelectedComposerEmoji { get; set; }

    [ObservableProperty]
    public partial EmojiChoice? SelectedReactionEmoji { get; set; }

    [ObservableProperty]
    public partial ChannelItem? SelectedChannel { get; set; }

    [ObservableProperty]
    public partial TopicItem? SelectedTopic { get; set; }

    [ObservableProperty]
    public partial NavigationItem? SelectedDirectMessage { get; set; }

    [ObservableProperty]
    public partial ConversationListItem? SelectedConversationItem { get; set; }

    [ObservableProperty]
    public partial ShellSection SelectedSection { get; set; } = ShellSection.Messages;

    [ObservableProperty]
    public partial SettingsCategory SelectedSettingsCategory { get; set; } = SettingsCategory.Appearance;

    [ObservableProperty]
    public partial ShellLayoutMode LayoutMode { get; set; } = ShellLayoutMode.Wide;

    [ObservableProperty]
    public partial bool IsDetailsOpen { get; set; }

    [ObservableProperty]
    public partial bool IsConversationListVisibleOnNarrow { get; set; } = true;

    [ObservableProperty]
    public partial AppAppearanceMode AppearanceMode { get; set; } = AppAppearanceMode.System;

    [ObservableProperty]
    public partial UiDensityMode DensityMode { get; set; } = UiDensityMode.Comfortable;

    [ObservableProperty]
    public partial UiFontScaleMode FontScaleMode { get; set; } = UiFontScaleMode.Default;

    [ObservableProperty]
    public partial UiConversationWidthMode ConversationWidthMode { get; set; } = UiConversationWidthMode.Standard;

    [ObservableProperty]
    public partial bool AreChannelsExpanded { get; set; } = true;

    [ObservableProperty]
    public partial bool AreDirectMessagesExpanded { get; set; } = true;

    [ObservableProperty]
    public partial bool SystemNotificationsEnabled { get; set; } = true;

    [ObservableProperty]
    public partial bool TaskbarFlashEnabled { get; set; } = true;

    [ObservableProperty]
    public partial bool TaskbarBadgeEnabled { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowMessagePreview { get; set; } = true;

    [ObservableProperty]
    public partial bool DoNotDisturb { get; set; }

    [ObservableProperty]
    public partial string? UnavailableFeatureMessage { get; set; }

    [ObservableProperty]
    public partial string ConversationTitle { get; set; } = "选择会话";

    [ObservableProperty]
    public partial string ConversationSubtitle { get; set; } = "从左侧选择会话开始";

    [ObservableProperty]
    public partial string DetailsTitle { get; set; } = "会话详情";

    [ObservableProperty]
    public partial string DetailsBody { get; set; } = "选择会话后显示可靠的会话信息。";

    [ObservableProperty]
    public partial string DetailsKindLabel { get; set; } = "会话";

    [ObservableProperty]
    public partial string DetailsGlyph { get; set; } = "•";

    [ObservableProperty]
    public partial string DetailsIdentifierLabel { get; set; } = "尚未选择会话";

    [ObservableProperty]
    public partial string DetailsStateLabel { get; set; } = "没有可显示的状态";

    [ObservableProperty]
    public partial string DetailsAvailableMessage { get; set; } = "选择会话后显示已经接通的能力。";

    [ObservableProperty]
    public partial string DetailsUnavailableMessage { get; set; } = "成员关系、共同频道与频道管理暂不可用。";

    [ObservableProperty]
    public partial bool ShowChannelDetails { get; set; }

    [ObservableProperty]
    public partial bool ShowDirectMessageSettings { get; set; }

    [ObservableProperty]
    public partial string? DetailsAvatarUrl { get; set; }

    [ObservableProperty]
    public partial string DetailsAvatarInitial { get; set; } = "?";

    [ObservableProperty]
    public partial string DetailsChannelName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DetailsChannelAnnouncement { get; set; } = "暂无群公告";

    [ObservableProperty]
    public partial string DetailsRemark { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsDetailsLoading { get; set; }

    [ObservableProperty]
    public partial bool IsDetailsContentReady { get; set; }

    [ObservableProperty]
    public partial string? DetailsLoadError { get; set; }

    [ObservableProperty]
    public partial bool IsSelectedDirectMessageMuted { get; set; }

    [ObservableProperty]
    public partial bool IsSelectedDirectMessagePinned { get; set; }

    [ObservableProperty]
    public partial long? DetailsPrivateGroupOwnerId { get; set; }

    [ObservableProperty]
    public partial bool IsPrivateGroupAuthorityLoaded { get; set; }

    [ObservableProperty]
    public partial string EditablePrivateGroupName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EditablePrivateGroupAnnouncement { get; set; } = string.Empty;

    [ObservableProperty]
    public partial ConversationSettingsMemberItem? SelectedGroupInviteCandidate { get; set; }

    [ObservableProperty]
    public partial ConversationSettingsMemberItem? SelectedGroupRemoveCandidate { get; set; }

    [ObservableProperty]
    public partial ConversationSettingsMemberItem? SelectedGroupTransferCandidate { get; set; }

    [ObservableProperty]
    public partial bool IsPrivateGroupActionBusy { get; set; }

    [ObservableProperty]
    public partial string? PrivateGroupActionStatus { get; set; }

    [ObservableProperty]
    public partial bool IsGroupRemoveConfirmationVisible { get; set; }

    [ObservableProperty]
    public partial bool IsGroupTransferConfirmationVisible { get; set; }

    [ObservableProperty]
    public partial bool IsGroupDissolveConfirmationVisible { get; set; }

    [ObservableProperty]
    public partial bool ClearConversationCacheConfirmationVisible { get; set; }

    [ObservableProperty]
    public partial bool IsClearConversationCacheBusy { get; set; }

    [ObservableProperty]
    public partial string NavigationUnreadLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasNavigationUnread { get; set; }

    public double ComposerHeight
    {
        get => _composerHeight;
        set
        {
            var normalized = Math.Clamp(value, MinimumComposerHeight, MaximumComposerHeight);
            if (!SetProperty(ref _composerHeight, normalized)) return;
            OnPropertyChanged(nameof(ComposerDisplayHeight));
            if (_suppressUiPreferenceSave) return;
            _persistedComposerHeight = normalized;
            _uiPreferencesService.SaveComposerHeight(normalized);
        }
    }

    public double ComposerDisplayHeight => ComposerHeight + (HasAttachments ? 66d : 0d);

    public bool LoginVisible => !IsLoggedIn;
    public bool MainVisible => IsLoggedIn;
    public bool HasSelectedConversation => IsRelayCoveConversation(_session.SelectedConversation, _projectedState);
    public bool CanOpenConversationSettings => _session.SelectedConversation is ChannelTopic channel &&
            PrivateGroupPolicy.IsEligible(_projectedState.Subscriptions.GetValueOrDefault(channel.ChannelId)) &&
            channel.Topic.Length == 0 ||
        _session.SelectedConversation is DirectMessage { OtherUserIds.Count: <= 1 };
    public bool HasSelectedTopic => SelectedTopic is { } topic &&
        _session.SelectedConversation is ChannelTopic current &&
        string.Equals(topic.CanonicalKey, current.CanonicalKey, StringComparison.Ordinal);
    public bool IsConversationContentVisible =>
        !IsAuthoritativeEmptyChannel &&
        HasSelectedConversation &&
        (IsNavigationPending &&
             _activeMessagePresentation is { } activePresentation &&
             string.Equals(_displayedConversationKey, activePresentation.ConversationKey, StringComparison.Ordinal) ||
         _session.SelectedConversation is { } selected &&
             string.Equals(_displayedConversationKey, selected.CanonicalKey, StringComparison.Ordinal));
    public bool HasMessages => Messages.Count > 0;
    public bool IsMessageListEmpty => !HasMessages;
    public bool IsMessageCollectionVisible => IsConversationContentVisible && HasMessages;
    public bool ShowMessageEmptyState =>
        !HasSelectedConversation && !IsNavigationPending ||
        IsConversationContentVisible && !HasMessages && !IsConversationLoading && !HasConversationActivationError;
    public bool ShowConversationLoadingIndicator => IsConversationLoading && !HasMessages;
    public bool HasTopics => Topics.Count > 0;
    public bool HasSelectedChannel => SelectedChannel is not null;
    public bool ShowTopicPicker => AreChannelsExpanded && _expandedChannelId is not null && Topics.Count > 0;
    public bool ShowEmptyChannelTopicState =>
        HasSelectedChannel &&
        _hasAuthoritativeTopics &&
        !IsNavigationPending &&
        !HasConversationActivationError &&
        !HasTopics;
    public bool HasKnownContacts => KnownContacts.Count > 0;
    public bool IsMessagesSection => SelectedSection == ShellSection.Messages;
    public bool IsContactsSection => SelectedSection == ShellSection.Contacts;
    public bool IsSavedSection => SelectedSection == ShellSection.Saved;
    public bool IsSettingsSection => SelectedSection == ShellSection.Settings;
    public bool IsConversationWorkspaceSection => IsMessagesSection || IsSavedSection;
    public bool IsWideLayout => LayoutMode == ShellLayoutMode.Wide;
    public bool IsCompactLayout => LayoutMode == ShellLayoutMode.Compact;
    public bool IsIntermediateLayout => LayoutMode == ShellLayoutMode.Compact && _viewportWidth <= IntermediateLayoutMaximum;
    public bool IsNarrowLayout => LayoutMode == ShellLayoutMode.Narrow;
    public bool IsNotNarrowLayout => !IsNarrowLayout;
    public bool IsConversationPaneVisible =>
        IsConversationWorkspaceSection && (!IsNarrowLayout || IsConversationListVisibleOnNarrow);
    public bool IsWorkspaceContentPaneVisible =>
        IsConversationWorkspaceSection && (!IsNarrowLayout || !IsConversationListVisibleOnNarrow);
    public bool IsChatPaneVisible =>
        IsMessagesSection && (!IsNarrowLayout || !IsConversationListVisibleOnNarrow);
    public bool IsInlineDetailsVisible => IsMessagesSection && IsWideLayout && IsDetailsOpen;
    public bool IsOverlayDetailsVisible => IsMessagesSection && !IsWideLayout && IsDetailsOpen;
    public bool IsModalOverlayVisible => IsOverlayDetailsVisible || IsSearchOpen || IsMessageMenuOpen || IsChannelMenuOpen || IsTopicMenuOpen || IsAccountMenuOpen || IsDownloadCenterOpen ||
        IsComposerEmojiPickerOpen || IsReactionPickerOpen || IsBlockingDialogVisible;
    private bool IsBlockingDialogVisible => IsEditDialogOpen ||
        IsDeleteConfirmationOpen || IsChannelUnsubscribeConfirmationOpen || IsImageViewerOpen ||
        IsNewConversationOpen || IsChannelBrowserOpen || ChannelSettings.IsOpen || IsTopicMoveDialogOpen || IsTopicDeleteConfirmationOpen || IsTopicResolutionConfirmationOpen || LogoutConfirmationVisible;
    // The details backdrop already intercepts pointer input. Disabling the root
    // also propagates through every retained conversation's native controls.
    public bool IsPrimaryShellEnabled => !IsModalOverlayVisible || IsOverlayDetailsVisible && !IsBlockingDialogVisible ||
        IsSearchOpen || IsMessageMenuOpen || IsChannelMenuOpen ||
        IsTopicMenuOpen && !IsTopicMoveDialogOpen && !IsTopicDeleteConfirmationOpen && !IsTopicResolutionConfirmationOpen || IsAccountMenuOpen || IsDownloadCenterOpen ||
        IsComposerEmojiPickerOpen || IsReactionPickerOpen;
    public bool CanCompose =>
        IsConversationContentVisible &&
        !HasConversationActivationError &&
        _projectedState.Connection.Status == RelayCove.Core.ConnectionStatus.Connected;
    public bool CanSend => CanCompose && (!string.IsNullOrWhiteSpace(ComposerText) || HasAttachments) &&
        Attachments.All(attachment => attachment.Status == AttachmentUploadStatus.Uploaded);
    public bool CanMarkRead => CanCompose;
    public bool HasUnavailableFeatureMessage => !string.IsNullOrWhiteSpace(UnavailableFeatureMessage);
    public bool HasLoginError => !string.IsNullOrWhiteSpace(LoginError);
    public bool HasAttachments => Attachments.Count > 0;
    public bool HasAttachmentError => !string.IsNullOrWhiteSpace(AttachmentError);
    public bool HasActiveMessageAttachment => ActiveMessageAttachment?.IsImage == true;
    public bool HasMediaActionStatus => !string.IsNullOrWhiteSpace(MediaActionStatus);
    public bool IsMediaDownloadStatusVisible => IsMediaActionBusy || HasMediaActionStatus;
    public bool IsMediaDownloadIndeterminate => IsMediaActionBusy && !HasKnownMediaDownloadLength;
    public bool CanStartMediaDownload => !IsMediaActionBusy;
    public bool CanRetryMediaDownload => !IsMediaActionBusy && _failedMediaDownloadAttachment is not null;
    public bool ShowDownloadCenterCurrentTask => IsMediaActionBusy || CanRetryMediaDownload;
    public bool HasRecentDownloads => RecentDownloads.Count > 0;
    public bool IsDownloadCenterEmpty => !ShowDownloadCenterCurrentTask && !HasRecentDownloads;
    public bool HasDownloadButtonAttention => IsMediaActionBusy || HasUnseenCompletedDownloads || HasUnseenDownloadFailure;
    public bool HasDownloadFailure => CanRetryMediaDownload;
    public bool HasDownloadCenterStatus => !string.IsNullOrWhiteSpace(DownloadCenterStatus);
    public string DownloadButtonDescription => IsMediaActionBusy
        ? $"正在下载 {MediaDownloadProgress:P0}"
        : CanRetryMediaDownload
            ? "下载失败，打开下载内容"
            : HasUnseenCompletedDownloads
                ? "下载已完成，打开下载内容"
                : "打开下载内容";
    public string DownloadFolderPath => _fileSaveService.DownloadFolderPath;
    public bool HasDownloadSettingsStatus => !string.IsNullOrWhiteSpace(DownloadSettingsStatus);
    public bool HasMessageLoadError => !string.IsNullOrWhiteSpace(MessageLoadError);
    public bool HasTopicActionStatus => !string.IsNullOrWhiteSpace(TopicActionStatus);
    public bool ActiveTopicHasMessages => ActiveTopicAction?.MaxMessageId is > 0;
    public bool ActiveTopicIsEmpty => !ActiveTopicHasMessages;
    public bool CanSetActiveTopicVisibility => ActiveTopicAction is { } topic &&
        _session.CurrentUserId is > 0 &&
        _projectedState.Subscriptions.TryGetValue(topic.ChannelId, out var subscription) &&
        subscription.IsActive &&
        !IsTopicActionBusy;
    public bool CanMarkActiveTopicRead => ActiveTopicHasMessages && CanSetActiveTopicVisibility;
    public bool CanAdministerActiveTopicOperations => ActiveTopicHasMessages &&
        _session.IsOrganizationAdministrator &&
        ActiveTopicAction is { } activeTopic &&
        _projectedState.Subscriptions.TryGetValue(activeTopic.ChannelId, out var subscription) &&
        subscription.IsActive;
    public bool CanMoveActiveTopic => CanAdministerActiveTopicOperations && !IsTopicActionBusy;
    public bool CanResolveActiveTopic => CanAdministerActiveTopicOperations && ActiveTopicAction is { Topic.Length: > 0 } && !IsTopicActionBusy;
    public bool CanDeleteActiveTopic => CanAdministerActiveTopicOperations && !IsTopicActionBusy;
    public bool CanConfirmTopicMove => CanMoveActiveTopic && TopicMoveDestinationChannel is { } channel &&
        (channel.ChannelId != ActiveTopicAction?.ChannelId || !string.Equals(TopicMoveDestinationName.Trim(), ActiveTopicAction.Topic, StringComparison.Ordinal));
    public bool IsActiveTopicMutedPolicy => ActiveTopicAction?.VisibilityPolicy == TopicVisibilityPolicy.Muted;
    public bool IsActiveTopicInheritPolicy => ActiveTopicAction?.VisibilityPolicy == TopicVisibilityPolicy.None;
    public bool IsActiveTopicUnmutedPolicy => ActiveTopicAction?.VisibilityPolicy == TopicVisibilityPolicy.Unmuted;
    public bool IsActiveTopicFollowedPolicy => ActiveTopicAction?.VisibilityPolicy == TopicVisibilityPolicy.Followed;
    public bool ShowActiveTopicUnmutedPolicy => IsActiveTopicUnmutedPolicy ||
        ActiveTopicAction is { } topic && _projectedState.Subscriptions.GetValueOrDefault(topic.ChannelId)?.IsMuted == true;
    public bool ShowHistoryRetry => HasMessageLoadError && !HasConversationActivationError;
    public bool HasSearchResults => SearchResults.Count > 0;
    public bool IsSearchEmpty => !HasSearchResults;
    public string SearchEmptyText => IsSearchBusy ? "正在搜索…"
        : _hasSubmittedSearch ? "没有匹配结果。"
        : string.IsNullOrWhiteSpace(SearchQuery) ? "点击搜索或按 Enter 查看记录"
        : "点击搜索或按 Enter 开始搜索";
    public bool HasSearchError => !string.IsNullOrWhiteSpace(SearchError);
    public bool HasConversationFilterStatus =>
        IsConversationFilterBusy || !string.IsNullOrWhiteSpace(ConversationFilterError);
    public string ConversationFilterStatus => !string.IsNullOrWhiteSpace(ConversationFilterError)
        ? ConversationFilterError
        : IsConversationFilterBusy ? "正在搜索历史消息…" : string.Empty;
    public string ConversationFilterEmptyText => IsConversationFilterBusy
        ? "正在搜索历史消息…"
        : !string.IsNullOrWhiteSpace(ConversationFilterError)
            ? ConversationFilterError
            : string.IsNullOrWhiteSpace(ConversationFilterQuery) ? "暂无聊天" : "没有匹配聊天";
    public bool ShowConversationSearchIcon => ConversationFilterQuery.Length == 0;
    public bool HasMoreConversationFilterResults => _conversationFilterBeforeMessageId is not null;
    public bool ShowMoreConversationFilterResults =>
        HasMoreConversationFilterResults &&
        !IsConversationFilterBusy &&
        !string.IsNullOrWhiteSpace(ConversationFilterQuery);
    public bool HasMoreSearchResults => _searchBeforeMessageId is not null;
    public bool HasMoreSavedMessages => _savedBeforeMessageId is not null;
    public bool HasSavedMessages => SavedMessages.Count > 0;
    public bool IsSavedEmpty => !HasSavedMessages && !IsSavedLoading && string.IsNullOrWhiteSpace(SavedError);
    public bool HasSavedError => !string.IsNullOrWhiteSpace(SavedError);
    public bool HasNewConversationChoices => NewConversationChoices.Count > 0;
    public bool IsNewConversationChoiceEmpty => !HasNewConversationChoices;
    public bool IsNewConversationChoicesVisible => HasNewConversationChoices;
    public bool IsNewConversationChoiceEmptyVisible => IsNewConversationChoiceEmpty;
    public bool CanStartNewConversation => IsNewDirectConversationMode &&
        _allNewConversationChoices.Count(choice => choice.IsSelected) == 1;
    public bool IsNewDirectConversationMode => !IsNewChannelConversationMode;
    public bool IsLockedChannelTopicComposer => IsNewChannelConversationMode && IsNewConversationChannelLocked;
    public bool IsNewUnlockedChannelTopicComposer => IsNewChannelConversationMode && !IsNewConversationChannelLocked;
    public bool IsNewConversationModeSwitcherVisible => !IsLockedChannelTopicComposer;
    public bool IsNewConversationChannelPickerVisible => IsNewChannelConversationMode && !IsNewConversationChannelLocked;
    public bool IsLockedChannelTopicVisible => IsLockedChannelTopicComposer;
    public bool CanStartNewChannelConversation => IsNewChannelConversationMode &&
        CanCreatePrivateGroup &&
        !string.IsNullOrWhiteSpace(NewPrivateGroupName) &&
        _allNewConversationChoices.Count(choice => choice.IsSelected && !choice.IsSelf) >= 2;
    public bool CanCreatePrivateGroup =>
        _projectedState.Connection.Status == RelayCove.Core.ConnectionStatus.Connected;
    public string PrivateGroupCreateDisabledReason => !CanCreatePrivateGroup
        ? "当前未连接，暂时无法创建群聊。"
        : string.IsNullOrWhiteSpace(NewPrivateGroupName)
            ? "请填写群聊名称。"
            : _allNewConversationChoices.Count(choice => choice.IsSelected && !choice.IsSelf) < 2
                ? "请至少选择两名其他成员。"
                : string.Empty;
    public bool ShowPrivateGroupCreateDisabledReason => IsNewConversationOpen &&
        (!CanCreatePrivateGroup || IsNewChannelConversationMode && !CanStartNewChannelConversation);
    public bool HasNewConversationError => !string.IsNullOrWhiteSpace(NewConversationError);
    public bool CanChooseNewConversationChannel => !IsNewConversationChannelLocked;
    public bool HasActiveMessageAction => ActiveMessageAction is not null;
    public bool CanCopyActiveMessage => ActiveMessageAction is { HasBody: true, IsImageOnly: false };
    public bool CanEditActiveMessage => CanChangeMessage(ActiveMessageAction, delete: false);
    public bool CanDeleteActiveMessage => CanChangeMessage(ActiveMessageAction, delete: true);

    private bool CanChangeMessage(MessageItem? item, bool delete)
    {
        var state = _session.State;
        if (item?.CanMutate != true || item.MessageId is not { } id ||
            state.Connection.Status != RelayCove.Core.ConnectionStatus.Connected ||
            !state.Messages.TryGetValue(id, out var message) ||
            state.MessageMutations.GetValueOrDefault(id)?.Status is MessageMutationStatus.Submitting or MessageMutationStatus.Uncertain)
            return false;
        return delete
            ? state.MessageActions.CanDelete(message, _session.CurrentUserId, _timeProvider.GetUtcNow())
            : state.MessageActions.CanEdit(message, _session.CurrentUserId, _timeProvider.GetUtcNow());
    }

    private void NotifyMessageActionPermissions()
    {
        OnPropertyChanged(nameof(CanEditActiveMessage));
        OnPropertyChanged(nameof(CanDeleteActiveMessage));
    }
    public bool CanStarActiveMessage => ActiveMessageAction?.CanMutate == true && CanCompose;
    public bool CanUnsubscribeSelectedChannel =>
        _session.SelectedConversation is ChannelTopic selected &&
        _projectedState.Subscriptions.ContainsKey(selected.ChannelId) &&
        _projectedState.Connection.Status == RelayCove.Core.ConnectionStatus.Connected;
    public bool HasChannelUnsubscribeError => !string.IsNullOrWhiteSpace(ChannelUnsubscribeError);
    public bool CanCloseChannelUnsubscribe => !IsChannelUnsubscribeBusy;
    public bool HasChannelBrowserError => !string.IsNullOrWhiteSpace(ChannelBrowserError);
    public bool CanManageSelectedChannel => CanUnsubscribeSelectedChannel;
    public bool ShowChannelActionBoundary => ShowChannelDetails && !CanManageSelectedChannel;
    public bool HasDetailsLoadError => !string.IsNullOrWhiteSpace(DetailsLoadError);
    public bool HasDetailsMembers => DetailsMembers.Count > 0;
    public bool ShowDetailsMembersEmptyState => !HasDetailsMembers && !IsDetailsLoading;
    public string DetailsMemberCountLabel => $"{DetailsMembers.Count} 位成员";
    public bool IsCurrentUserPrivateGroupOwner => DetailsPrivateGroupOwnerId is { } ownerId &&
        ownerId == _session.CurrentUserId;
    public bool CanManagePrivateGroup => IsPrivateGroupAuthorityLoaded &&
        IsCurrentUserPrivateGroupOwner &&
        !IsPrivateGroupActionBusy;
    public bool ShowPrivateGroupManagementBoundary => IsPrivateGroupAuthorityLoaded &&
        DetailsPrivateGroupOwnerId is null &&
        !IsDetailsLoading;
    public string PrivateGroupManagementBoundary =>
        "此群的管理权限不是 RichChat 单群主结构，仅提供个人设置；不会推断或改写权限。";
    public bool HasPrivateGroupActionStatus => !string.IsNullOrWhiteSpace(PrivateGroupActionStatus);
    public bool CanInvitePrivateGroupMember => CanManagePrivateGroup && SelectedGroupInviteCandidate is not null;
    public bool CanRemovePrivateGroupMember => CanManagePrivateGroup && SelectedGroupRemoveCandidate is not null;
    public bool CanTransferPrivateGroupOwnership => CanManagePrivateGroup && SelectedGroupTransferCandidate is not null;
    public bool CanExitPrivateGroup => IsPrivateGroupAuthorityLoaded &&
        CanUnsubscribeSelectedChannel &&
        !IsCurrentUserPrivateGroupOwner &&
        !IsPrivateGroupActionBusy;
    public bool IsSelectedChannelMuted => _session.SelectedConversation is ChannelTopic selected &&
        _projectedState.Subscriptions.GetValueOrDefault(selected.ChannelId)?.IsMuted == true;
    public bool IsSelectedChannelPinned => _session.SelectedConversation is ChannelTopic selected &&
        _projectedState.Subscriptions.GetValueOrDefault(selected.ChannelId)?.IsPinned == true;
    public bool CanClearConversationCache => HasSelectedConversation && !IsClearConversationCacheBusy;
    public string ClearConversationCacheDescription => _session.SelectedConversation is ChannelTopic
        ? "只清除当前账号下此群聊在本机的缓存，不删除服务器消息；重新进入后会再次同步。"
        : "只清除此私信在本机的缓存，不删除服务器消息；重新进入后会再次同步。";
    public bool CanManageActiveChannel => ActiveChannelAction is { } channel &&
        _projectedState.Subscriptions.ContainsKey(channel.ChannelId) &&
        _projectedState.Connection.Status == RelayCove.Core.ConnectionStatus.Connected;
    public string SelectedChannelMuteLabel => _session.SelectedConversation is ChannelTopic selected &&
        _projectedState.Subscriptions.GetValueOrDefault(selected.ChannelId)?.IsMuted == true ? "取消静音" : "静音频道";
    public string SelectedChannelPinLabel => _session.SelectedConversation is ChannelTopic selected &&
        _projectedState.Subscriptions.GetValueOrDefault(selected.ChannelId)?.IsPinned == true ? "取消置顶" : "置顶频道";
    public string ActiveChannelMuteLabel => ActiveChannelAction is { } channel &&
        _projectedState.Subscriptions.GetValueOrDefault(channel.ChannelId)?.IsMuted == true ? "取消静音" : "静音频道";
    public string ActiveChannelPinLabel => ActiveChannelAction is { } channel &&
        _projectedState.Subscriptions.GetValueOrDefault(channel.ChannelId)?.IsPinned == true ? "取消置顶" : "置顶频道";
    public string ActiveChannelMarkReadLabel => ActiveChannelAction?.HasUnread == true
        ? "将所有消息标记为已读"
        : "将所有消息标记为未读";
    public string ActiveMessageStarActionLabel => ActiveMessageAction?.IsStarred == true
        ? "取消收藏"
        : "收藏消息";
    public string ActiveMessageTitle => ActiveMessageAction is null
        ? "消息操作"
        : $"{ActiveMessageAction.Sender} · {ActiveMessageAction.Timestamp}";
    public bool IsSystemTheme => AppearanceMode == AppAppearanceMode.System;
    public bool IsLightTheme => AppearanceMode == AppAppearanceMode.Light;
    public bool IsDarkTheme => AppearanceMode == AppAppearanceMode.Dark;
    public bool IsComfortableDensity => DensityMode == UiDensityMode.Comfortable;
    public bool IsCompactDensity => DensityMode == UiDensityMode.Compact;
    public bool IsSmallFont => FontScaleMode == UiFontScaleMode.Small;
    public bool IsDefaultFont => FontScaleMode == UiFontScaleMode.Default;
    public bool IsLargeFont => FontScaleMode == UiFontScaleMode.Large;
    public bool IsNarrowConversationWidth => ConversationWidthMode == UiConversationWidthMode.Narrow;
    public bool IsStandardConversationWidth => ConversationWidthMode == UiConversationWidthMode.Standard;
    public bool IsWideConversationWidth => ConversationWidthMode == UiConversationWidthMode.Wide;
    public bool IsAppearanceSettings => SelectedSettingsCategory == SettingsCategory.Appearance;
    public bool IsGeneralSettings => SelectedSettingsCategory == SettingsCategory.General;
    public bool IsNotificationSettings => SelectedSettingsCategory == SettingsCategory.Notifications;
    public bool IsStorageSettings => SelectedSettingsCategory == SettingsCategory.Storage;
    public bool IsAccountSettings => SelectedSettingsCategory == SettingsCategory.Account;
    public bool IsAboutSettings => SelectedSettingsCategory == SettingsCategory.About;
    public string ApplicationVersion { get; } = typeof(ShellViewModel).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "未知";
    public bool IsSystemNotificationSupported => _appNotificationService.IsSystemNotificationSupported;
    public string SystemNotificationStatus => _appNotificationService.SystemNotificationStatus;
    public string TaskbarBadgeStatus => _appNotificationService.TaskbarBadgeStatus;
    public double FontScaleSliderValue
    {
        get => _fontSize;
        set
        {
            var normalized = Math.Clamp(Math.Round(value), 11d, 18d);
            if (Math.Abs(_fontSize - normalized) < 0.01d) return;
            _fontSize = normalized;
            _preserveContinuousPreference = true;
            FontScaleMode = normalized <= 13d ? UiFontScaleMode.Small : normalized >= 15d ? UiFontScaleMode.Large : UiFontScaleMode.Default;
            _preserveContinuousPreference = false;
            OnPropertyChanged(nameof(FontScaleSliderValue));
            OnPropertyChanged(nameof(CurrentFontSizeLabel));
            SaveUiPreferences();
        }
    }
    public string CurrentFontSizeLabel => $"{FontScaleSliderValue:0} px";
    public double ConversationWidthSliderValue
    {
        get => _conversationPaneWidth;
        set
        {
            var normalized = Math.Clamp(Math.Round(value), 240d, 380d);
            if (Math.Abs(_conversationPaneWidth - normalized) < 0.01d) return;
            _conversationPaneWidth = normalized;
            _preserveContinuousPreference = true;
            ConversationWidthMode = normalized < 288d ? UiConversationWidthMode.Narrow : normalized >= 336d ? UiConversationWidthMode.Wide : UiConversationWidthMode.Standard;
            _preserveContinuousPreference = false;
            OnPropertyChanged(nameof(ConversationWidthSliderValue));
            OnPropertyChanged(nameof(CurrentConversationWidthLabel));
            OnPropertyChanged(nameof(ConversationPaneWidth));
            OnPropertyChanged(nameof(MessageRowMaximumWidth));
            SaveUiPreferences();
        }
    }
    public string CurrentConversationWidthLabel => $"{ConversationWidthSliderValue:0} px";
    public string ChannelGroupCountLabel => Channels.Count.ToString();
    public string DirectMessageGroupCountLabel => $"{DirectMessages.Count} 个会话";
    public double ChannelListHeight => AreChannelsExpanded
        ? Math.Min(FilteredChannels.Count * 38d + (ShowTopicPicker ? Topics.Count * 34d : 0d), 268d)
        : 0d;
    public double TopicListHeight => ShowTopicPicker
        ? Math.Min(Topics.Count * 34d, 136d)
        : 0d;
    public string CurrentUserDisplayName => _session.CurrentUserId is { } currentUserId &&
        _projectedState.Users.TryGetValue(currentUserId, out var currentUser)
            ? currentUser.FullName
            : "当前账户";
    public string CurrentUserInitial => string.IsNullOrWhiteSpace(CurrentUserDisplayName)
        ? "我"
        : AvatarInitials.Create(CurrentUserDisplayName);
    public string? CurrentUserAvatarUrl => _session.CurrentUserId is { } currentUserId &&
        _projectedState.Users.TryGetValue(currentUserId, out var currentUser)
            ? currentUser.DisplayAvatarUrl
            : null;
    public UserPresenceStatus? OwnPresenceStatus => _session.OwnPresenceStatus;
    public bool HasOwnPresenceStatus => OwnPresenceStatus is not null;
    public string OwnPresenceLabel => OwnPresenceStatus switch
    {
        UserPresenceStatus.Active => "在线",
        UserPresenceStatus.Idle => "忙碌",
        UserPresenceStatus.Offline => "离线",
        _ => "状态未确认"
    };
    public bool IsOwnPresenceOnline => OwnPresenceStatus == UserPresenceStatus.Active;
    public bool IsOwnPresenceIdle => OwnPresenceStatus == UserPresenceStatus.Idle;
    public bool IsOwnPresenceOffline => OwnPresenceStatus == UserPresenceStatus.Offline;
    public Brush OwnPresenceBrush => OwnPresenceStatus switch
    {
        UserPresenceStatus.Active => OnlinePresenceBrush,
        UserPresenceStatus.Idle => IdlePresenceBrush,
        _ => OfflinePresenceBrush
    };
    public string OwnPresenceStatusText => $"在线状态：{OwnPresenceLabel}";
    public string OwnStatusSummary => HasOwnPresenceStatus ? OwnPresenceLabel : string.Empty;
    public bool HasOwnStatusSummary => OwnStatusSummary.Length > 0;
    public string WorkspaceDisplayName
    {
        get
        {
            var host = _session.ActiveRealm?.Uri.Host ?? TryGetRealmHost(Realm);
            return string.Equals(host, "preview.invalid", StringComparison.OrdinalIgnoreCase)
                ? "Acme Workspace"
                : host ?? "RichChat";
        }
    }
    public bool IsNativePreview => string.Equals(
        _session.ActiveRealm?.Uri.Host,
        "preview.invalid",
        StringComparison.OrdinalIgnoreCase);
    public string NativePreviewStatus => "本地演示数据 · 不连接 Zulip";
    public bool ShowConnectionStatus => IsLoggedIn &&
        _projectedState.Connection.Status is not (RelayCove.Core.ConnectionStatus.Connected or RelayCove.Core.ConnectionStatus.Connecting) &&
        _projectedState.Connection.Detail != "cache_first";
    public bool HasCurrentConversationUnread => _session.SelectedConversation is { } selected &&
        GetConversationUnread(_projectedState.Unread, selected) > 0;
    public bool ShowLoadOlderButton => IsConversationContentVisible && !IsNativePreview && !HasReachedOldestMessage;
    public bool ShowNewMessagesButton => NewMessageCount > 0 || _isMessageViewportBeyondJumpThreshold;
    public string NewMessagesButtonText => "跳转到最新消息";
    public string MessageEmptyTitle => !HasSelectedConversation
        ? "选择一个会话"
        : _projectedState.Connection.Status == RelayCove.Core.ConnectionStatus.Offline && _projectedState.Connection.Detail != "cache_first"
            ? "当前离线缓存没有更多可显示消息"
            : "这里还没有消息";
    public GridLength ConversationPaneWidth =>
        IsNarrowLayout
            ? IsConversationListVisibleOnNarrow ? GridLength.Star : new GridLength(0)
            : new GridLength(ConversationWidthSliderValue);
    public GridLength ChatPaneWidth =>
        IsNarrowLayout
            ? IsConversationListVisibleOnNarrow ? new GridLength(0) : GridLength.Star
            : GridLength.Star;
    public GridLength InlineDetailsWidth => IsInlineDetailsVisible
        ? new GridLength(284)
        : new GridLength(0);
    public double MessageRowMaximumWidth
    {
        get
        {
            var conversationWidth = IsNarrowLayout ? 0d : ConversationWidthSliderValue;
            var detailsWidth = IsInlineDetailsVisible ? 284d : 0d;
            var chatPaneWidth = Math.Max(0d, _viewportWidth - conversationWidth - detailsWidth);
            var messageContentWidth = Math.Max(0d, chatPaneWidth - 40d);
            var rowWidth = Math.Min(690d, messageContentWidth * (IsNarrowLayout ? 0.90d : 0.76d));

            return Math.Max(0d, rowWidth);
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0) return;
        if (!IsNativePreview && Updates is not null) _ = Updates.CheckOnStartupAsync();
        _activateInitialConversationWhenAvailable = !IsNativePreview;
        try
        {
            await _session.RestoreAsync(cancellationToken);
        }
        catch (CredentialVaultException)
        {
            LoginError = "保存的登录凭据不可用，请重新登录。";
        }
        catch (GatewayException exception)
        {
            LoginError = DescribeGatewayFailure(exception);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            LoginError = "本地缓存不可用，请重新登录。";
        }
        finally
        {
            Project(_session.State);
#if DEBUG
            if (NativeShellPreviewSession.IsRequested)
            {
                ApplyNativePreviewTheme(NativeShellPreviewSession.RequestedTheme);
                ApplyNativePreviewScene(NativeShellPreviewSession.RequestedScene);
            }
            else
            {
                ActivateInitialConversationIfAvailable();
            }
#else
            ActivateInitialConversationIfAvailable();
#endif
        }
    }

    private void ActivateInitialConversationIfAvailable()
    {
        if (!IsLoggedIn || IsNavigationPending) return;
        if (_session.SelectedConversation is not null)
        {
            _activateInitialConversationWhenAvailable = false;
            return;
        }
        if (Conversations.FirstOrDefault() is { } conversation)
        {
            _activateInitialConversationWhenAvailable = false;
            ActivateConversation(conversation);
        }
    }

    internal void ApplyNativePreviewScene(string? scene)
    {
        switch (scene?.Trim().ToLowerInvariant())
        {
            case "shell-avatars":
                CloseTransientOverlays();
                break;
            case "composer-empty":
                ApplyNativeComposerPreview(null);
                break;
            case "composer-uploading":
                ApplyNativeComposerPreview(AttachmentUploadStatus.Uploading);
                break;
            case "composer-uploaded":
                ApplyNativeComposerPreview(AttachmentUploadStatus.Uploaded);
                break;
            case "search-flow":
                OpenSearch();
                break;
            case "message-quick-actions":
                foreach (var message in Messages) message.SetQuickActionsPreviewVisible(false);
                Messages.FirstOrDefault(message => message.IsOwn)?.SetQuickActionsPreviewVisible(true);
                Messages.LastOrDefault(message => message.IsOther)?.SetQuickActionsPreviewVisible(true);
                break;
            case "outbox-states":
                CloseTransientOverlays();
                break;
            case "composer-emoji":
                OpenComposerEmojiPickerAt(new PopoverAnchorRequest(402d, 820d));
                break;
            case "message-menu":
                if (Messages.FirstOrDefault(message => message.IsOwn && message.MessageId is not null) is { } menuMessage)
                {
                    OpenMessageMenuAt(new MessageMenuRequest(
                        menuMessage,
                        Math.Max(420d, _viewportWidth - 232d),
                        210d));
                }
                break;
            case "reaction-picker":
                if (Messages.FirstOrDefault(message => message.CanMutate) is { } reactionMessage)
                {
                    MessageMenuAnchorX = Math.Max(420d, _viewportWidth - 232d);
                    MessageMenuAnchorY = 210d;
                    OpenReactionPicker(reactionMessage);
                }
                break;
            case "account-menu":
                CloseTransientOverlays();
                IsAccountMenuOpen = true;
                break;
            case "settings":
                ShowSettings();
                break;
            case "download-center":
                HasUnseenDownloadFailure = true;
                ToggleDownloadCenter();
                break;
            case "details":
                CloseTransientOverlays();
                IsDetailsOpen = true;
                _ = LoadConversationSettingsAsync();
                break;
            case "narrow-list":
                CloseTransientOverlays();
                IsDetailsOpen = false;
                IsConversationListVisibleOnNarrow = true;
                break;
            case "narrow-chat":
                CloseTransientOverlays();
                IsDetailsOpen = false;
                IsConversationListVisibleOnNarrow = false;
                break;
            case "dm-cache-switch":
                if (!_nativePreviewCacheSwitchStarted)
                {
                    _nativePreviewCacheSwitchStarted = true;
                    _ = RunNativePreviewCacheSwitchAsync();
                }
                break;
        }
    }

    private async Task RunNativePreviewCacheSwitchAsync()
    {
        var directMessages = DirectMessages.Take(2).ToArray();
        if (directMessages.Length < 2) return;

        await ActivateConversationFromNavigationAsync(
            directMessages[0].Conversation,
            null,
            null,
            directMessages[0]);
        await Task.Yield();
        await ActivateConversationFromNavigationAsync(
            directMessages[1].Conversation,
            null,
            null,
            directMessages[1]);
        await Task.Yield();
        await ActivateConversationFromNavigationAsync(
            directMessages[0].Conversation,
            null,
            null,
            directMessages[0]);
    }

    internal void ApplyNativePreviewTheme(string? theme)
    {
        AppearanceMode = theme?.Trim().ToLowerInvariant() switch
        {
            "dark" => AppAppearanceMode.Dark,
            "system" => AppAppearanceMode.System,
            _ => AppAppearanceMode.Light
        };
    }

    public void UpdateViewport(double width, double height = 900d)
    {
        if (!double.IsFinite(width) || width <= 0) return;
        _viewportWidth = width;
        if (double.IsFinite(height) && height > 0) _viewportHeight = height;
        OnPropertyChanged(nameof(EmojiPickerWidth));
        OnPropertyChanged(nameof(EmojiPickerHeight));
        OnPropertyChanged(nameof(DefaultEmojiPickerHeight));
        OnPropertyChanged(nameof(StickerPickerHeight));
        OnPropertyChanged(nameof(StickerPickerColumns));
        OnPropertyChanged(nameof(EmojiPickerContentWidth));
        OnPropertyChanged(nameof(EmojiPickerColumns));
        OnPropertyChanged(nameof(ComposerEmojiPickerColumns));
        var next = width >= WideLayoutMinimum
            ? ShellLayoutMode.Wide
            : width <= NarrowLayoutMaximum
                ? ShellLayoutMode.Narrow
                : ShellLayoutMode.Compact;
        if (next == LayoutMode)
        {
            OnPropertyChanged(nameof(MessageRowMaximumWidth));
            OnPropertyChanged(nameof(IsIntermediateLayout));
            return;
        }

        var previous = LayoutMode;
        LayoutMode = next;
        if (previous == ShellLayoutMode.Wide && next != ShellLayoutMode.Wide)
        {
            IsDetailsOpen = false;
        }

        if (next == ShellLayoutMode.Narrow)
        {
            IsConversationListVisibleOnNarrow = !HasSelectedConversation;
        }

        NotifyLayoutProperties();
#if DEBUG
        if (NativeShellPreviewSession.IsRequested)
        {
            ApplyNativePreviewScene(NativeShellPreviewSession.RequestedScene);
        }
#endif
    }

    internal void SetWindowActive(bool isActive)
    {
        if (_disposed) return;
        if (isActive) RefreshAttachmentDownloads();
        Updates?.SetWindowActive(isActive);
        if (_isWindowActive == isActive)
        {
            if (!isActive) return;
            if (!IsApplicationWindowForeground)
            {
                CancelAutoMarkReadOperation(allowRetry: true);
                return;
            }
            _appNotificationService.StopTaskbarFlash();
            RequestAutoMarkDisplayedRead(_projectedState);
            return;
        }
        _isWindowActive = isActive;
        if (!isActive)
        {
            CancelAutoMarkReadOperation(allowRetry: true);
            return;
        }

        if (!IsApplicationWindowForeground)
        {
            CancelAutoMarkReadOperation(allowRetry: true);
            return;
        }
        _appNotificationService.StopTaskbarFlash();
        RequestAutoMarkDisplayedRead(_projectedState);
    }

    [RelayCommand(IncludeCancelCommand = true, AllowConcurrentExecutions = false)]
    private async Task LoginAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _loginInFlight, 1) != 0) return;
        LoginError = null;
        try
        {
            await _session.LoginAsync(Realm, Email, Password, cancellationToken);
            _lastRealmStore.Set(Realm);
        }
        catch (CredentialVaultException)
        {
            LoginError = "无法安全保存登录凭据。";
        }
        catch (GatewayException exception)
        {
            LoginError = DescribeGatewayFailure(exception);
        }
        catch (ArgumentException)
        {
            LoginError = "请输入有效的 HTTPS Realm、邮箱和密码。";
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("active", StringComparison.OrdinalIgnoreCase))
        {
            LoginError = "当前已有活动会话，请先注销。";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LoginError = "登录已取消。";
        }
        catch (Exception)
        {
            LoginError = "本地缓存不可用，请重启应用后重试。";
        }
        finally
        {
            Password = string.Empty;
            Project(_session.State);
            Volatile.Write(ref _loginInFlight, 0);
        }
    }

    private void ApplyNativeComposerPreview(AttachmentUploadStatus? status)
    {
        SetComposerTextWithoutTracking(string.Empty);
        Attachments.Clear();
        if (_activeDraftKey is { } key) _attachmentDrafts[key] = [];
        if (status is null)
        {
            NotifyAttachmentProperties();
            return;
        }

        var file = new SelectedAttachmentFile(
            "preview-upload.png",
            "image/png",
            100,
            _ => Task.FromResult<Stream>(new MemoryStream(new byte[100])));
        var attachment = new AttachmentDraftItem(file);
        if (status == AttachmentUploadStatus.Uploading)
        {
            attachment.BeginUpload();
            attachment.ReportUploadProgress(new RealmMediaTransferProgress(58, 100));
        }
        else
        {
            attachment.Uploaded = new UploadedAttachment(
                file.FileName,
                "https://preview.invalid/user_uploads/preview-upload.png");
            attachment.UploadProgress = 1d;
            attachment.UploadProgressLabel = "100%";
            attachment.Status = AttachmentUploadStatus.Uploaded;
        }

        Attachments.Add(attachment);
        if (_activeDraftKey is { } activeKey) _attachmentDrafts[activeKey] = [attachment];
        NotifyAttachmentProperties();
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task OpenRegistrationAsync()
    {
        LoginError = null;
        if (!RealmEndpoint.TryParse(Realm, out var endpoint) || endpoint is null)
        {
            LoginError = "请先输入有效的 HTTPS Realm 地址。";
            return;
        }

        try
        {
            await _platformInteractions.OpenUriAsync(new Uri(endpoint.Uri, "register/"));
        }
        catch (Exception)
        {
            LoginError = "无法打开 Zulip 官方注册页面，请检查系统浏览器设置。";
        }
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task OpenPasswordResetAsync()
    {
        LoginError = null;
        if (!RealmEndpoint.TryParse(Realm, out var endpoint) || endpoint is null)
        {
            LoginError = "请先输入有效的 HTTPS Realm 地址。";
            return;
        }

        try
        {
            await _platformInteractions.OpenUriAsync(new Uri(endpoint.Uri, "accounts/password/reset/"));
        }
        catch (Exception)
        {
            LoginError = "无法打开密码重置页面，请检查系统浏览器设置。";
        }
    }

    [RelayCommand(IncludeCancelCommand = true, AllowConcurrentExecutions = false)]
    private async Task LoadOlderAsync(CancellationToken cancellationToken)
    {
        if (_session.SelectedConversation is null || HasReachedOldestMessage) return;
        MessageLoadError = null;
        IsLoadingOlder = true;
        try
        {
            if (!await ExecuteSessionActionAsync(() => _session.LoadOlderAsync(cancellationToken)))
            {
                MessageLoadError = LoginError ?? "无法加载更早消息，请稍后重试。";
            }
        }
        finally
        {
            IsLoadingOlder = false;
            ProjectHistoryState(_session.SelectedConversation);
        }
    }

    internal Task ReportMessageViewportAsync(
        int firstVisibleItemIndex,
        int lastVisibleItemIndex,
        double verticalOffset,
        long? timestampMilliseconds = null,
        double? bottomDistanceDip = null,
        double? viewportHeightDip = null,
        string? expectedConversationKey = null,
        long expectedHistoryGeneration = long.MinValue)
    {
        if (!IsMessageViewportReportCurrent(expectedConversationKey, expectedHistoryGeneration)) return Task.CompletedTask;
        _ = verticalOffset;
        UpdateMessageViewportBottomState(bottomDistanceDip, viewportHeightDip, lastVisibleItemIndex);
        return Task.CompletedTask;
    }

    internal async Task RequestOlderFromTopInputAsync(
        long? timestampMilliseconds = null,
        string? expectedConversationKey = null,
        long expectedHistoryGeneration = long.MinValue)
    {
        if (!IsMessageViewportReportCurrent(expectedConversationKey, expectedHistoryGeneration)) return;
        await TryRequestOlderAsync(0, timestampMilliseconds);
    }

    private async Task TryRequestOlderAsync(int firstVisibleItemIndex, long? timestampMilliseconds)
    {
        // Programmatic realization and the final native jump both raise viewport
        // callbacks. Starting pagination from those transient positions lets a
        // prepend-anchor restore compete with the authoritative latest request.
        if (PendingMessageScrollRequest is not null || IsNavigationPending || _session.HistoryState.IsLoading)
        {
            return;
        }

        var now = timestampMilliseconds ?? Environment.TickCount64;
        if (!MessageViewportPolicy.ShouldRequestOlder(
                firstVisibleItemIndex,
                ShowLoadOlderButton,
                IsLoadingOlder,
                HasMessageLoadError,
                now,
                _lastAutomaticLoadOlderMilliseconds) ||
            Interlocked.Exchange(ref _automaticLoadOlderInFlight, 1) != 0)
        {
            return;
        }

        _lastAutomaticLoadOlderMilliseconds = now;
        try
        {
            await LoadOlderCommand.ExecuteAsync(null);
        }
        finally
        {
            Volatile.Write(ref _automaticLoadOlderInFlight, 0);
        }
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task ScrollToLatestAsync()
    {
        var selected = _session.SelectedConversation;
        var history = _session.HistoryState;
        if (selected is null || history.IsLoading || IsNavigationPending) return;

        if (_messageAnchorHistory is { } anchor &&
            anchor.AccountId == _session.AccountId &&
            anchor.ConversationKey == selected.CanonicalKey &&
            anchor.Generation == history.Generation)
        {
            // An anchor window ends near the search hit, not necessarily at the
            // newest message. Reuse normal navigation to load the latest page first.
            if (!await ActivateConversationFromNavigationAsync(
                    selected, SelectedChannel, SelectedTopic, SelectedDirectMessage)) return;
            ProjectLatestStateImmediately();
        }
        QueueScrollToLatest(MessageScrollReason.ManualJumpToLatest);
    }

    [RelayCommand]
    private void RetryConversationActivation()
    {
        if (SelectedDirectMessage is { } directMessage)
        {
            ActivateDirectMessage(directMessage);
            return;
        }
        if (SelectedTopic is { } topic)
        {
            ActivateTopic(topic);
            return;
        }
        if (SelectedChannel is { } channel)
        {
            ActivateChannel(channel);
        }
    }

    internal void ReportMessageBottomDistance(
        double bottomDistanceDip,
        double? viewportHeightDip = null,
        string? expectedConversationKey = null,
        long expectedHistoryGeneration = long.MinValue)
    {
        if (!double.IsFinite(bottomDistanceDip) ||
            !IsMessageViewportReportCurrent(expectedConversationKey, expectedHistoryGeneration))
        {
            return;
        }
        UpdateMessageViewportBottomState(Math.Max(0d, bottomDistanceDip), viewportHeightDip, -1);
    }

    private bool IsMessageViewportReportCurrent(
        string? expectedConversationKey,
        long expectedHistoryGeneration)
    {
        if (expectedHistoryGeneration == long.MinValue)
        {
            return true;
        }

        return expectedConversationKey is not null &&
            expectedHistoryGeneration == CurrentHistoryGeneration &&
            string.Equals(expectedConversationKey, CurrentConversationKey, StringComparison.Ordinal);
    }

    private void UpdateMessageViewportBottomState(
        double? bottomDistanceDip,
        double? viewportHeightDip,
        int lastVisibleItemIndex)
    {
        var isNearBottom = MessageViewportPolicy.IsNearBottom(bottomDistanceDip, lastVisibleItemIndex, Messages.Count);
        _isMessageViewportNearBottom = isNearBottom;
        if (PendingMessageScrollRequest is null && !IsNavigationPending)
        {
            SetMessageViewportBeyondJumpThreshold(
                MessageViewportPolicy.ShouldShowJumpToLatest(bottomDistanceDip, viewportHeightDip));
        }
        if (!isNearBottom)
        {
            CancelAutoMarkReadOperation(allowRetry: true);
        }
        if (isNearBottom &&
            NewMessageCount > 0 &&
            PendingMessageScrollRequest?.Reason != MessageScrollReason.ManualJumpToLatest)
        {
            NewMessageCount = 0;
        }
        // A realtime-follow request is intentionally non-blocking for marking
        // the active conversation as read. The viewport was already at the
        // bottom when the event arrived, and WinUI's later programmatic-scroll
        // acknowledgement is not a reliable prerequisite for server confirmation.
        // Other pending requests still require acknowledgement.
        if (isNearBottom &&
            (PendingMessageScrollRequest is null ||
             PendingMessageScrollRequest.Reason == MessageScrollReason.RealtimeFollow))
        {
            RequestAutoMarkDisplayedRead(_projectedState);
        }
    }

    [RelayCommand(IncludeCancelCommand = true, AllowConcurrentExecutions = false, CanExecute = nameof(CanSend))]
    private async Task SendAsync(CancellationToken cancellationToken)
    {
        var conversation = _session.SelectedConversation;
        if (conversation is null) return;
        var content = ComposerText;
        var attachmentSnapshot = Attachments.ToArray();
        if (!CanSend || attachmentSnapshot.Any(attachment => attachment.Uploaded is null)) return;
        CancelActivationScrollForUserInteraction(conversation);

        var key = conversation.CanonicalKey;
        var accountId = _session.AccountId;
        var previousOutbox = _session.State.Outbox;
        var uploadedMarkdown = attachmentSnapshot
            .Select(attachment => BuildUploadedAttachmentMarkdown(attachment.Uploaded!, attachment.IsImage));
        var sendContent = string.Join(
            "\n",
            new[] { content.TrimEnd() }.Where(value => value.Length > 0).Concat(uploadedMarkdown));
        ClearSubmittedComposerDraft(key);
        var messageSendStarted = false;
        void ObserveSubmittedMessage(object? sender, ClientStateChangedEventArgs args)
        {
            if (_session.AccountId == accountId && args.State.Outbox.Values.Any(entry =>
                    !previousOutbox.ContainsKey(entry.LocalId) && entry.Conversation == conversation && entry.Content == sendContent))
            {
                Volatile.Write(ref messageSendStarted, true);
            }
        }

        // Session validation can fail before an outbox item exists. Once queued, keep
        // recovery with the message even if realtime confirms it before the POST returns.
        _session.StateChanged += ObserveSubmittedMessage;
        try
        {
            var succeeded = await ExecuteSessionActionAsync(async () =>
            {
                await _session.SendAsync(conversation, sendContent, cancellationToken);
                if (_session.AccountId == accountId && _session.SelectedConversation == conversation)
                    QueueScrollToLatest(MessageScrollReason.RealtimeFollow);
            }, "消息未能发送，请恢复内容后重试。");

            if (!succeeded && !Volatile.Read(ref messageSendStarted) && !_disposed && _session.AccountId == accountId)
            {
                var entry = new OutboxEntry(
                    $"unsubmitted-{Guid.NewGuid():N}", conversation, sendContent, DateTimeOffset.UtcNow,
                    OutboxState.Failed, OutboxFailureKind.Rejected);
                _unsubmittedMessages[entry.LocalId] = entry;
                ProjectLatestStateImmediately();
            }
        }
        finally
        {
            _session.StateChanged -= ObserveSubmittedMessage;
        }
    }

    [RelayCommand(IncludeCancelCommand = true, AllowConcurrentExecutions = false)]
    private Task MarkReadAsync(CancellationToken cancellationToken) =>
        ExecuteSessionActionAsync(() => _session.MarkDisplayedReadAsync(cancellationToken));

    [RelayCommand]
    private void RequestLogout()
    {
        IsAccountMenuOpen = false;
        LogoutConfirmationVisible = true;
    }

    [RelayCommand]
    private void CancelLogout() => LogoutConfirmationVisible = false;

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task ConfirmLogoutAsync()
    {
        if (!LogoutConfirmationVisible) return;
        UploadAvatarCommand.Cancel();
        var accountId = _session.AccountId;
        SaveOwnNameCommand.Cancel();
        if (await ExecuteSessionActionAsync(
                () => _session.LogoutAsync(),
                "注销未完全完成，请重试以安全删除凭据并锁定本地缓存。"))
        {
            if (accountId is { } loggedOutAccount && _notificationAvatarFileStore is not null)
            {
                await _notificationAvatarFileStore.ClearAccountAsync(loggedOutAccount);
            }
            LogoutConfirmationVisible = false;
        }
    }

    [RelayCommand]
    private void RequestClearCache() => ClearCacheConfirmationVisible = true;

    [RelayCommand]
    private void CancelClearCache() => ClearCacheConfirmationVisible = false;

    [RelayCommand]
    private void RecoverOutbox(MessageItem? message)
    {
        if (message?.CanRecover == true)
        {
            var unsubmittedId = message.Id.StartsWith("local-unsubmitted-", StringComparison.Ordinal)
                ? message.Id["local-".Length..]
                : null;
            if (unsubmittedId is not null &&
                (_messagePresentationAccountId != _session.AccountId ||
                 !_unsubmittedMessages.TryGetValue(unsubmittedId, out var entry) ||
                 entry.Conversation != _session.SelectedConversation)) return;

            var uploadedMarkdown = Attachments
                .Where(attachment => attachment.Uploaded is not null)
                .Select(attachment => BuildUploadedAttachmentMarkdown(attachment.Uploaded!, attachment.IsImage))
                .ToHashSet(StringComparer.Ordinal);
            ComposerText = uploadedMarkdown.Count == 0
                ? message.Content
                : string.Join(
                    "\n",
                    message.Content.Split('\n').Where(line => !uploadedMarkdown.Contains(line)));
            if (unsubmittedId is not null && _unsubmittedMessages.Remove(unsubmittedId))
            {
                ProjectLatestStateImmediately();
            }
        }
    }

    [RelayCommand(IncludeCancelCommand = true, AllowConcurrentExecutions = false)]
    private async Task ConfirmClearCacheAsync(CancellationToken cancellationToken)
    {
        if (!ClearCacheConfirmationVisible) return;
        var accountId = _session.AccountId;
        if (await ExecuteSessionActionAsync(() => _session.ClearLocalCacheAsync(cancellationToken)) &&
            accountId is { } clearedAccount &&
            _notificationAvatarFileStore is not null)
        {
            await _notificationAvatarFileStore.ClearAccountAsync(clearedAccount, cancellationToken);
        }
        ClearCacheConfirmationVisible = false;
    }

    [RelayCommand]
    private void ShowMessages()
    {
        CloseTransientOverlays();
        SelectedSection = ShellSection.Messages;
        UnavailableFeatureMessage = null;
    }

    [RelayCommand]
    private void ShowContacts()
    {
        CloseTransientOverlays();
        SelectedSection = ShellSection.Contacts;
        IsDetailsOpen = false;
        UnavailableFeatureMessage = null;
    }

    [RelayCommand]
    private void ShowSettings()
    {
        CloseTransientOverlays();
        SelectedSection = ShellSection.Settings;
        SelectedSettingsCategory = SettingsCategory.Appearance;
        IsDetailsOpen = false;
        UnavailableFeatureMessage = null;
    }

    [RelayCommand]
    private void ToggleSettings()
    {
        if (IsSettingsSection)
        {
            ShowMessages();
            return;
        }

        ShowSettings();
    }

    [RelayCommand(IncludeCancelCommand = true, AllowConcurrentExecutions = false)]
    private async Task ShowSavedAsync(CancellationToken cancellationToken)
    {
        CloseTransientOverlays();
        SelectedSection = ShellSection.Saved;
        if (IsNarrowLayout) IsConversationListVisibleOnNarrow = false;
        IsDetailsOpen = false;
        await RefreshSavedAsync(cancellationToken).ConfigureAwait(false);
    }

    [RelayCommand]
    private void ToggleAccountMenu()
    {
        var open = !IsAccountMenuOpen;
        CloseTransientOverlays();
        IsAccountMenuOpen = open;
    }

    [RelayCommand]
    private void CloseAccountMenu() => IsAccountMenuOpen = false;

    [RelayCommand(CanExecute = nameof(CanEditOwnName))]
    private void EditOwnName()
    {
        if (!CanEditOwnName) return;
        _nameEditAccountId = _session.AccountId;
        OwnNameDraft = CurrentUserDisplayName;
        OwnNameEditStatus = null;
        IsOwnNameEditing = true;
    }

    [RelayCommand(CanExecute = nameof(CanChangeOwnNameDraft))]
    private void CancelOwnNameEdit()
    {
        if (IsOwnNameSaveBusy) return;
        IsOwnNameEditing = false;
        OwnNameDraft = string.Empty;
        OwnNameEditStatus = null;
        _nameEditAccountId = null;
    }

    [RelayCommand(CanExecute = nameof(CanSaveOwnName), IncludeCancelCommand = true)]
    private async Task SaveOwnNameAsync(CancellationToken cancellationToken)
    {
        if (!CanSaveOwnName || _nameEditAccountId is not { } accountId) return;
        var fullName = OwnNameDraft.Trim();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCancellation.Token);
        IsOwnNameSaveBusy = true;
        OwnNameEditStatus = null;
        try
        {
            var confirmed = await _session.UpdateOwnNameAsync(accountId, fullName, linked.Token);
            if (_disposed || linked.IsCancellationRequested || _session.AccountId != accountId) return;
            OwnNameEditStatus = confirmed ? "名字已更新。" : "名字未更新，组织可能不允许修改，请核对当前名字。";
            if (confirmed) IsOwnNameEditing = false;
        }
        catch (OperationCanceledException)
        {
            // Logout, account changes and shutdown discard this editor's pending result.
        }
        catch (GatewayException exception)
        {
            if (!_disposed && !linked.IsCancellationRequested && _session.AccountId == accountId)
                OwnNameEditStatus = exception.Kind is GatewayErrorKind.Offline or GatewayErrorKind.Server or GatewayErrorKind.Protocol
                    ? "无法确认名字是否已更新，请核对当前名字后再试。"
                    : $"修改名字失败：{DescribeGatewayFailure(exception)}";
        }
        catch (Exception)
        {
            if (!_disposed && !linked.IsCancellationRequested && _session.AccountId == accountId)
                OwnNameEditStatus = "修改名字未完成，请检查连接后再试。";
        }
        finally
        {
            if (!_disposed) IsOwnNameSaveBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanUploadAvatar), IncludeCancelCommand = true)]
    private async Task UploadAvatarAsync(CancellationToken cancellationToken)
    {
        if (!CanUploadAvatar || _session.AccountId is not { } accountId) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCancellation.Token);
        var token = linked.Token;
        IsAvatarUploadBusy = true;
        AvatarUploadStatus = null;
        try
        {
            var file = await _fileSelectionService.PickAvatarAsync(token);
            token.ThrowIfCancellationRequested();
            if (file is null || _session.AccountId != accountId) return;
            var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (extension is not (".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" or ".bmp"))
            {
                AvatarUploadStatus = "请选择 PNG、JPG、WebP、GIF 或 BMP 图片。";
                return;
            }
            if (file.Length <= 0 || file.Length > _session.MaxAvatarUploadBytes)
            {
                AvatarUploadStatus = $"请选择非空图片，大小不超过 {_session.MaxAvatarUploadBytes / (1024d * 1024d):0.#} MB。";
                return;
            }
            await using var stream = await file.OpenReadAsync(token);
            token.ThrowIfCancellationRequested();
            if (_session.AccountId != accountId) return;
            var length = stream.CanSeek ? stream.Length : file.Length;
            if (length != file.Length)
            {
                AvatarUploadStatus = "图片已发生变化，请重新选择。";
                return;
            }
            await _session.UploadOwnAvatarAsync(accountId,
                new AttachmentUpload(file.FileName, file.ContentType, length, stream), token);
            if (!_disposed && _session.AccountId == accountId) AvatarUploadStatus = "头像已更新。";
        }
        catch (OperationCanceledException)
        {
            // Cancelled selection, shutdown and old-account results do not update the current account's UI.
        }
        catch (GatewayException exception)
        {
            if (!_disposed && _session.AccountId == accountId)
                AvatarUploadStatus = exception.Kind is GatewayErrorKind.Offline or GatewayErrorKind.Server or GatewayErrorKind.Protocol
                    ? "无法确认头像是否已更新，请检查头像后再试。"
                    : $"头像上传失败：{DescribeGatewayFailure(exception)}";
        }
        catch (Exception)
        {
            if (!_disposed && _session.AccountId == accountId) AvatarUploadStatus = "头像上传未完成，请检查图片和连接后再试。";
        }
        finally
        {
            if (!_disposed) IsAvatarUploadBusy = false;
        }
    }

    [RelayCommand]
    private void ToggleDownloadCenter()
    {
        var open = !IsDownloadCenterOpen;
        CloseTransientOverlays();
        IsDownloadCenterOpen = open;
        if (!open) return;
        HasUnseenCompletedDownloads = false;
        HasUnseenDownloadFailure = false;
        RefreshDownloadAvailability();
    }

    [RelayCommand]
    private void CloseDownloadCenter() => IsDownloadCenterOpen = false;

    [RelayCommand]
    private void ToggleChannels() => AreChannelsExpanded = !AreChannelsExpanded;

    [RelayCommand]
    private void ToggleDirectMessages() => AreDirectMessagesExpanded = !AreDirectMessagesExpanded;

    [RelayCommand]
    private void ShowAppearanceSettings() => SelectedSettingsCategory = SettingsCategory.Appearance;

    [RelayCommand]
    private void ShowGeneralSettings()
    {
        RefreshStartupSettings();
        SelectedSettingsCategory = SettingsCategory.General;
    }

    [RelayCommand]
    private void ShowNotificationSettings() => SelectedSettingsCategory = SettingsCategory.Notifications;

    [RelayCommand]
    private void ShowStorageSettings() => SelectedSettingsCategory = SettingsCategory.Storage;

    [RelayCommand]
    private void ShowAccountSettings() => SelectedSettingsCategory = SettingsCategory.Account;

    [RelayCommand]
    private void ShowAboutSettings() => SelectedSettingsCategory = SettingsCategory.About;

    [RelayCommand]
    private void OpenSearch() => OpenSearchDialog(null);

    [RelayCommand]
    private void OpenConversationSearch()
    {
        if (_session.SelectedConversation is not { } conversation ||
            !IsRelayCoveConversation(conversation, _projectedState)) return;
        OpenSearchDialog(conversation);
    }

    private void OpenSearchDialog(ConversationKey? conversation)
    {
        CloseTransientOverlays();
        CancelSearchInput();
        _searchConversation = conversation;
        _hasSubmittedSearch = false;
        _serverSearchResults = [];
        _searchBeforeMessageId = null;
        SearchError = null;
        IsSearchBusy = false;
        SelectedSearchResult = null;
        SearchQuery = string.Empty;
        IsSearchOpen = true;
        ProjectSearch();
        OnPropertyChanged(nameof(HasMoreSearchResults));
        OnPropertyChanged(nameof(SearchEmptyText));
    }

    [RelayCommand]
    private void CloseSearch()
    {
        CancelSearchInput();
        _searchConversation = null;
        _serverSearchResults = [];
        IsSearchOpen = false;
        SelectedSearchResult = null;
        ProjectSearch();
    }

    [RelayCommand]
    private void SelectSearchCategory(SearchCategoryChoice? category)
    {
        if (category is null || category.IsSelected) return;
        CancelSearchInput();
        _hasSubmittedSearch = false;
        foreach (var item in SearchCategories)
        {
            item.IsSelected = ReferenceEquals(item, category);
        }
        _serverSearchResults = [];
        _searchBeforeMessageId = null;
        SelectedSearchResult = null;
        IsSearchBusy = false;
        SearchError = null;
        ProjectSearch();
        OnPropertyChanged(nameof(HasMoreSearchResults));
        OnPropertyChanged(nameof(SearchEmptyText));
        ScheduleServerSearch(SearchQuery);
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task SelectSearchResultAsync(SearchResultItem? result)
    {
        result ??= SelectedSearchResult;
        if (result is null) return;
        if (result.Conversation is not { } conversation ||
            !IsRelayCoveConversation(conversation, _projectedState)) return;
        RestoreConversationToList(conversation);
        var targetMessageId = result.MessageId;
        var opened = targetMessageId is { } openMessageId
            ? await ExecuteSessionActionAsync(() => _session.OpenMessageAsync(conversation, openMessageId))
            : await ActivateConversationFromNavigationAsync(conversation, null, null, null);
        if (opened)
        {
            if (targetMessageId is { } anchorMessageId)
            {
                ProjectLatestStateImmediately();
                QueueScrollToMessage(anchorMessageId);
            }
            SelectedSection = ShellSection.Messages;
            if (IsNarrowLayout) IsConversationListVisibleOnNarrow = false;
            CloseSearch();
        }
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private Task SearchNowAsync() => RunServerSearchAsync(SearchQuery, CancellationToken.None);

    [RelayCommand(IncludeCancelCommand = true, AllowConcurrentExecutions = false)]
    private async Task LoadOlderSearchAsync(CancellationToken cancellationToken)
    {
        var query = SearchQuery.Trim();
        var filter = SelectedSearchFilter;
        if (_searchBeforeMessageId is null ||
            !IsSearchOpen)
        {
            return;
        }
        CancelSearchInput();
        var generation = ++_searchInputGeneration;
        var accountId = _session.AccountId;
        if (accountId is null) return;
        try
        {
            IsSearchBusy = true;
            SearchError = null;
            var page = await _session.SearchMessagesAsync(
                query,
                _searchBeforeMessageId,
                50,
                cancellationToken,
                filter,
                _searchConversation).ConfigureAwait(false);
            if (!IsSearchCurrent(generation, accountId.Value) ||
                !IsSearchOpen ||
                SelectedSearchFilter != filter ||
                !string.Equals(SearchQuery.Trim(), query, StringComparison.Ordinal)) return;
            if (!page.FoundAnchor)
            {
                _searchBeforeMessageId = null;
                SearchError = "搜索结果已变化，请刷新搜索。";
                OnPropertyChanged(nameof(HasMoreSearchResults));
                return;
            }
            var existing = _serverSearchResults.Select(result => result.Id).ToHashSet(StringComparer.Ordinal);
            var older = page.Messages
                .Where(message => IsRelayCoveConversation(message.Conversation, _projectedState))
                .OrderByDescending(message => message.Id)
                .Select(message => ToSearchResult(message, filter))
                .Where(result => existing.Add(result.Id)).ToArray();
            _serverSearchResults = _serverSearchResults.Concat(older).ToArray();
            _searchBeforeMessageId = page.FoundOldest ? null : page.OldestFetchedMessageId;
            ProjectSearch();
        }
        catch (OperationCanceledException)
        {
        }
        catch (GatewayException exception)
        {
            if (IsSearchCurrent(generation, accountId.Value)) SearchError = DescribeGatewayFailure(exception);
        }
        finally
        {
            if (IsSearchCurrent(generation, accountId.Value))
            {
                IsSearchBusy = false;
                OnPropertyChanged(nameof(HasMoreSearchResults));
            }
        }
    }

    [RelayCommand(IncludeCancelCommand = true, AllowConcurrentExecutions = false)]
    private async Task RefreshSavedAsync(CancellationToken cancellationToken) =>
        await StartSavedLoadAsync(replace: true, cancellationToken).ConfigureAwait(false);

    [RelayCommand(IncludeCancelCommand = true, AllowConcurrentExecutions = false)]
    private Task LoadOlderSavedAsync(CancellationToken cancellationToken) =>
        _savedBeforeMessageId is null
            ? Task.CompletedTask
            : StartSavedLoadAsync(replace: false, cancellationToken);

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task OpenSavedMessageAsync(SavedMessageItem? message)
    {
        if (message is null || !IsRelayCoveConversation(message.Conversation, _projectedState)) return;
        RestoreConversationToList(message.Conversation);
        if (await ExecuteSessionActionAsync(() => _session.OpenMessageAsync(message.Conversation, message.MessageId)))
        {
            ProjectLatestStateImmediately();
            SelectedSection = ShellSection.Messages;
            if (IsNarrowLayout) IsConversationListVisibleOnNarrow = false;
            QueueScrollToMessage(message.MessageId);
        }
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task UnstarSavedMessageAsync(SavedMessageItem? message)
    {
        if (message is null) return;
        if (await ExecuteSessionActionAsync(() => _session.SetMessageStarredAsync(message.MessageId, false)))
        {
            SavedMessages.Remove(message);
            SavedRefreshSuggested = false;
            OnPropertyChanged(nameof(HasSavedMessages));
            OnPropertyChanged(nameof(IsSavedEmpty));
        }
    }

    [RelayCommand]
    private void OpenNewConversation()
    {
        CloseTransientOverlays();
        IsNewChannelConversationMode = false;
        IsNewConversationChannelLocked = false;
        NewConversationTopic = string.Empty;
        NewPrivateGroupName = string.Empty;
        NewConversationError = null;
        ClearNewConversationChoices();
        foreach (var user in _projectedState.Users.Values
                     .Where(user => user.IsActive)
                     .OrderBy(user => user.UserId != _session.CurrentUserId)
                     .ThenBy(user => user.FullName, StringComparer.Ordinal)
                     .ThenBy(user => user.UserId))
        {
            var choice = new ConversationContactChoice(
                user.UserId, user.FullName, user.DisplayAvatarUrl, user.IsBot, user.UserId == _session.CurrentUserId);
            choice.PropertyChanged += OnNewConversationChoiceChanged;
            _allNewConversationChoices.Add(choice);
        }
        NewConversationQuery = string.Empty;
        ProjectNewConversationChoices();
        IsNewConversationOpen = true;
    }

    [RelayCommand]
    private void SelectNewDirectConversationContact(ConversationContactChoice? choice)
    {
        if (choice is null || !IsNewDirectConversationMode) return;
        choice.IsSelected = true;
    }

    [RelayCommand]
    private void CloseNewConversation()
    {
        IsNewConversationOpen = false;
        IsNewConversationChannelLocked = false;
        NewConversationQuery = string.Empty;
        NewConversationTopic = string.Empty;
        NewPrivateGroupName = string.Empty;
        NewConversationError = null;
        ClearNewConversationChoices();
    }

    [RelayCommand]
    private void ShowNewDirectConversation()
    {
        IsNewChannelConversationMode = false;
        NewConversationError = null;
        var selected = _allNewConversationChoices.Where(choice => choice.IsSelected).ToArray();
        foreach (var choice in selected.Skip(1)) choice.IsSelected = false;
    }

    [RelayCommand]
    private void ShowNewChannelConversation()
    {
        if (!IsNewConversationOpen) OpenNewConversation();
        if (!CanCreatePrivateGroup)
        {
            NewConversationError = PrivateGroupCreateDisabledReason;
            return;
        }
        IsNewChannelConversationMode = true;
        IsNewConversationChannelLocked = false;
        NewConversationError = null;
    }

    [RelayCommand]
    private void OpenNewChannelTopic()
    {
        OpenNewConversation();
        IsNewChannelConversationMode = true;
        IsNewConversationChannelLocked = false;
        NewConversationChannel = SelectedChannel ?? Channels.FirstOrDefault();
    }

    [RelayCommand]
    private void OpenNewChannelTopicForChannel(ChannelItem? channel)
    {
        if (channel is null || !Channels.Any(item => item.ChannelId == channel.ChannelId)) return;
        OpenNewConversation();
        IsNewChannelConversationMode = true;
        NewConversationChannel = Channels.First(item => item.ChannelId == channel.ChannelId);
        IsNewConversationChannelLocked = true;
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task StartNewChannelConversationAsync()
    {
        var memberIds = _allNewConversationChoices
            .Where(choice => choice.IsSelected && !choice.IsSelf)
            .Select(choice => choice.UserId)
            .OrderBy(static userId => userId)
            .ToArray();
        var name = NewPrivateGroupName.Trim();
        if (!CanStartNewChannelConversation || memberIds.Length < 2 || name.Length == 0) return;
        NewConversationError = null;
        try
        {
            var created = await _session.CreatePrivateGroupAsync(new PrivateGroupCreateOptions(name, memberIds));
            var currentUserId = _session.CurrentUserId;
            _privateGroupMembers[created.ChannelId] = memberIds
                .Prepend(currentUserId ?? 0)
                .Where(id => id > 0)
                .Distinct()
                .OrderBy(static id => id)
                .Select(id => _session.State.Users.GetValueOrDefault(id))
                .Where(static user => user is not null)
                .Cast<UserProfile>()
                .ToArray();
            Project(_session.State);
            var channel = Channels.FirstOrDefault(item => item.ChannelId == created.ChannelId);
            var topicItem = new TopicItem(created.ChannelId, string.Empty, null, isSelected: true);
            if (await ActivateConversationFromNavigationAsync(created.Conversation, channel, topicItem, null))
            {
                SelectedSection = ShellSection.Messages;
                if (IsNarrowLayout) IsConversationListVisibleOnNarrow = false;
                CloseNewConversation();
            }
        }
        catch (GatewayException exception)
        {
            NewConversationError = DescribePrivateGroupCreationFailure(exception);
        }
        catch (InvalidOperationException exception)
        {
            NewConversationError = exception.InnerException is GatewayException gateway
                ? DescribePrivateGroupCreationFailure(gateway)
                : exception.Message == "Refresh the active user directory before creating this group."
                    ? "群聊创建失败：所选成员已停用或不在当前联系人列表中，请刷新联系人后重新选择。"
                    : $"群聊创建未完成：{DescribeInvalidOperation(exception)}";
        }
        catch (ArgumentException)
        {
            NewConversationError = "群聊创建失败：请填写有效群名，并至少选择两名不同的其他活跃成员。";
        }
        catch (OperationCanceledException)
        {
            NewConversationError = "群聊创建已取消；结果不确定时请先刷新列表，不会自动重试。";
        }
        catch
        {
            NewConversationError = "群聊创建失败；结果不确定时请先刷新列表，不会自动重试。";
        }
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task StartNewConversationAsync()
    {
        var userIds = _allNewConversationChoices
            .Where(choice => choice.IsSelected)
            .Select(choice => choice.UserId)
            .OrderBy(userId => userId)
            .ToArray();
        if (userIds.Length != 1) return;
        var conversation = new DirectMessage(userIds.Where(userId => userId != _session.CurrentUserId));
        if (await ActivateConversationFromNavigationAsync(conversation, null, null, null))
        {
            SelectedSection = ShellSection.Messages;
            if (IsNarrowLayout) IsConversationListVisibleOnNarrow = false;
            CloseNewConversation();
        }
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task StartSelfConversationAsync()
    {
        if (await ActivateConversationFromNavigationAsync(new DirectMessage([]), null, null, null))
        {
            SelectedSection = ShellSection.Messages;
            if (IsNarrowLayout) IsConversationListVisibleOnNarrow = false;
            CloseNewConversation();
        }
    }

    [RelayCommand(IncludeCancelCommand = true, AllowConcurrentExecutions = false)]
    private async Task PickAttachmentsAsync(CancellationToken cancellationToken)
    {
        AttachmentError = null;
        var selected = await _fileSelectionService.PickMultipleAsync(cancellationToken);
        AddAttachmentSelection(selected);
    }

    [RelayCommand]
    private void AddDroppedAttachments(IReadOnlyList<SelectedAttachmentFile>? selected)
    {
        IsFileDragActive = false;
        AddAttachmentSelection(selected ?? []);
    }

    [RelayCommand]
    private void AddPastedImage(SelectedAttachmentFile? selected)
    {
        if (selected is null)
        {
            AttachmentError = "无法读取剪贴板中的截图。";
            return;
        }

        AddAttachmentSelection([selected]);
    }

    [RelayCommand(IncludeCancelCommand = true, AllowConcurrentExecutions = false)]
    private Task PasteAttachmentsAsync(
        Func<CancellationToken, Task<IReadOnlyList<SelectedAttachmentFile>>>? readAsync,
        CancellationToken cancellationToken) =>
        ReadAttachmentSelectionAsync(readAsync,
            "无法读取剪贴板附件，请复制文件或截图后重试；暂不支持文件夹。", cancellationToken);

    [RelayCommand(IncludeCancelCommand = true, AllowConcurrentExecutions = false)]
    private Task DropAttachmentsAsync(
        Func<CancellationToken, Task<IReadOnlyList<SelectedAttachmentFile>>>? readAsync,
        CancellationToken cancellationToken)
    {
        IsFileDragActive = false;
        return ReadAttachmentSelectionAsync(readAsync,
            "无法读取拖入的附件，请重新拖入本地文件；暂不支持文件夹。", cancellationToken);
    }

    private async Task ReadAttachmentSelectionAsync(
        Func<CancellationToken, Task<IReadOnlyList<SelectedAttachmentFile>>>? readAsync,
        string failureMessage,
        CancellationToken cancellationToken)
    {
        if (readAsync is null || !CanCompose || _session.AccountId is not { } accountId ||
            _avatarUploadAccountId != accountId ||
            _activeDraftKey is not { } draftKey ||
            _session.SelectedConversation?.CanonicalKey != draftKey) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _lifetimeCancellation.Token, _attachmentUploadsCancellation.Token);
        bool IsCurrent() => !cancellation.IsCancellationRequested && !_disposed && CanCompose &&
            _session.AccountId == accountId && _activeDraftKey == draftKey &&
            _session.SelectedConversation?.CanonicalKey == draftKey;
        AttachmentError = null;
        try
        {
            var selected = await readAsync(cancellation.Token);
            if (IsCurrent()) AddAttachmentSelection(selected);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch
        {
            if (IsCurrent()) AttachmentError = failureMessage;
        }
    }

    private void AddAttachmentSelection(IReadOnlyList<SelectedAttachmentFile> selected)
    {
        if (selected.Count == 0) return;
        var error = ValidateAttachmentSelection(Attachments, selected, _session.MaxFileUploadBytes);
        if (error is not null)
        {
            AttachmentError = error;
            return;
        }
        var added = selected.Select(file => new AttachmentDraftItem(file)).ToArray();
        foreach (var attachment in added) Attachments.Add(attachment);
        SetComposerHeightWithoutPersistence(Math.Max(ComposerHeight, 184d));
        SaveCurrentAttachmentDrafts();
        NotifyAttachmentProperties();
        if (_activeDraftKey is { } key && _session.AccountId is { } accountId)
        {
            _ = UploadAttachmentsAsync(key, accountId, added, _attachmentUploadsCancellation.Token);
        }
    }

    [RelayCommand]
    private void RemoveAttachment(AttachmentDraftItem? attachment)
    {
        if (attachment?.CanRemove != true || !Attachments.Remove(attachment)) return;
        SaveCurrentAttachmentDrafts();
        if (_activeAttachmentUploads.TryGetValue(attachment.Id, out var cancellation)) cancellation.Cancel();
        NotifyAttachmentProperties();
    }

    [RelayCommand]
    private void RetryAttachment(AttachmentDraftItem? attachment)
    {
        if (attachment?.CanRetry != true || _activeDraftKey is not { } key || _session.AccountId is not { } accountId) return;
        attachment.Status = AttachmentUploadStatus.Pending;
        attachment.Uploaded = null;
        AttachmentError = null;
        SaveCurrentAttachmentDrafts();
        NotifyAttachmentProperties();
        _ = UploadAttachmentsAsync(key, accountId, [attachment], _attachmentUploadsCancellation.Token);
    }

    private async Task UploadAttachmentsAsync(
        string draftKey,
        AccountId accountId,
        IReadOnlyList<AttachmentDraftItem> attachments,
        CancellationToken sessionCancellationToken)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token,
            sessionCancellationToken);
        var gateEntered = false;
        try
        {
            await _attachmentUploadGate.WaitAsync(cancellation.Token);
            gateEntered = true;
            foreach (var attachment in attachments)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                if (!IsAttachmentUploadCurrent(draftKey, accountId, attachment) ||
                    attachment.Status != AttachmentUploadStatus.Pending)
                {
                    continue;
                }

                using var fileCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                var fileToken = fileCancellation.Token;
                _activeAttachmentUploads.Add(attachment.Id, fileCancellation);
                try
                {
                    attachment.BeginUpload();
                    NotifyAttachmentPropertiesForDraft(draftKey);
                    fileToken.ThrowIfCancellationRequested();
                    await using var stream = await attachment.File.OpenReadAsync(fileToken);
                    var progress = new InlineProgress<RealmMediaTransferProgress>(value =>
                        _dispatcher.Dispatch(() =>
                        {
                            if (!fileToken.IsCancellationRequested && IsAttachmentUploadCurrent(draftKey, accountId, attachment))
                            {
                                attachment.ReportUploadProgress(value);
                            }
                        }));
                    if (fileToken.IsCancellationRequested || !IsAttachmentUploadCurrent(draftKey, accountId, attachment)) continue;
                    var uploaded = await _session.UploadAttachmentAsync(
                        new AttachmentUpload(
                            attachment.FileName,
                            attachment.File.ContentType,
                            attachment.Length,
                            stream,
                            progress),
                        fileToken);
                    if (fileToken.IsCancellationRequested || !IsAttachmentUploadCurrent(draftKey, accountId, attachment)) continue;
                    attachment.Uploaded = uploaded;
                    attachment.ReportUploadProgress(new RealmMediaTransferProgress(attachment.Length, attachment.Length));
                    attachment.Status = AttachmentUploadStatus.Uploaded;
                }
                catch (GatewayException exception)
                {
                    if (!IsAttachmentUploadCurrent(draftKey, accountId, attachment)) continue;
                    attachment.Status = exception.Kind is GatewayErrorKind.Offline or GatewayErrorKind.Server or GatewayErrorKind.Protocol
                        ? AttachmentUploadStatus.Uncertain
                        : AttachmentUploadStatus.Failed;
                    SetAttachmentErrorForDraft(
                        draftKey,
                        attachment.Status == AttachmentUploadStatus.Uncertain
                            ? "附件上传结果未知；不会自动重试。请确认后显式重试或移除。"
                            : "附件上传失败；请检查限制或权限后显式重试。");
                }
                catch (OperationCanceledException)
                {
                    if (!IsAttachmentUploadCurrent(draftKey, accountId, attachment)) continue;
                    attachment.Status = AttachmentUploadStatus.Uncertain;
                    SetAttachmentErrorForDraft(draftKey, "附件上传已取消且结果未知；不会自动重试。");
                    if (cancellation.IsCancellationRequested) return;
                }
                catch
                {
                    if (!IsAttachmentUploadCurrent(draftKey, accountId, attachment)) continue;
                    attachment.Status = AttachmentUploadStatus.Failed;
                    SetAttachmentErrorForDraft(draftKey, "无法读取或上传附件。");
                }
                finally
                {
                    _activeAttachmentUploads.Remove(attachment.Id);
                    NotifyAttachmentPropertiesForDraft(draftKey);
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            if (gateEntered) _attachmentUploadGate.Release();
        }
    }

    private bool IsAttachmentUploadCurrent(string draftKey, AccountId accountId, AttachmentDraftItem attachment) =>
        _session.AccountId == accountId &&
        _attachmentDrafts.TryGetValue(draftKey, out var draft) &&
        draft.Contains(attachment);

    private void SetAttachmentErrorForDraft(string draftKey, string message)
    {
        if (string.Equals(_activeDraftKey, draftKey, StringComparison.Ordinal)) AttachmentError = message;
    }

    private void NotifyAttachmentPropertiesForDraft(string draftKey)
    {
        if (string.Equals(_activeDraftKey, draftKey, StringComparison.Ordinal)) NotifyAttachmentProperties();
    }

    [RelayCommand]
    private void OpenImageViewer(MessageAttachmentItem? attachment)
    {
        if (attachment?.IsImage != true) return;
        CloseTransientOverlays();
        if (!IsMediaActionBusy) MediaActionStatus = null;
        IsImageViewerOpen = true;
        ActiveImageAttachment = attachment;
    }

    [RelayCommand]
    private void CloseImageViewer()
    {
        IsImageViewerOpen = false;
        ActiveImageAttachment = null;
        if (!IsMediaActionBusy) MediaActionStatus = null;
        MessageActionFocusRequest++;
    }

    private MessageAttachmentItem? _downloadingAttachment;
    private AccountId? _downloadingAttachmentAccount;

    [RelayCommand(IncludeCancelCommand = true, AllowConcurrentExecutions = false)]
    private async Task DownloadAttachmentAsync(
        MessageAttachmentItem? attachment,
        CancellationToken cancellationToken)
    {
        if (attachment is null || IsMediaActionBusy) return;
        if (IsMessageMenuOpen && Equals(attachment, ActiveMessageAttachment)) CloseMessageMenu();
        if (IsImageViewerOpen && Equals(attachment, ActiveImageAttachment)) CloseImageViewer();
        var downloadAccountId = _session.AccountId;
        long downloadedLength = 0;
        CancelMediaStatusClear();
        SetFailedMediaDownload(null);
        MediaDownloadFileName = attachment.Name;
        MediaDownloadProgress = 0;
        HasKnownMediaDownloadLength = false;
        MediaDownloadProgressText = null;
        IsMediaActionBusy = true;
        _downloadingAttachment = attachment;
        _downloadingAttachmentAccount = downloadAccountId;
        RefreshAttachmentTransfer();
        MediaActionStatus = AskWhereToSaveDownloads ? "请选择保存位置…" : "准备下载…";
        try
        {
            var progress = new InlineProgress<RealmMediaTransferProgress>(value =>
                _dispatcher.Dispatch(() =>
                {
                    if (!ReferenceEquals(_downloadingAttachment, attachment) || cancellationToken.IsCancellationRequested) return;
                    UpdateMediaDownloadProgress(value);
                    RefreshAttachmentTransfer();
                }));
            var saved = await _fileSaveService.SaveDownloadAsync(
                attachment.Name,
                async (destination, token) =>
                {
                    MediaActionStatus = "正在下载…";
                    var result = await _realmMediaService.DownloadFileAsync(
                        attachment.SourceUrl,
                        destination,
                        progress,
                        token);
                    downloadedLength = result.Length;
                },
                cancellationToken);
            MediaDownloadProgress = saved.Saved ? 1d : 0d;
            MediaActionStatus = saved.Saved ? $"已保存 {attachment.Name}" : "已取消保存";
            if (saved.Saved && saved.FilePath is { Length: > 0 } filePath && downloadAccountId is { } accountId)
            {
                RecordCompletedDownload(
                    accountId,
                    new DownloadHistoryEntry(
                        Guid.NewGuid(),
                        attachment.Name,
                        filePath,
                        downloadedLength,
                        DateTimeOffset.Now,
                        attachment.AttachmentKey));
                if (_session.AccountId == accountId)
                {
                    attachment.SetDownloaded(DownloadedFileExists(filePath));
                    RefreshAttachmentDownloads();
                }
            }
            ScheduleMediaStatusClear();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            MediaActionStatus = "下载已取消";
            ScheduleMediaStatusClear();
        }
        catch (GatewayException)
        {
            SetFailedMediaDownload(attachment);
            MediaActionStatus = "下载失败；请检查连接、权限或文件限制";
        }
        catch (DirectoryNotFoundException)
        {
            SetFailedMediaDownload(attachment);
            MediaActionStatus = "下载位置不可用，请在设置中重新选择";
        }
        catch
        {
            SetFailedMediaDownload(attachment);
            MediaActionStatus = "无法保存附件；可重新下载";
        }
        finally
        {
            attachment.SetTransfer(false, 0, false);
            _downloadingAttachment = null;
            _downloadingAttachmentAccount = null;
            RefreshAttachmentTransfer();
            IsMediaActionBusy = false;
        }
    }

    private string? FindDownloadedAttachment(MessageAttachmentItem attachment) =>
        _session.AccountId is not null && _session.AccountId == _downloadHistoryAccountId
            ? RecentDownloads.Where(item => item.Entry.AttachmentKey == attachment.AttachmentKey)
                .Select(item => item.FilePath).FirstOrDefault(DownloadedFileExists)
            : null;

    private void RefreshAttachmentDownloads()
    {
        foreach (var attachment in Messages.SelectMany(message => message.Attachments).Where(item => item.IsFile))
            attachment.SetDownloaded(FindDownloadedAttachment(attachment) is not null);
        RefreshAttachmentTransfer();
    }

    private void RefreshAttachmentTransfer()
    {
        _downloadingAttachment?.SetTransfer(true, MediaDownloadProgress, !HasKnownMediaDownloadLength);
        foreach (var item in Messages.SelectMany(message => message.Attachments).Where(item => item.IsFile))
        {
            var active = _session.AccountId == _downloadingAttachmentAccount && item.Equals(_downloadingAttachment);
            if (active || item.IsDownloading)
                item.SetTransfer(active, active ? MediaDownloadProgress : 0, active && !HasKnownMediaDownloadLength);
        }
    }

    [RelayCommand]
    private async Task OpenOrDownloadAttachmentAsync(MessageAttachmentItem? attachment)
    {
        if (attachment is null || IsMediaActionBusy || attachment.IsDownloading) return;
        var path = FindDownloadedAttachment(attachment);
        attachment.SetDownloaded(path is not null);
        if (path is null)
        {
            await DownloadAttachmentCommand.ExecuteAsync(attachment);
            return;
        }
        try
        {
            await _fileSaveService.OpenDownloadedFileAsync(path, _lifetimeCancellation.Token);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested) { }
        catch (FileNotFoundException)
        {
            attachment.SetDownloaded(false);
            MediaActionStatus = "文件已移走或删除，请重新下载";
        }
        catch
        {
            MediaActionStatus = "无法打开文件，请检查关联程序";
        }
    }

    [RelayCommand]
    private void RetryMediaDownload()
    {
        if (_failedMediaDownloadAttachment is { } attachment)
            DownloadAttachmentCommand.Execute(attachment);
    }

    [RelayCommand]
    private void DismissFailedMediaDownload()
    {
        if (IsMediaActionBusy || _failedMediaDownloadAttachment is null) return;
        SetFailedMediaDownload(null);
        MediaActionStatus = null;
        MediaDownloadFileName = null;
        MediaDownloadProgress = 0;
        MediaDownloadProgressText = null;
        HasKnownMediaDownloadLength = false;
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task OpenRecentDownloadAsync(DownloadHistoryItem? item)
    {
        if (item is null) return;
        DownloadCenterStatus = null;
        try
        {
            await _fileSaveService.OpenDownloadedFileAsync(item.FilePath, _lifetimeCancellation.Token);
            IsDownloadCenterOpen = false;
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (FileNotFoundException)
        {
            item.IsMissing = true;
        }
        catch
        {
            DownloadCenterStatus = "无法打开该文件";
        }
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task ShowRecentDownloadInFolderAsync(DownloadHistoryItem? item)
    {
        if (item is null) return;
        DownloadCenterStatus = null;
        try
        {
            await _fileSaveService.ShowDownloadedFileInFolderAsync(item.FilePath, _lifetimeCancellation.Token);
            IsDownloadCenterOpen = false;
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (FileNotFoundException)
        {
            item.IsMissing = true;
        }
        catch
        {
            DownloadCenterStatus = "无法定位该文件";
        }
    }

    [RelayCommand]
    private void RemoveRecentDownload(DownloadHistoryItem? item)
    {
        if (item is null || !RecentDownloads.Remove(item)) return;
        PersistCurrentDownloadHistory();
        NotifyDownloadHistoryProperties();
    }

    [RelayCommand]
    private void ClearDownloadHistory()
    {
        RecentDownloads.Clear();
        PersistCurrentDownloadHistory();
        DownloadCenterStatus = null;
        NotifyDownloadHistoryProperties();
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task ChangeDownloadFolderAsync()
    {
        DownloadSettingsStatus = null;
        try
        {
            if (await _fileSaveService.ChooseDownloadFolderAsync(_lifetimeCancellation.Token))
            {
                OnPropertyChanged(nameof(DownloadFolderPath));
                DownloadSettingsStatus = "下载位置已更新。";
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch
        {
            DownloadSettingsStatus = "无法更改下载位置。";
        }
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task OpenDownloadFolderAsync()
    {
        DownloadSettingsStatus = null;
        try
        {
            await _fileSaveService.OpenDownloadFolderAsync(_lifetimeCancellation.Token);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch
        {
            DownloadSettingsStatus = "下载位置不可用，请重新选择。";
        }
    }

    [RelayCommand]
    private void DismissUnavailableFeature() => UnavailableFeatureMessage = null;

    [RelayCommand]
    private void SelectEmojiCategory(EmojiCategoryChoice? category)
    {
        if (category is null) return;

        foreach (var item in EmojiCategories)
        {
            item.IsSelected = ReferenceEquals(item, category);
        }

        SelectedComposerEmoji = null;
        SelectedReactionEmoji = null;
        _visibleEmojiChoices.ReplaceAll(EmojiCatalog.FilterChoices(EmojiChoices, category.Key));
    }

    [RelayCommand]
    private void ToggleComposerEmojiPicker()
    {
        var open = !IsComposerEmojiPickerOpen;
        CloseTransientOverlays();
        IsComposerEmojiPickerOpen = open;
        SelectedComposerEmoji = null;
    }

    [RelayCommand]
    private void OpenComposerEmojiPickerAt(PopoverAnchorRequest? request)
    {
        if (request is null) return;
        CloseTransientOverlays();
        ComposerEmojiAnchorX = request.X;
        ComposerEmojiAnchorY = request.Y;
        IsComposerEmojiPickerOpen = true;
        SelectedComposerEmoji = null;
    }

    [RelayCommand]
    private void InsertComposerEmoji(EmojiChoice? choice)
    {
        if (choice is null || !EmojiChoices.Contains(choice)) return;
        var start = Math.Clamp(ComposerCursorPosition, 0, ComposerText.Length);
        var selection = Math.Clamp(ComposerSelectionLength, 0, ComposerText.Length - start);
        ComposerText = string.Concat(
            ComposerText.AsSpan(0, start),
            choice.Emoji,
            ComposerText.AsSpan(start + selection));
        ComposerCursorPosition = start + choice.Emoji.Length;
        ComposerSelectionLength = 0;
        IsComposerEmojiPickerOpen = false;
        ComposerFocusRequest++;
    }

    [RelayCommand]
    private void OpenMessageMenu(MessageItem? message)
    {
        if (message?.MessageId is null) return;
        OpenMessageMenuAt(new MessageMenuRequest(message, Math.Max(12d, _viewportWidth - 232d), 68d));
    }

    [RelayCommand]
    private void OpenMessageMenuAt(MessageMenuRequest? request)
    {
        if (request?.Message.MessageId is null ||
            !double.IsFinite(request.AnchorX) || !double.IsFinite(request.AnchorY)) return;
        CloseTransientOverlays();
        ActiveMessageAction = request.Message;
        ActiveMessageAttachment = null;
        _activeMessageSelection = request.SelectedText;
        MessageMenuAnchorX = Math.Max(0d, request.AnchorX);
        MessageMenuAnchorY = Math.Max(0d, request.AnchorY);
        IsMessageMenuOpen = true;
    }

    [RelayCommand]
    private void OpenImageAttachmentMenuAt(ImageAttachmentMenuRequest? request)
    {
        if (request?.Message.MessageId is null || request.Attachment.IsImage != true ||
            !double.IsFinite(request.AnchorX) || !double.IsFinite(request.AnchorY))
        {
            return;
        }

        CloseTransientOverlays();
        ActiveMessageAction = request.Message;
        ActiveMessageAttachment = request.Attachment;
        _activeMessageSelection = null;
        MessageMenuAnchorX = Math.Max(0d, request.AnchorX);
        MessageMenuAnchorY = Math.Max(0d, request.AnchorY);
        IsMessageMenuOpen = true;
    }

    [RelayCommand]
    private void OpenChannelMenuAt(ChannelMenuRequest? request)
    {
        if (request?.Channel is null ||
            !double.IsFinite(request.AnchorX) || !double.IsFinite(request.AnchorY) ||
            Channels.FirstOrDefault(item => item.ChannelId == request.Channel.ChannelId) is not { } channel)
        {
            return;
        }

        CloseTransientOverlays();
        ActiveChannelAction = channel;
        channel.IsActionMenuOpen = true;
        ChannelMenuAnchorX = Math.Max(0d, request.AnchorX);
        ChannelMenuAnchorY = Math.Max(0d, request.AnchorY);
        IsChannelMenuOpen = true;
    }

    [RelayCommand]
    private void CloseChannelMenu() => CloseChannelMenuCore(restoreFocus: true);

    [RelayCommand]
    private void OpenTopicMenuAt(TopicMenuRequest? request)
    {
        if (request?.Topic is null || !double.IsFinite(request.AnchorX) || !double.IsFinite(request.AnchorY) ||
            Topics.FirstOrDefault(item => string.Equals(item.CanonicalKey, request.Topic.CanonicalKey, StringComparison.Ordinal)) is not { } topic) return;
        CloseTransientOverlays();
        ActiveTopicAction = topic;
        topic.IsActionMenuOpen = true;
        TopicMenuAnchorX = Math.Max(0d, request.AnchorX);
        TopicMenuAnchorY = Math.Max(0d, request.AnchorY);
        TopicActionStatus = null;
        IsTopicMenuOpen = true;
        NotifyTopicActionProperties();
    }

    [RelayCommand]
    private void CloseTopicMenu() => CloseTopicMenuCore(restoreFocus: true);

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task SetActiveTopicVisibilityPolicyAsync(string? policyName)
    {
        if (ActiveTopicAction is not { } topic || !CanSetActiveTopicVisibility || !Enum.TryParse<TopicVisibilityPolicy>(policyName, out var policy)) return;
        await ExecuteTopicActionAsync(topic, async () =>
        {
            await _session.SetTopicVisibilityPolicyAsync(new ChannelTopic(topic.ChannelId, topic.Topic), policy);
            topic.VisibilityPolicy = policy;
            CloseTopicMenuCore(restoreFocus: true);
        });
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task MarkActiveTopicReadAsync()
    {
        if (ActiveTopicAction is not { } topic || !CanMarkActiveTopicRead) return;
        await ExecuteTopicActionAsync(topic, async () =>
        {
            await _session.MarkTopicReadAsync(new ChannelTopic(topic.ChannelId, topic.Topic));
            CloseTopicMenuCore(restoreFocus: true);
        });
    }

    [RelayCommand]
    private async Task CopyActiveTopicLinkAsync()
    {
        if (ActiveTopicAction is not { } topic || _session.ActiveRealm is not { } realm) return;
        var channel = _projectedState.Subscriptions.GetValueOrDefault(topic.ChannelId);
        if (channel is null) return;
        try
        {
            await _platformInteractions.CopyTextAsync(TopicPermalink.Build(realm.Uri, topic.ChannelId, channel.Name, topic.Topic, topic.MaxMessageId));
            CloseTopicMenuCore(restoreFocus: true);
            TopicActionStatus = "已复制话题链接。";
        }
        catch { TopicActionStatus = "无法复制话题链接。"; }
    }

    [RelayCommand]
    private void OpenTopicMoveDialog()
    {
        if (!CanMoveActiveTopic) return;
        IsTopicMoveDialogOpen = true;
        TopicMoveDestinationChannel = Channels.FirstOrDefault(item => item.ChannelId == ActiveTopicAction?.ChannelId);
        TopicMoveDestinationName = ActiveTopicAction?.Topic ?? string.Empty;
    }

    [RelayCommand]
    private void CancelTopicMoveDialog() => IsTopicMoveDialogOpen = false;

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task ConfirmTopicMoveAsync()
    {
        if (ActiveTopicAction is not { } topic || !CanConfirmTopicMove || TopicMoveDestinationChannel is not { } destinationChannel) return;
        var destination = new ChannelTopic(destinationChannel.ChannelId, TopicMoveDestinationName.Trim());
        await ExecuteTopicActionAsync(topic, async () =>
        {
            await _session.MoveTopicAsync(new ChannelTopic(topic.ChannelId, topic.Topic), destination);
            IsTopicMoveDialogOpen = false;
            CloseTopicMenuCore(restoreFocus: true);
            if (destination.ChannelId == topic.ChannelId) _lastSelectedTopicByChannel[destination.ChannelId] = destination.Topic;
        });
    }

    [RelayCommand]
    private void RequestActiveTopicResolution()
    {
        if (!CanResolveActiveTopic) return;
        TopicActionStatus = null;
        IsTopicResolutionConfirmationOpen = true;
    }

    [RelayCommand]
    private void CancelTopicResolution() => IsTopicResolutionConfirmationOpen = false;

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task ConfirmTopicResolutionAsync()
    {
        if (ActiveTopicAction is not { } topic || !CanResolveActiveTopic) return;
        var resolved = !topic.IsResolved;
        await ExecuteTopicActionAsync(topic, async () =>
        {
            await _session.SetTopicResolvedAsync(new ChannelTopic(topic.ChannelId, topic.Topic), resolved);
            IsTopicResolutionConfirmationOpen = false;
            CloseTopicMenuCore(restoreFocus: true);
        });
    }

    [RelayCommand]
    private void RequestTopicDelete()
    {
        if (CanDeleteActiveTopic) IsTopicDeleteConfirmationOpen = true;
    }

    [RelayCommand]
    private void CancelTopicDelete() => IsTopicDeleteConfirmationOpen = false;

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task ConfirmTopicDeleteAsync()
    {
        if (ActiveTopicAction is not { } topic || !CanDeleteActiveTopic) return;
        await ExecuteTopicActionAsync(topic, async () =>
        {
            var result = await _session.DeleteTopicAsync(new ChannelTopic(topic.ChannelId, topic.Topic));
            IsTopicDeleteConfirmationOpen = false;
            CloseTopicMenuCore(restoreFocus: true);
            TopicActionStatus = result.Complete ? "话题已删除。" : "话题仅部分删除；请在确认后手动继续，系统不会自动重试。";
            UnavailableFeatureMessage = TopicActionStatus;
        });
    }

    private async Task ExecuteTopicActionAsync(TopicItem target, Func<Task> action)
    {
        if (IsTopicActionBusy) return;
        IsTopicActionBusy = true;
        TopicActionStatus = null;
        NotifyTopicActionProperties();
        try { await action(); }
        catch (OperationCanceledException)
        {
            TopicActionStatus = "话题操作已取消。";
            UnavailableFeatureMessage = TopicActionStatus;
        }
        catch (GatewayException exception)
        {
            TopicActionStatus = $"{DescribeGatewayFailure(exception)} 系统不会自动重试此话题操作。";
            UnavailableFeatureMessage = TopicActionStatus;
        }
        catch (InvalidOperationException exception)
        {
            TopicActionStatus = exception.Message.Contains("anchor", StringComparison.OrdinalIgnoreCase)
                ? "该话题中没有可操作的消息。"
                : DescribeInvalidOperation(exception);
            UnavailableFeatureMessage = TopicActionStatus;
        }
        catch
        {
            TopicActionStatus = "话题操作失败；不会自动重试。";
            UnavailableFeatureMessage = TopicActionStatus;
        }
        finally { IsTopicActionBusy = false; NotifyTopicActionProperties(); }
    }

    [RelayCommand]
    private async Task CopyActiveChannelLinkAsync()
    {
        if (ActiveChannelAction is not { } channel || _session.ActiveRealm is not { } realm) return;
        var link = new Uri(realm.Uri, $"#narrow/channel/{channel.ChannelId}-{Uri.EscapeDataString(channel.Name)}").AbsoluteUri;
        try
        {
            await _platformInteractions.CopyTextAsync(link);
            CloseChannelMenuCore(restoreFocus: false);
            UnavailableFeatureMessage = "已复制频道链接。";
        }
        catch
        {
            LoginError = "无法复制频道链接。";
        }
    }

    [RelayCommand]
    private void OpenActiveChannelTopicList()
    {
        if (ActiveChannelAction is not { } channel) return;
        CloseChannelMenuCore(restoreFocus: false);
        SetExpandedChannel(channel.ChannelId);
        _ = ActivateChannelAsync(channel);
    }

    [RelayCommand]
    private void ExplainActiveChannelFeature(string? feature)
    {
        if (ActiveChannelAction is null || string.IsNullOrWhiteSpace(feature)) return;
        CloseChannelMenuCore(restoreFocus: false);
        UnavailableFeatureMessage = $"{feature}尚未接通频道级协议；未执行任何 Realm 操作。";
    }

    [RelayCommand]
    private void CloseMessageMenu()
    {
        IsMessageMenuOpen = false;
        ActiveMessageAction = null;
        ActiveMessageAttachment = null;
        _activeMessageSelection = null;
        MessageActionFocusRequest++;
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task CopyActiveImageAsync()
    {
        if (ActiveMessageAttachment is not { IsImage: true } image) return;
        var account = _session.AccountId;
        CloseMessageMenu();
        try
        {
            var result = await _realmMediaService.GetFileAsync(image.SourceUrl, _lifetimeCancellation.Token);
            if (_disposed || _session.AccountId != account) return;
            await _platformInteractions.CopyImageAsync(result.Content, _lifetimeCancellation.Token);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested) { }
        catch
        {
            if (!_disposed && _session.AccountId == account)
                MediaActionStatus = "无法复制图片，请重试";
        }
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task CopyActiveMessageAsync()
    {
        var message = ActiveMessageAction;
        if (!CanCopyActiveMessage || message is null) return;

        var text = string.IsNullOrEmpty(_activeMessageSelection)
            ? message.Body
            : _activeMessageSelection;
        CloseMessageMenu();
        try
        {
            await _platformInteractions.CopyTextAsync(text).ConfigureAwait(false);
        }
        catch
        {
            // Clipboard availability must not leave a stale menu or success banner.
        }
    }

    [RelayCommand]
    private void QuoteMessage(MessageItem? message)
    {
        message ??= ActiveMessageAction;
        if (message?.MessageId is null) return;
        var quote = MessageQuote.Build(message);
        ComposerText = string.IsNullOrWhiteSpace(ComposerText)
            ? quote
            : $"{ComposerText}\n\n{quote}";
        ComposerCursorPosition = ComposerText.Length;
        ComposerSelectionLength = 0;
        CloseMessageMenu();
        ComposerFocusRequest++;
    }

    [RelayCommand]
    private void OpenReactionPicker(MessageItem? message)
    {
        message ??= ActiveMessageAction;
        if (message?.CanMutate != true || !CanCompose) return;
        var anchorX = IsMessageMenuOpen
            ? MessageMenuAnchorX
            : Math.Max(12d, _viewportWidth - 322d);
        var anchorY = IsMessageMenuOpen ? MessageMenuAnchorY : 68d;
        OpenReactionPickerAt(new ReactionPickerRequest(message, anchorX, anchorY));
    }

    [RelayCommand]
    private void OpenReactionPickerAt(ReactionPickerRequest? request)
    {
        if (request?.Message.CanMutate != true ||
            !CanCompose ||
            !double.IsFinite(request.AnchorX) ||
            !double.IsFinite(request.AnchorY))
        {
            return;
        }

        CloseTransientOverlays();
        ActiveMessageAction = request.Message;
        ReactionPickerAnchorX = Math.Max(0d, request.AnchorX);
        ReactionPickerAnchorY = Math.Max(0d, request.AnchorY);
        IsReactionPickerOpen = true;
        SelectedReactionEmoji = null;
    }

    [RelayCommand]
    private void CloseReactionPicker()
    {
        IsReactionPickerOpen = false;
        SelectedReactionEmoji = null;
        ActiveMessageAction = null;
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task SelectReactionEmojiAsync(EmojiChoice? choice)
    {
        var message = ActiveMessageAction;
        if (choice is null || !EmojiChoices.Contains(choice) || message?.MessageId is not { } messageId) return;
        var existing = message.Reactions.FirstOrDefault(reaction =>
            string.Equals(reaction.Identity.CanonicalKey, choice.Identity.CanonicalKey, StringComparison.Ordinal));
        var add = existing?.CurrentUserReacted != true;
        if (await ExecuteSessionActionAsync(() => _session.SetReactionAsync(messageId, choice.Identity, add)))
        {
            CloseReactionPicker();
        }
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private Task ToggleReactionAsync(ReactionItem? reaction)
    {
        if (reaction is null) return Task.CompletedTask;
        return ExecuteSessionActionAsync(() => _session.SetReactionAsync(
            reaction.MessageId,
            reaction.Identity,
            !reaction.CurrentUserReacted));
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private Task ToggleMessageStarAsync(MessageItem? message)
    {
        message ??= ActiveMessageAction;
        if (message?.MessageId is not { } messageId) return Task.CompletedTask;
        var isStarred = !message.IsStarred;
        CloseMessageMenu();
        return ExecuteSessionActionAsync(() => _session.SetMessageStarredAsync(messageId, isStarred));
    }

    [RelayCommand]
    private void OpenEditDialog(MessageItem? message)
    {
        message ??= ActiveMessageAction;
        if (message is null || !CanChangeMessage(message, delete: false)) return;
        CloseTransientOverlays();
        ActiveMessageAction = message;
        EditMessageText = message.Content;
        IsEditDialogOpen = true;
    }

    [RelayCommand]
    private void CancelEditDialog()
    {
        IsEditDialogOpen = false;
        EditMessageText = string.Empty;
        ActiveMessageAction = null;
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task ConfirmEditMessageAsync()
    {
        if (ActiveMessageAction?.MessageId is not { } messageId || string.IsNullOrWhiteSpace(EditMessageText)) return;
        if (!CanEditActiveMessage)
        {
            LoginError = "这条消息已超过编辑时限或当前没有编辑权限。";
            return;
        }
        if (await ExecuteSessionActionAsync(() => _session.EditMessageAsync(messageId, EditMessageText)))
        {
            CancelEditDialog();
        }
    }

    [RelayCommand]
    private void RequestDeleteMessage(MessageItem? message)
    {
        message ??= ActiveMessageAction;
        if (!CanChangeMessage(message, delete: true)) return;
        CloseTransientOverlays();
        ActiveMessageAction = message;
        IsDeleteConfirmationOpen = true;
    }

    [RelayCommand]
    private void CancelDeleteMessage()
    {
        IsDeleteConfirmationOpen = false;
        ActiveMessageAction = null;
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task ConfirmDeleteMessageAsync()
    {
        if (ActiveMessageAction?.MessageId is not { } messageId) return;
        if (!CanDeleteActiveMessage)
        {
            CancelDeleteMessage();
            LoginError = "这条消息已超过删除时限或当前没有删除权限。";
            return;
        }
        if (await ExecuteSessionActionAsync(() => _session.DeleteMessageAsync(messageId)))
        {
            CancelDeleteMessage();
        }
    }

    [RelayCommand]
    private void RequestChannelUnsubscribe()
    {
        if (_session.SelectedConversation is not ChannelTopic selected ||
            !_projectedState.Subscriptions.TryGetValue(selected.ChannelId, out var subscription) ||
            !CanUnsubscribeSelectedChannel)
        {
            return;
        }
        if (IsCurrentUserPrivateGroupOwner)
        {
            PrivateGroupActionStatus = "群主退出前必须先转让群主，或使用“解散群聊”。";
            return;
        }
        if (!CanExitPrivateGroup) return;
        RequestChannelUnsubscribeCore(selected.ChannelId, subscription.Name);
    }

    [RelayCommand]
    private void RequestActiveChannelUnsubscribe()
    {
        if (ActiveChannelAction is not { } channel ||
            !_projectedState.Subscriptions.TryGetValue(channel.ChannelId, out var subscription) ||
            !CanManageActiveChannel)
        {
            return;
        }
        if (PrivateGroupPolicy.IsEligible(subscription) && IsCurrentUserPrivateGroupOwner)
        {
            CloseChannelMenuCore(restoreFocus: false);
            PrivateGroupActionStatus = "群主退出前必须先转让群主，或使用“解散群聊”。";
            return;
        }

        CloseChannelMenuCore(restoreFocus: false);
        RequestChannelUnsubscribeCore(channel.ChannelId, subscription.Name);
    }

    [RelayCommand]
    private void CancelChannelUnsubscribe()
    {
        if (IsChannelUnsubscribeBusy) return;
        IsChannelUnsubscribeConfirmationOpen = false;
        _channelUnsubscribeTargetId = null;
        ChannelUnsubscribeTargetName = string.Empty;
        ChannelUnsubscribeError = null;
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task ConfirmChannelUnsubscribeAsync()
    {
        if (_channelUnsubscribeTargetId is not { } channelId || IsChannelUnsubscribeBusy) return;
        IsChannelUnsubscribeBusy = true;
        ChannelUnsubscribeError = null;
        try
        {
            await _session.UnsubscribeChannelAsync(channelId);
            IsChannelUnsubscribeConfirmationOpen = false;
            _channelUnsubscribeTargetId = null;
            ChannelUnsubscribeTargetName = string.Empty;
            IsDetailsOpen = false;
        }
        catch (GatewayException exception)
        {
            ChannelUnsubscribeError = DescribeGatewayFailure(exception);
        }
        catch (OperationCanceledException)
        {
            ChannelUnsubscribeError = "退出频道未完成，请确认连接状态。";
        }
        catch (InvalidOperationException exception)
        {
            ChannelUnsubscribeError = DescribeInvalidOperation(exception);
        }
        catch (Exception)
        {
            ChannelUnsubscribeError = "退出频道失败，请稍后重试。";
        }
        finally
        {
            IsChannelUnsubscribeBusy = false;
            Project(_session.State);
        }
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task OpenChannelBrowserAsync()
    {
        CloseTransientOverlays();
        IsChannelBrowserOpen = true;
        IsChannelBrowserLoading = true;
        ChannelBrowserError = null;
        var generation = ++_channelBrowserGeneration;
        var accountId = _session.AccountId;
        _channelBrowserAccountId = accountId;
        _channelBrowserCancellation?.Cancel();
        _channelBrowserCancellation?.Dispose();
        var browserCancellation = _channelBrowserCancellation = new CancellationTokenSource();
        try
        {
            var subscribed = _projectedState.Subscriptions.Keys;
            var channels = await _session.GetAvailableChannelsAsync(browserCancellation.Token);
            if (!IsChannelBrowserCurrent(generation, accountId, browserCancellation)) return;
            Reconcile(AvailableChannels,
                channels.Where(channel => !channel.IsArchived && !subscribed.Contains(channel.ChannelId))
                    .OrderBy(channel => channel.Name, StringComparer.Ordinal)
                    .Select(channel => new AvailableChannelItem(channel.ChannelId, channel.Name, channel.Description, channel.SubscriberCount)),
                channel => channel.ChannelId);
        }
        catch (GatewayException exception)
        {
            if (IsChannelBrowserCurrent(generation, accountId, browserCancellation)) ChannelBrowserError = DescribeGatewayFailure(exception);
        }
        catch (OperationCanceledException)
        {
            if (IsChannelBrowserCurrent(generation, accountId, browserCancellation)) ChannelBrowserError = "加载频道已取消，请重试。";
        }
        catch (Exception)
        {
            if (IsChannelBrowserCurrent(generation, accountId, browserCancellation)) ChannelBrowserError = "无法加载可加入频道，请确认连接后重试。";
        }
        finally
        {
            if (IsChannelBrowserCurrent(generation, accountId, browserCancellation)) IsChannelBrowserLoading = false;
        }
    }

    [RelayCommand]
    private void CloseChannelBrowser()
    {
        _channelBrowserCancellation?.Cancel();
        _channelBrowserCancellation?.Dispose();
        _channelBrowserCancellation = null;
        _channelBrowserGeneration++;
        _channelBrowserAccountId = null;
        ChannelBrowserError = null;
        IsChannelBrowserLoading = false;
        IsChannelBrowserOpen = false;
        Reconcile(AvailableChannels, [], item => item.ChannelId);
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task JoinAvailableChannelAsync(AvailableChannelItem? channel)
    {
        if (channel is null || IsChannelBrowserLoading) return;
        IsChannelBrowserLoading = true;
        ChannelBrowserError = null;
        var generation = _channelBrowserGeneration;
        var accountId = _channelBrowserAccountId;
        try
        {
            await _session.SubscribeToChannelAsync(channel.ChannelId);
            if (!IsChannelBrowserCurrent(generation, accountId)) return;
            AvailableChannels.Remove(channel);
            Project(_session.State);
        }
        catch (GatewayException exception)
        {
            if (IsChannelBrowserCurrent(generation, accountId)) ChannelBrowserError = DescribeGatewayFailure(exception);
        }
        catch (InvalidOperationException exception)
        {
            if (IsChannelBrowserCurrent(generation, accountId)) ChannelBrowserError = DescribeInvalidOperation(exception);
        }
        catch (OperationCanceledException)
        {
            if (IsChannelBrowserCurrent(generation, accountId)) ChannelBrowserError = "加入频道未完成；结果未知时不会自动重试。";
        }
        catch (Exception)
        {
            if (IsChannelBrowserCurrent(generation, accountId)) ChannelBrowserError = "加入频道失败；结果未知时不会自动重试。";
        }
        finally
        {
            if (IsChannelBrowserCurrent(generation, accountId)) IsChannelBrowserLoading = false;
        }
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task ToggleSelectedChannelMutedAsync()
    {
        await SetSelectedChannelPreferenceAsync(SubscriptionPreference.Muted);
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task ToggleSelectedChannelPinnedAsync()
    {
        await SetSelectedChannelPreferenceAsync(SubscriptionPreference.Pinned);
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task ToggleActiveChannelMutedAsync()
    {
        if (ActiveChannelAction is not { } channel || !CanManageActiveChannel) return;
        CloseChannelMenuCore(restoreFocus: false);
        await SetChannelPreferenceAsync(channel.ChannelId, SubscriptionPreference.Muted);
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task ToggleActiveChannelPinnedAsync()
    {
        if (ActiveChannelAction is not { } channel || !CanManageActiveChannel) return;
        CloseChannelMenuCore(restoreFocus: false);
        await SetChannelPreferenceAsync(channel.ChannelId, SubscriptionPreference.Pinned);
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task OpenActiveChannelSettingsAsync()
    {
        if (ActiveChannelAction is not { } channel) return;
        CloseChannelMenuCore(restoreFocus: false);
        await ChannelSettings.OpenAsync(channel.ChannelId);
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task OpenCreateChannelSettingsAsync()
    {
        CloseTransientOverlays();
        var channelId = (_session.SelectedConversation as ChannelTopic)?.ChannelId ?? Channels.FirstOrDefault()?.ChannelId;
        await ChannelSettings.OpenCreateAsync(channelId);
    }

    private async Task SetSelectedChannelPreferenceAsync(SubscriptionPreference preference)
    {
        if (_session.SelectedConversation is not ChannelTopic selected ||
            !_projectedState.Subscriptions.ContainsKey(selected.ChannelId)) return;
        await SetChannelPreferenceAsync(selected.ChannelId, preference);
    }

    private async Task SetChannelPreferenceAsync(long channelId, SubscriptionPreference preference)
    {
        if (!_projectedState.Subscriptions.TryGetValue(channelId, out var subscription)) return;
        var value = preference == SubscriptionPreference.Muted ? !subscription.IsMuted : !subscription.IsPinned;
        await ExecuteSessionActionAsync(() => _session.SetSubscriptionPreferenceAsync(channelId, preference, value));
    }

    private void RequestChannelUnsubscribeCore(long channelId, string channelName)
    {
        CloseTransientOverlays();
        _channelUnsubscribeTargetId = channelId;
        ChannelUnsubscribeTargetName = channelName;
        ChannelUnsubscribeError = null;
        IsChannelUnsubscribeConfirmationOpen = true;
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ToggleDetailsAsync()
    {
        if (!CanOpenConversationSettings) return;
        if (IsDetailsOpen)
        {
            CloseDetailsCore();
            return;
        }

        IsDetailsContentReady = false;
        IsDetailsLoading = true;
        IsDetailsOpen = true;
        await LoadConversationSettingsAsync();
    }

    [RelayCommand]
    private void SaveDetailsRemark()
    {
        if (!TryGetConversationPreferenceTarget(out var accountId, out var preferenceKey)) return;
        var current = _conversationPreferencesStore.Get(accountId, preferenceKey);
        _conversationPreferencesStore.Save(accountId, preferenceKey, current with { Remark = DetailsRemark });
        DetailsRemark = _conversationPreferencesStore.Get(accountId, preferenceKey).Remark ?? string.Empty;
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task SavePrivateGroupNameAsync()
    {
        if (!TryGetSelectedPrivateGroup(out var group) || !CanManagePrivateGroup) return;
        var name = EditablePrivateGroupName.Trim();
        if (name.Length == 0)
        {
            PrivateGroupActionStatus = "群聊名称不能为空。";
            return;
        }

        var expectedKey = group.CanonicalKey;
        IsPrivateGroupActionBusy = true;
        PrivateGroupActionStatus = null;
        try
        {
            await _session.UpdateChannelAsync(group.ChannelId, name, null, null);
            if (!IsSelectedConversation(expectedKey)) return;
            DetailsChannelName = name;
            EditablePrivateGroupName = name;
            PrivateGroupActionStatus = "群聊名称已更新。";
        }
        catch (Exception exception)
        {
            if (IsSelectedConversation(expectedKey)) PrivateGroupActionStatus = DescribePrivateGroupActionFailure("更新群聊名称", exception);
        }
        finally
        {
            IsPrivateGroupActionBusy = false;
        }
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task SavePrivateGroupAnnouncementAsync()
    {
        if (!TryGetSelectedPrivateGroup(out var group) || !CanManagePrivateGroup) return;
        var announcement = EditablePrivateGroupAnnouncement.Trim();
        var expectedKey = group.CanonicalKey;
        IsPrivateGroupActionBusy = true;
        PrivateGroupActionStatus = null;
        try
        {
            await _session.UpdateChannelAsync(group.ChannelId, null, announcement, null);
            if (!IsSelectedConversation(expectedKey)) return;
            DetailsChannelAnnouncement = announcement.Length == 0 ? "暂无群公告" : announcement;
            EditablePrivateGroupAnnouncement = announcement;
            PrivateGroupActionStatus = "群公告已更新。";
        }
        catch (Exception exception)
        {
            if (IsSelectedConversation(expectedKey)) PrivateGroupActionStatus = DescribePrivateGroupActionFailure("更新群公告", exception);
        }
        finally
        {
            IsPrivateGroupActionBusy = false;
        }
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task InvitePrivateGroupMemberAsync()
    {
        if (!TryGetSelectedPrivateGroup(out var group) || !CanInvitePrivateGroupMember || SelectedGroupInviteCandidate is not { } target) return;
        var expectedKey = group.CanonicalKey;
        IsPrivateGroupActionBusy = true;
        PrivateGroupActionStatus = null;
        try
        {
            await _session.AddChannelMembersAsync(group.ChannelId, [target.UserId], false);
            if (!IsSelectedConversation(expectedKey)) return;
            await LoadConversationSettingsAsync();
            if (IsSelectedConversation(expectedKey)) PrivateGroupActionStatus = $"已邀请 {target.Name}。";
        }
        catch (Exception exception)
        {
            if (IsSelectedConversation(expectedKey)) PrivateGroupActionStatus = DescribePrivateGroupActionFailure("邀请成员", exception);
        }
        finally
        {
            IsPrivateGroupActionBusy = false;
        }
    }

    [RelayCommand]
    private void RequestRemovePrivateGroupMember()
    {
        if (CanRemovePrivateGroupMember) IsGroupRemoveConfirmationVisible = true;
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task ConfirmRemovePrivateGroupMemberAsync()
    {
        if (!IsGroupRemoveConfirmationVisible || !TryGetSelectedPrivateGroup(out var group) ||
            !CanRemovePrivateGroupMember || SelectedGroupRemoveCandidate is not { } target) return;
        var expectedKey = group.CanonicalKey;
        IsGroupRemoveConfirmationVisible = false;
        IsPrivateGroupActionBusy = true;
        PrivateGroupActionStatus = null;
        try
        {
            await _session.RemoveChannelMembersAsync(group.ChannelId, [target.UserId]);
            if (!IsSelectedConversation(expectedKey)) return;
            await LoadConversationSettingsAsync();
            if (IsSelectedConversation(expectedKey)) PrivateGroupActionStatus = $"已移除 {target.Name}。";
        }
        catch (Exception exception)
        {
            if (IsSelectedConversation(expectedKey)) PrivateGroupActionStatus = DescribePrivateGroupActionFailure("移除成员", exception);
        }
        finally
        {
            IsPrivateGroupActionBusy = false;
        }
    }

    [RelayCommand]
    private void RequestTransferPrivateGroupOwnership()
    {
        if (CanTransferPrivateGroupOwnership) IsGroupTransferConfirmationVisible = true;
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task ConfirmTransferPrivateGroupOwnershipAsync()
    {
        if (!IsGroupTransferConfirmationVisible || !TryGetSelectedPrivateGroup(out var group) ||
            !CanTransferPrivateGroupOwnership || SelectedGroupTransferCandidate is not { } target) return;
        var expectedKey = group.CanonicalKey;
        IsGroupTransferConfirmationVisible = false;
        IsPrivateGroupActionBusy = true;
        PrivateGroupActionStatus = null;
        try
        {
            var result = await _session.TransferPrivateGroupOwnershipAsync(group.ChannelId, target.UserId);
            if (result.PreviousOwnerExited)
            {
                CloseDetailsCore();
                UnavailableFeatureMessage = result.Status;
                return;
            }
            if (!IsSelectedConversation(expectedKey)) return;
            await LoadConversationSettingsAsync();
            if (IsSelectedConversation(expectedKey)) PrivateGroupActionStatus = result.Status;
        }
        catch (Exception exception)
        {
            if (IsSelectedConversation(expectedKey)) PrivateGroupActionStatus = DescribePrivateGroupActionFailure("转让群主", exception);
        }
        finally
        {
            IsPrivateGroupActionBusy = false;
        }
    }

    [RelayCommand]
    private void RequestDissolvePrivateGroup()
    {
        if (CanManagePrivateGroup) IsGroupDissolveConfirmationVisible = true;
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task ConfirmDissolvePrivateGroupAsync()
    {
        if (!IsGroupDissolveConfirmationVisible || !TryGetSelectedPrivateGroup(out var group) || !CanManagePrivateGroup) return;
        var expectedKey = group.CanonicalKey;
        IsGroupDissolveConfirmationVisible = false;
        IsPrivateGroupActionBusy = true;
        PrivateGroupActionStatus = null;
        try
        {
            var result = await _session.DissolvePrivateGroupAsync(group.ChannelId);
            if (result.OwnerExited)
            {
                CloseDetailsCore();
                UnavailableFeatureMessage = result.Status;
                return;
            }
            if (!IsSelectedConversation(expectedKey)) return;
            await LoadConversationSettingsAsync();
            if (IsSelectedConversation(expectedKey)) PrivateGroupActionStatus = result.Status;
        }
        catch (Exception exception)
        {
            if (IsSelectedConversation(expectedKey)) PrivateGroupActionStatus = DescribePrivateGroupActionFailure("解散群聊", exception);
        }
        finally
        {
            IsPrivateGroupActionBusy = false;
        }
    }

    [RelayCommand]
    private void CancelPrivateGroupConfirmation()
    {
        if (IsPrivateGroupActionBusy) return;
        IsGroupRemoveConfirmationVisible = false;
        IsGroupTransferConfirmationVisible = false;
        IsGroupDissolveConfirmationVisible = false;
    }

    internal ConversationMenuTarget? CreateConversationMenuTarget(ConversationListItem item) =>
        _session.AccountId is { } accountId && _avatarUploadAccountId == accountId &&
        (Conversations.Contains(item) || FilteredConversations.Contains(item)) && IsRelayCoveConversation(item.Conversation, _projectedState)
            ? new(accountId, item.Conversation, item.IsPinned, item.IsMuted)
            : null;

    private bool CanDeleteConversation(ConversationMenuTarget? target) =>
        !IsConversationMenuActionBusy && target is not null && _session.AccountId == target.AccountId &&
        IsRelayCoveConversation(target.Conversation, _projectedState);

    private bool CanSetConversationPreference(ConversationMenuTarget? target) => CanDeleteConversation(target) &&
        (target!.Conversation is DirectMessage || _projectedState.Connection.Status == RelayCove.Core.ConnectionStatus.Connected);

    [RelayCommand(CanExecute = nameof(CanSetConversationPreference))]
    private Task ToggleConversationPinnedAsync(ConversationMenuTarget target) =>
        SetConversationPreferenceAsync(target, SubscriptionPreference.Pinned, !target.IsPinned);

    [RelayCommand(CanExecute = nameof(CanSetConversationPreference))]
    private Task ToggleConversationMutedAsync(ConversationMenuTarget target) =>
        SetConversationPreferenceAsync(target, SubscriptionPreference.Muted, !target.IsMuted);

    private async Task SetConversationPreferenceAsync(ConversationMenuTarget target, SubscriptionPreference setting, bool value)
    {
        if (!CanSetConversationPreference(target)) return;
        IsConversationMenuActionBusy = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        _conversationMenuActionCancellation = cancellation;
        try
        {
            if (target.Conversation is ChannelTopic group)
                await _session.SetSubscriptionPreferenceAsync(target.AccountId, group.ChannelId, setting, value, cancellation.Token);
            else
            {
                var current = _conversationPreferencesStore.Get(target.AccountId, target.Conversation.CanonicalKey);
                _conversationPreferencesStore.Save(target.AccountId, target.Conversation.CanonicalKey,
                    setting == SubscriptionPreference.Pinned ? current with { IsPinned = value } : current with { IsMuted = value });
            }
            if (!cancellation.IsCancellationRequested && _session.AccountId == target.AccountId)
            {
                ProjectLatestStateImmediately();
                ProjectConversationPreference();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (!cancellation.IsCancellationRequested && _session.AccountId == target.AccountId)
                LoginError = exception is GatewayException gateway ? DescribeGatewayFailure(gateway) : "未能更新聊天设置，请稍后重试。";
        }
        finally
        {
            _conversationMenuActionCancellation = null;
            if (!_disposed) IsConversationMenuActionBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanDeleteConversation))]
    private async Task DeleteConversationAsync(ConversationMenuTarget target)
    {
        if (!CanDeleteConversation(target)) return;
        IsConversationMenuActionBusy = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        _conversationMenuActionCancellation = cancellation;
        try
        {
            var key = target.Conversation.CanonicalKey;
            var latestMessageId = GetLatestConversationMessageId(_session.State, target.Conversation);
            if (_navigationConversationKey == key) CancelNavigation();
            if (_session.SelectedConversation == target.Conversation)
            {
                CancelNavigation();
                await _session.CloseConversationAsync(target.AccountId, target.Conversation, cancellation.Token);
            }
            if (cancellation.IsCancellationRequested || _session.AccountId != target.AccountId) return;
            var current = _conversationPreferencesStore.Get(target.AccountId, key);
            _conversationPreferencesStore.Save(target.AccountId, key, current with { HiddenThroughMessageId = latestMessageId });
            ProjectLatestStateImmediately();
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (!cancellation.IsCancellationRequested && _session.AccountId == target.AccountId)
                LoginError = "未能从列表移除聊天，请稍后重试。";
        }
        finally
        {
            _conversationMenuActionCancellation = null;
            if (!_disposed) IsConversationMenuActionBusy = false;
        }
    }

    private static long GetLatestConversationMessageId(ClientState state, ConversationKey conversation) =>
        Math.Max(state.ConversationSummaries.GetValueOrDefault(conversation.CanonicalKey)?.LatestMessage.Id ?? 0,
            state.Messages.Values.Where(message => message.Conversation == conversation).Select(message => message.Id).DefaultIfEmpty().Max());

    private bool IsConversationHidden(ClientState state, ConversationKey conversation)
    {
        if (_session.AccountId is not { } accountId) return false;
        var preference = _conversationPreferencesStore.Get(accountId, conversation.CanonicalKey);
        if (preference.HiddenThroughMessageId is not { } hiddenThrough) return false;
        if (GetLatestConversationMessageId(state, conversation) <= hiddenThrough) return true;
        _conversationPreferencesStore.Save(accountId, conversation.CanonicalKey, preference with { HiddenThroughMessageId = null });
        return false;
    }

    private void RestoreConversationToList(ConversationKey conversation)
    {
        if (_session.AccountId is not { } accountId) return;
        var preference = _conversationPreferencesStore.Get(accountId, conversation.CanonicalKey);
        if (preference.HiddenThroughMessageId is not null)
            _conversationPreferencesStore.Save(accountId, conversation.CanonicalKey, preference with { HiddenThroughMessageId = null });
    }

    [RelayCommand]
    private void ToggleDirectMessageMuted()
    {
        if (_session.SelectedConversation is not DirectMessage ||
            !TryGetConversationPreferenceTarget(out var accountId, out var preferenceKey)) return;
        var current = _conversationPreferencesStore.Get(accountId, preferenceKey);
        _conversationPreferencesStore.Save(accountId, preferenceKey, current with { IsMuted = !current.IsMuted });
        ProjectConversationPreference();
    }

    [RelayCommand]
    private void ToggleDirectMessagePinned()
    {
        if (_session.SelectedConversation is not DirectMessage ||
            !TryGetConversationPreferenceTarget(out var accountId, out var preferenceKey)) return;
        var current = _conversationPreferencesStore.Get(accountId, preferenceKey);
        _conversationPreferencesStore.Save(accountId, preferenceKey, current with { IsPinned = !current.IsPinned });
        ProjectConversationPreference();
        Project(_projectedState);
    }

    [RelayCommand]
    private void RequestClearConversationCache()
    {
        if (HasSelectedConversation) ClearConversationCacheConfirmationVisible = true;
    }

    [RelayCommand]
    private void CancelClearConversationCache() => ClearConversationCacheConfirmationVisible = false;

    [RelayCommand(IncludeCancelCommand = true, AllowConcurrentExecutions = false)]
    private async Task ConfirmClearConversationCacheAsync(CancellationToken cancellationToken)
    {
        if (!ClearConversationCacheConfirmationVisible ||
            _session.SelectedConversation is not { } selected ||
            IsClearConversationCacheBusy) return;
        IsClearConversationCacheBusy = true;
        try
        {
            if (await ExecuteSessionActionAsync(
                    () => _session.ClearConversationCacheAsync(selected, cancellationToken),
                    "无法清除当前会话的本机缓存；没有删除任何服务器消息。"))
            {
                ClearConversationCacheConfirmationVisible = false;
            }
        }
        finally
        {
            IsClearConversationCacheBusy = false;
        }
    }

    private async Task LoadConversationSettingsAsync()
    {
        CancelDetailsLoad();
        if (_session.SelectedConversation is not { } selected) return;
        var expectedKey = selected.CanonicalKey;
        var expectedAccountId = _session.AccountId;
        var generation = ++_detailsLoadGeneration;
        var cancellation = _detailsLoadCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        DetailsLoadError = null;
        IsDetailsContentReady = false;
        IsDetailsLoading = true;
        IsPrivateGroupAuthorityLoaded = false;
        try
        {
            await _dispatcher.YieldToRenderAsync(cancellation.Token);
            if (!IsDetailsLoadCurrent(generation, cancellation, expectedKey, expectedAccountId)) return;
            DetailsMembers.Clear();
            ResetPrivateGroupSettings();
            NotifyDetailsMembersChanged();
            ProjectConversationPreference();
            ProjectDirectMessageAvatar(selected);
            if (selected is not ChannelTopic channel)
            {
                IsDetailsContentReady = true;
                return;
            }

            DetailsChannelName = _projectedState.Subscriptions.GetValueOrDefault(channel.ChannelId)?.Name ?? ConversationSubtitle;
            DetailsChannelAnnouncement = "正在加载群公告…";
            IsDetailsContentReady = true;
            var detailsTask = _session.LoadChannelDetailsAsync(channel.ChannelId, cancellation.Token);
            var memberIdsTask = _session.GetChannelMemberIdsAsync(channel.ChannelId, cancellation.Token);
            var usersTask = _session.GetRealmUsersAsync(cancellation.Token);
            // Observe every parallel read even if authority fails or the pane closes first.
            var allReads = Task.WhenAll(detailsTask, memberIdsTask, usersTask);
            _ = allReads.ContinueWith(static task => { _ = task.Exception; },
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            var details = await detailsTask;
            if (!IsDetailsLoadCurrent(generation, cancellation, expectedKey, expectedAccountId)) return;
            if (!PrivateGroupPolicy.IsEligible(details) || channel.Topic.Length != 0)
            {
                DetailsLoadError = "此频道已不再符合 RichChat 私有群聊规则；未提供管理操作。";
                DetailsChannelAnnouncement = "群公告暂不可用";
                return;
            }
            DetailsChannelName = details.Name;
            DetailsChannelAnnouncement = string.IsNullOrWhiteSpace(details.Description)
                ? "暂无群公告"
                : details.Description;
            EditablePrivateGroupName = details.Name;
            EditablePrivateGroupAnnouncement = details.Description ?? string.Empty;
            DetailsPrivateGroupOwnerId = PrivateGroupPolicy.TryGetOwnerId(details);
            IsPrivateGroupAuthorityLoaded = true;
            NotifyPrivateGroupActionProperties();

            await allReads;
            if (!IsDetailsLoadCurrent(generation, cancellation, expectedKey, expectedAccountId)) return;

            var memberIds = await memberIdsTask;
            var users = await usersTask;
            var usersById = users.ToDictionary(user => user.UserId);
            if (memberIds.Any(id => !usersById.ContainsKey(id)))
            {
                DetailsLoadError = "成员资料不完整，暂时无法安全显示全部成员。";
                return;
            }
            Reconcile(
                DetailsMembers,
                memberIds.Select(id => usersById[id])
                    .OrderBy(user => user.UserId)
                    .Select(user => new ConversationSettingsMemberItem(
                        user.UserId,
                        user.FullName,
                        user.DisplayAvatarUrl,
                        user.IsBot,
                        user.UserId == DetailsPrivateGroupOwnerId)),
                item => item.UserId);
            Reconcile(
                GroupMemberActionCandidates,
                memberIds.Select(id => usersById[id])
                    .Where(user => user.UserId != _session.CurrentUserId && user.IsActive && !user.IsBot)
                    .OrderBy(user => user.FullName, StringComparer.Ordinal)
                    .ThenBy(user => user.UserId)
                    .Select(user => new ConversationSettingsMemberItem(user.UserId, user.FullName, user.DisplayAvatarUrl, user.IsBot)),
                item => item.UserId);
            var memberIdSet = memberIds.ToHashSet();
            Reconcile(
                GroupInviteCandidates,
                users.Where(user => user.IsActive && !user.IsBot && user.UserId != _session.CurrentUserId && !memberIdSet.Contains(user.UserId))
                    .OrderBy(user => user.FullName, StringComparer.Ordinal)
                    .ThenBy(user => user.UserId)
                    .Select(user => new ConversationSettingsMemberItem(user.UserId, user.FullName, user.DisplayAvatarUrl, user.IsBot)),
                item => item.UserId);
            _privateGroupMembers[channel.ChannelId] = memberIds
                .OrderBy(static id => id)
                .Select(id => usersById[id])
                .ToArray();
            NotifyDetailsMembersChanged();
            NotifyPrivateGroupActionProperties();
            Project(_projectedState);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch
        {
            if (IsDetailsLoadCurrent(generation, cancellation, expectedKey, expectedAccountId))
            {
                DetailsLoadError = IsPrivateGroupAuthorityLoaded
                    ? "群资料已加载，但成员列表暂不可用；未改变任何设置。"
                    : "无法加载群资料；未改变任何设置。";
                if (!IsPrivateGroupAuthorityLoaded) DetailsChannelAnnouncement = "群公告暂不可用";
            }
        }
        finally
        {
            if (IsDetailsLoadCurrent(generation, cancellation, expectedKey, expectedAccountId)) IsDetailsLoading = false;
        }
    }

    private bool IsDetailsLoadCurrent(
        long generation, CancellationTokenSource cancellation, string expectedKey, AccountId? expectedAccountId) =>
        !_disposed && IsDetailsOpen &&
        _session.AccountId == expectedAccountId &&
        generation == _detailsLoadGeneration &&
        ReferenceEquals(cancellation, _detailsLoadCancellation) &&
        !cancellation.IsCancellationRequested &&
        string.Equals(_session.SelectedConversation?.CanonicalKey, expectedKey, StringComparison.Ordinal);

    private void ProjectDirectMessageAvatar(ConversationKey selected)
    {
        var avatar = selected is DirectMessage directMessage
            ? GetDirectMessageAvatar(directMessage, _projectedState.Users, _session.CurrentUserId)
            : null;
        DetailsAvatarUrl = avatar?.DisplayAvatarUrl;
        DetailsAvatarInitial = selected is DirectMessage ? AvatarInitials.Create(DetailsTitle, avatar?.IsBot == true) : "?";
    }

    private void ProjectConversationPreference()
    {
        if (!TryGetConversationPreferenceTarget(out var accountId, out var preferenceKey))
        {
            DetailsRemark = string.Empty;
            IsSelectedDirectMessageMuted = false;
            IsSelectedDirectMessagePinned = false;
            return;
        }

        var preference = _conversationPreferencesStore.Get(accountId, preferenceKey);
        DetailsRemark = preference.Remark ?? string.Empty;
        IsSelectedDirectMessageMuted = preference.IsMuted;
        IsSelectedDirectMessagePinned = preference.IsPinned;
    }

    private bool TryGetConversationPreferenceTarget(out AccountId accountId, out string preferenceKey)
    {
        if (_session.AccountId is not { } currentAccount || _session.SelectedConversation is not { } selected)
        {
            accountId = default;
            preferenceKey = string.Empty;
            return false;
        }

        accountId = currentAccount;
        preferenceKey = selected.CanonicalKey;
        return true;
    }

    private bool IsDirectMessagePinned(DirectMessage conversation)
    {
        if (_session.AccountId is not { } accountId) return false;
        return _conversationPreferencesStore.Get(accountId, conversation.CanonicalKey).IsPinned;
    }

    private void NotifyDetailsMembersChanged()
    {
        OnPropertyChanged(nameof(HasDetailsMembers));
        OnPropertyChanged(nameof(ShowDetailsMembersEmptyState));
        OnPropertyChanged(nameof(DetailsMemberCountLabel));
    }

    private void ResetPrivateGroupSettings()
    {
        DetailsPrivateGroupOwnerId = null;
        IsPrivateGroupAuthorityLoaded = false;
        EditablePrivateGroupName = string.Empty;
        EditablePrivateGroupAnnouncement = string.Empty;
        SelectedGroupInviteCandidate = null;
        SelectedGroupRemoveCandidate = null;
        SelectedGroupTransferCandidate = null;
        IsPrivateGroupActionBusy = false;
        PrivateGroupActionStatus = null;
        IsGroupRemoveConfirmationVisible = false;
        IsGroupTransferConfirmationVisible = false;
        IsGroupDissolveConfirmationVisible = false;
        Reconcile(GroupInviteCandidates, [], item => item.UserId);
        Reconcile(GroupMemberActionCandidates, [], item => item.UserId);
        NotifyPrivateGroupActionProperties();
    }

    private bool TryGetSelectedPrivateGroup(out ChannelTopic group)
    {
        if (_session.SelectedConversation is ChannelTopic
            {
                Topic.Length: 0
            } selected &&
            PrivateGroupPolicy.IsEligible(_projectedState.Subscriptions.GetValueOrDefault(selected.ChannelId)))
        {
            group = selected;
            return true;
        }

        group = null!;
        return false;
    }

    private bool IsSelectedConversation(string expectedKey) =>
        IsDetailsOpen && string.Equals(
            _session.SelectedConversation?.CanonicalKey,
            expectedKey,
            StringComparison.Ordinal);

    private static string DescribePrivateGroupActionFailure(string action, Exception exception) => exception switch
    {
        OperationCanceledException => $"{action}已取消；结果不确定时不会自动重试，请刷新后确认。",
        GatewayException gateway => $"{action}未完成：{DescribeGatewayFailure(gateway)} 不会自动重试。",
        InvalidOperationException invalid => $"{action}未执行：{DescribeInvalidOperation(invalid)}",
        _ => $"{action}失败；结果不确定时不会自动重试，请刷新后确认。"
    };

    private void CloseDetailsCore()
    {
        IsDetailsContentReady = false;
        CancelDetailsLoad();
        ClearConversationCacheConfirmationVisible = false;
        ResetPrivateGroupSettings();
        IsDetailsOpen = false;
    }

    private void CancelDetailsLoad()
    {
        var cancellation = Interlocked.Exchange(ref _detailsLoadCancellation, null);
        _detailsLoadGeneration++;
        if (cancellation is null) return;
        cancellation.Cancel();
        cancellation.Dispose();
    }

    [RelayCommand]
    private void BackToConversationList()
    {
        if (IsNarrowLayout)
        {
            IsConversationListVisibleOnNarrow = true;
        }
    }

    [RelayCommand]
    private void SetSystemTheme() => AppearanceMode = AppAppearanceMode.System;

    [RelayCommand]
    private void SetLightTheme() => AppearanceMode = AppAppearanceMode.Light;

    [RelayCommand]
    private void SetDarkTheme() => AppearanceMode = AppAppearanceMode.Dark;

    [RelayCommand]
    private void ToggleTheme() => AppearanceMode = AppearanceMode == AppAppearanceMode.Dark
        ? AppAppearanceMode.Light
        : AppAppearanceMode.Dark;

    [RelayCommand]
    private void SetComfortableDensity() => DensityMode = UiDensityMode.Comfortable;

    [RelayCommand]
    private void SetCompactDensity() => DensityMode = UiDensityMode.Compact;

    [RelayCommand]
    private void SetSmallFont() => FontScaleMode = UiFontScaleMode.Small;

    [RelayCommand]
    private void SetDefaultFont() => FontScaleMode = UiFontScaleMode.Default;

    [RelayCommand]
    private void SetLargeFont() => FontScaleMode = UiFontScaleMode.Large;

    [RelayCommand]
    private void SetNarrowConversationWidth() => ConversationWidthMode = UiConversationWidthMode.Narrow;

    [RelayCommand]
    private void SetStandardConversationWidth() => ConversationWidthMode = UiConversationWidthMode.Standard;

    [RelayCommand]
    private void SetWideConversationWidth() => ConversationWidthMode = UiConversationWidthMode.Wide;

    [RelayCommand]
    private void ResetUiPreferences() => ApplyUiPreferences(_uiPreferencesService.Reset());

    private void CloseTransientOverlays()
    {
        ChannelSettings.Close();
        IsSearchOpen = false;
        IsAccountMenuOpen = false;
        IsDownloadCenterOpen = false;
        IsFileDragActive = false;
        IsComposerEmojiPickerOpen = false;
        IsReactionPickerOpen = false;
        IsMessageMenuOpen = false;
        CloseChannelMenuCore(restoreFocus: false);
        CloseTopicMenuCore(restoreFocus: false);
        IsTopicMoveDialogOpen = false;
        IsTopicDeleteConfirmationOpen = false;
        IsTopicResolutionConfirmationOpen = false;
        IsEditDialogOpen = false;
        IsDeleteConfirmationOpen = false;
        IsChannelUnsubscribeConfirmationOpen = false;
        _channelUnsubscribeTargetId = null;
        ChannelUnsubscribeTargetName = string.Empty;
        ChannelUnsubscribeError = null;
        IsImageViewerOpen = false;
        IsNewConversationOpen = false;
        IsChannelBrowserOpen = false;
        ActiveImageAttachment = null;
        ActiveMessageAction = null;
        ActiveMessageAttachment = null;
    }

    private void CloseChannelMenuCore(bool restoreFocus)
    {
        if (ActiveChannelAction is { } channel) channel.IsActionMenuOpen = false;
        var wasOpen = IsChannelMenuOpen;
        IsChannelMenuOpen = false;
        ActiveChannelAction = null;
        if (restoreFocus && wasOpen) ChannelMenuFocusRequest++;
    }

    private void CloseTopicMenuCore(bool restoreFocus)
    {
        if (ActiveTopicAction is { } topic) topic.IsActionMenuOpen = false;
        var wasOpen = IsTopicMenuOpen;
        IsTopicMenuOpen = false;
        ActiveTopicAction = null;
        if (restoreFocus && wasOpen)
        {
            TopicMenuFocusRequest++;
        }
        NotifyTopicActionProperties();
    }

    private void NotifyTopicActionProperties()
    {
        OnPropertyChanged(nameof(CanMoveActiveTopic));
        OnPropertyChanged(nameof(CanResolveActiveTopic));
        OnPropertyChanged(nameof(CanDeleteActiveTopic));
        OnPropertyChanged(nameof(ActiveTopicHasMessages));
        OnPropertyChanged(nameof(ActiveTopicIsEmpty));
        OnPropertyChanged(nameof(CanSetActiveTopicVisibility));
        OnPropertyChanged(nameof(CanMarkActiveTopicRead));
        OnPropertyChanged(nameof(CanAdministerActiveTopicOperations));
        OnPropertyChanged(nameof(CanConfirmTopicMove));
        OnPropertyChanged(nameof(HasTopicActionStatus));
        OnPropertyChanged(nameof(IsActiveTopicMutedPolicy));
        OnPropertyChanged(nameof(IsActiveTopicInheritPolicy));
        OnPropertyChanged(nameof(IsActiveTopicUnmutedPolicy));
        OnPropertyChanged(nameof(IsActiveTopicFollowedPolicy));
        OnPropertyChanged(nameof(ShowActiveTopicUnmutedPolicy));
    }

    partial void OnIsLoggedInChanged(bool value)
    {
        OnPropertyChanged(nameof(LoginVisible));
        OnPropertyChanged(nameof(MainVisible));
        OnPropertyChanged(nameof(WorkspaceDisplayName));
        if (!value)
        {
            CloseChannelBrowser();
            ResetDrafts();
        }
    }

    partial void OnRealmChanged(string value) => OnPropertyChanged(nameof(WorkspaceDisplayName));

    partial void OnLoginErrorChanged(string? value) => OnPropertyChanged(nameof(HasLoginError));

    partial void OnChannelUnsubscribeErrorChanged(string? value) =>
        OnPropertyChanged(nameof(HasChannelUnsubscribeError));

    partial void OnChannelBrowserErrorChanged(string? value) => OnPropertyChanged(nameof(HasChannelBrowserError));

    partial void OnTopicActionStatusChanged(string? value) => OnPropertyChanged(nameof(HasTopicActionStatus));

    partial void OnActiveTopicActionChanged(TopicItem? value) => NotifyTopicActionProperties();

    partial void OnTopicMoveDestinationChannelChanged(ChannelItem? value) => OnPropertyChanged(nameof(CanConfirmTopicMove));

    partial void OnTopicMoveDestinationNameChanged(string value) => OnPropertyChanged(nameof(CanConfirmTopicMove));

    partial void OnIsChannelUnsubscribeBusyChanged(bool value) =>
        OnPropertyChanged(nameof(CanCloseChannelUnsubscribe));

    partial void OnComposerTextChanged(string value)
    {
        if (!_suppressDraftTracking && _activeDraftKey is not null)
        {
            _drafts[_activeDraftKey] = value;
        }

        NotifyCanSendChanged();
        ComposerCursorPosition = Math.Clamp(ComposerCursorPosition, 0, value.Length);
        ComposerSelectionLength = Math.Clamp(ComposerSelectionLength, 0, value.Length - ComposerCursorPosition);
    }

    partial void OnSearchQueryChanged(string value)
    {
        CancelSearchInput();
        _hasSubmittedSearch = !string.IsNullOrWhiteSpace(value);
        _serverSearchResults = [];
        _searchBeforeMessageId = null;
        SelectedSearchResult = null;
        IsSearchBusy = false;
        SearchError = null;
        ProjectSearch();
        OnPropertyChanged(nameof(HasMoreSearchResults));
        OnPropertyChanged(nameof(SearchEmptyText));
        ScheduleServerSearch(value);
    }
    partial void OnSearchErrorChanged(string? value) => OnPropertyChanged(nameof(HasSearchError));
    partial void OnIsSearchBusyChanged(bool value) => OnPropertyChanged(nameof(SearchEmptyText));
    partial void OnSavedErrorChanged(string? value)
    {
        OnPropertyChanged(nameof(HasSavedError));
        OnPropertyChanged(nameof(IsSavedEmpty));
    }
    partial void OnNewConversationQueryChanged(string value) => ProjectNewConversationChoices();
    partial void OnAttachmentErrorChanged(string? value) =>
        OnPropertyChanged(nameof(HasAttachmentError));
    partial void OnMediaActionStatusChanged(string? value)
    {
        OnPropertyChanged(nameof(HasMediaActionStatus));
        OnPropertyChanged(nameof(IsMediaDownloadStatusVisible));
    }

    partial void OnIsMediaActionBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(IsMediaDownloadStatusVisible));
        OnPropertyChanged(nameof(IsMediaDownloadIndeterminate));
        OnPropertyChanged(nameof(CanStartMediaDownload));
        OnPropertyChanged(nameof(CanRetryMediaDownload));
        OnPropertyChanged(nameof(ShowDownloadCenterCurrentTask));
        OnPropertyChanged(nameof(IsDownloadCenterEmpty));
        OnPropertyChanged(nameof(HasDownloadButtonAttention));
        OnPropertyChanged(nameof(DownloadButtonDescription));
    }

    partial void OnMediaDownloadProgressChanged(double value) =>
        OnPropertyChanged(nameof(DownloadButtonDescription));

    partial void OnHasKnownMediaDownloadLengthChanged(bool value) =>
        OnPropertyChanged(nameof(IsMediaDownloadIndeterminate));

    partial void OnAskWhereToSaveDownloadsChanged(bool value) =>
        _fileSaveService.AskWhereToSave = value;

    partial void OnDownloadSettingsStatusChanged(string? value) =>
        OnPropertyChanged(nameof(HasDownloadSettingsStatus));

    partial void OnDownloadCenterStatusChanged(string? value) =>
        OnPropertyChanged(nameof(HasDownloadCenterStatus));

    partial void OnIsDownloadCenterOpenChanged(bool value) => NotifyOverlayProperties();

    partial void OnHasUnseenCompletedDownloadsChanged(bool value)
    {
        OnPropertyChanged(nameof(HasDownloadButtonAttention));
        OnPropertyChanged(nameof(DownloadButtonDescription));
    }

    partial void OnHasUnseenDownloadFailureChanged(bool value) =>
        OnPropertyChanged(nameof(HasDownloadButtonAttention));
    partial void OnMessageLoadErrorChanged(string? value)
    {
        OnPropertyChanged(nameof(HasMessageLoadError));
        OnPropertyChanged(nameof(ShowHistoryRetry));
    }
    partial void OnNewMessageCountChanged(int value)
    {
        OnPropertyChanged(nameof(ShowNewMessagesButton));
        OnPropertyChanged(nameof(NewMessagesButtonText));
    }

    partial void OnIsSearchOpenChanged(bool value)
    {
#if DEBUG
        if (value && NativeShellPreviewSession.IsRequested &&
            string.Equals(NativeShellPreviewSession.RequestedScene, "details", StringComparison.OrdinalIgnoreCase))
        {
            IsSearchOpen = false;
            return;
        }
#endif
        NotifyOverlayProperties();
    }
    partial void OnIsAccountMenuOpenChanged(bool value)
    {
        if (!value && !IsOwnNameSaveBusy) CancelOwnNameEdit();
        NotifyOverlayProperties();
    }
    partial void OnIsNewConversationOpenChanged(bool value)
    {
        if (!value) ClearNewConversationChoices();
        OnPropertyChanged(nameof(ShowPrivateGroupCreateDisabledReason));
        NotifyOverlayProperties();
    }
    partial void OnIsChannelBrowserOpenChanged(bool value) => NotifyOverlayProperties();
    partial void OnIsComposerEmojiPickerOpenChanged(bool value)
    {
        Stickers?.SetOpen(value);
        NotifyOverlayProperties();
    }

    private void OnStickerSent(object? sender, EventArgs args)
    {
        IsComposerEmojiPickerOpen = false;
        QueueScrollToLatest(MessageScrollReason.RealtimeFollow);
        ComposerFocusRequest++;
    }

    [RelayCommand]
    private async Task CollectActiveImageAsync()
    {
        if (Stickers is null || ActiveMessageAttachment is not { IsImage: true } image) return;
        CloseMessageMenu();
        await Stickers.CollectMessageImageAsync(image);
    }
    partial void OnIsReactionPickerOpenChanged(bool value) => NotifyOverlayProperties();

    private bool IsChannelBrowserCurrent(long generation, AccountId? accountId, CancellationTokenSource? cancellation = null) =>
        IsChannelBrowserOpen && generation == _channelBrowserGeneration && accountId == _channelBrowserAccountId && accountId == _session.AccountId &&
        (cancellation is null || ReferenceEquals(cancellation, _channelBrowserCancellation) && !cancellation.IsCancellationRequested);
    partial void OnSelectedComposerEmojiChanged(EmojiChoice? oldValue, EmojiChoice? newValue)
    {
        if (oldValue is not null) oldValue.IsComposerSelected = false;
        if (newValue is not null) newValue.IsComposerSelected = true;
    }

    partial void OnSelectedReactionEmojiChanged(EmojiChoice? oldValue, EmojiChoice? newValue)
    {
        if (oldValue is not null) oldValue.IsReactionSelected = false;
        if (newValue is not null) newValue.IsReactionSelected = true;
    }

    partial void OnIsMessageMenuOpenChanged(bool value)
    {
        _messageActionTimer?.Dispose();
        _messageActionTimer = value ? _timeProvider.CreateTimer(_ => _dispatcher.Dispatch(() =>
        {
            if (!_disposed && IsMessageMenuOpen) NotifyMessageActionPermissions();
        }), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)) : null;
        NotifyMessageActionPermissions();
        NotifyOverlayProperties();
    }
    partial void OnActiveMessageAttachmentChanged(MessageAttachmentItem? value) =>
        OnPropertyChanged(nameof(HasActiveMessageAttachment));
    partial void OnIsChannelMenuOpenChanged(bool value) => NotifyOverlayProperties();
    partial void OnActiveChannelActionChanged(ChannelItem? value)
    {
        OnPropertyChanged(nameof(CanManageActiveChannel));
        OnPropertyChanged(nameof(ActiveChannelMuteLabel));
        OnPropertyChanged(nameof(ActiveChannelPinLabel));
        OnPropertyChanged(nameof(ActiveChannelMarkReadLabel));
    }
    partial void OnIsEditDialogOpenChanged(bool value) => NotifyOverlayProperties();
    partial void OnIsDeleteConfirmationOpenChanged(bool value) => NotifyOverlayProperties();
    partial void OnIsChannelUnsubscribeConfirmationOpenChanged(bool value) => NotifyOverlayProperties();
    partial void OnIsImageViewerOpenChanged(bool value) => NotifyOverlayProperties();
    partial void OnLogoutConfirmationVisibleChanged(bool value) => NotifyOverlayProperties();

    partial void OnActiveMessageActionChanged(MessageItem? value)
    {
        if (value is null) _activeMessageSelection = null;
        OnPropertyChanged(nameof(HasActiveMessageAction));
        OnPropertyChanged(nameof(CanCopyActiveMessage));
        OnPropertyChanged(nameof(CanEditActiveMessage));
        OnPropertyChanged(nameof(CanDeleteActiveMessage));
        OnPropertyChanged(nameof(CanStarActiveMessage));
        OnPropertyChanged(nameof(ActiveMessageStarActionLabel));
        OnPropertyChanged(nameof(ActiveMessageTitle));
    }

    partial void OnSelectedSectionChanged(ShellSection value)
    {
        OnPropertyChanged(nameof(IsMessagesSection));
        OnPropertyChanged(nameof(IsContactsSection));
        OnPropertyChanged(nameof(IsSavedSection));
        OnPropertyChanged(nameof(IsSettingsSection));
        OnPropertyChanged(nameof(IsConversationWorkspaceSection));
        NotifyLayoutProperties();
        RequestAutoMarkDisplayedRead(_projectedState);
    }

    partial void OnSelectedSettingsCategoryChanged(SettingsCategory value)
    {
        OnPropertyChanged(nameof(IsAppearanceSettings));
        OnPropertyChanged(nameof(IsGeneralSettings));
        OnPropertyChanged(nameof(IsNotificationSettings));
        OnPropertyChanged(nameof(IsStorageSettings));
        OnPropertyChanged(nameof(IsAccountSettings));
        OnPropertyChanged(nameof(IsAboutSettings));
    }

    partial void OnLayoutModeChanged(ShellLayoutMode value) => NotifyLayoutProperties();

    partial void OnIsDetailsOpenChanged(bool value)
    {
        if (!value)
        {
            IsDetailsContentReady = false;
            IsDetailsLoading = false;
            CancelDetailsLoad();
            ClearConversationCacheConfirmationVisible = false;
        }
        OnPropertyChanged(nameof(IsInlineDetailsVisible));
        OnPropertyChanged(nameof(IsOverlayDetailsVisible));
        OnPropertyChanged(nameof(IsPrimaryShellEnabled));
        OnPropertyChanged(nameof(InlineDetailsWidth));
        OnPropertyChanged(nameof(MessageRowMaximumWidth));
        RequestAutoMarkDisplayedRead(_projectedState);
    }

    partial void OnIsConversationListVisibleOnNarrowChanged(bool value)
    {
        NotifyLayoutProperties();
        RequestAutoMarkDisplayedRead(_projectedState);
    }

    partial void OnAppearanceModeChanged(AppAppearanceMode value)
    {
        _appearanceService.Apply(value);
        OnPropertyChanged(nameof(IsSystemTheme));
        OnPropertyChanged(nameof(IsLightTheme));
        OnPropertyChanged(nameof(IsDarkTheme));
    }

    partial void OnDensityModeChanged(UiDensityMode value)
    {
        OnPropertyChanged(nameof(IsComfortableDensity));
        OnPropertyChanged(nameof(IsCompactDensity));
        SaveUiPreferences();
    }

    partial void OnFontScaleModeChanged(UiFontScaleMode value)
    {
        if (!_preserveContinuousPreference)
        {
            _fontSize = value switch { UiFontScaleMode.Small => 12d, UiFontScaleMode.Large => 16d, _ => 14d };
        }
        OnPropertyChanged(nameof(IsSmallFont));
        OnPropertyChanged(nameof(IsDefaultFont));
        OnPropertyChanged(nameof(IsLargeFont));
        OnPropertyChanged(nameof(FontScaleSliderValue));
        OnPropertyChanged(nameof(CurrentFontSizeLabel));
        SaveUiPreferences();
    }

    partial void OnConversationWidthModeChanged(UiConversationWidthMode value)
    {
        if (!_preserveContinuousPreference)
        {
            _conversationPaneWidth = value switch { UiConversationWidthMode.Narrow => 264d, UiConversationWidthMode.Wide => 352d, _ => 310d };
        }
        OnPropertyChanged(nameof(IsNarrowConversationWidth));
        OnPropertyChanged(nameof(IsStandardConversationWidth));
        OnPropertyChanged(nameof(IsWideConversationWidth));
        OnPropertyChanged(nameof(ConversationPaneWidth));
        OnPropertyChanged(nameof(MessageRowMaximumWidth));
        OnPropertyChanged(nameof(ConversationWidthSliderValue));
        OnPropertyChanged(nameof(CurrentConversationWidthLabel));
        SaveUiPreferences();
    }

    partial void OnAreChannelsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(ChannelListHeight));
        OnPropertyChanged(nameof(TopicListHeight));
        OnPropertyChanged(nameof(ShowTopicPicker));
        OnPropertyChanged(nameof(ShowEmptyChannelTopicState));
        SaveUiPreferences();
    }

    partial void OnAreDirectMessagesExpandedChanged(bool value) => SaveUiPreferences();

    partial void OnSystemNotificationsEnabledChanged(bool value) => SaveNotificationPreferences();

    partial void OnTaskbarFlashEnabledChanged(bool value)
    {
        SaveNotificationPreferences();
        if (!value)
        {
            _appNotificationService.StopTaskbarFlash();
            _appNotificationService.StopTrayFlash();
        }
    }

    partial void OnTaskbarBadgeEnabledChanged(bool value)
    {
        SaveNotificationPreferences();
        SynchronizeTaskbarBadge(_projectedState.Unread);
    }

    partial void OnShowMessagePreviewChanged(bool value) => SaveNotificationPreferences();

    partial void OnDoNotDisturbChanged(bool value)
    {
        SaveNotificationPreferences();
        if (value)
        {
            _appNotificationService.StopTaskbarFlash();
            _appNotificationService.StopTrayFlash();
        }
    }

    partial void OnUnavailableFeatureMessageChanged(string? value) =>
        OnPropertyChanged(nameof(HasUnavailableFeatureMessage));

    partial void OnDetailsLoadErrorChanged(string? value) => OnPropertyChanged(nameof(HasDetailsLoadError));

    partial void OnIsDetailsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowDetailsMembersEmptyState));
        NotifyPrivateGroupActionProperties();
    }

    partial void OnDetailsPrivateGroupOwnerIdChanged(long? value) => NotifyPrivateGroupActionProperties();

    partial void OnIsPrivateGroupAuthorityLoadedChanged(bool value) => NotifyPrivateGroupActionProperties();

    partial void OnIsPrivateGroupActionBusyChanged(bool value) => NotifyPrivateGroupActionProperties();

    partial void OnPrivateGroupActionStatusChanged(string? value) => OnPropertyChanged(nameof(HasPrivateGroupActionStatus));

    partial void OnSelectedGroupInviteCandidateChanged(ConversationSettingsMemberItem? value) =>
        OnPropertyChanged(nameof(CanInvitePrivateGroupMember));

    partial void OnSelectedGroupRemoveCandidateChanged(ConversationSettingsMemberItem? value) =>
        OnPropertyChanged(nameof(CanRemovePrivateGroupMember));

    partial void OnSelectedGroupTransferCandidateChanged(ConversationSettingsMemberItem? value) =>
        OnPropertyChanged(nameof(CanTransferPrivateGroupOwnership));

    private void NotifyPrivateGroupActionProperties()
    {
        OnPropertyChanged(nameof(IsCurrentUserPrivateGroupOwner));
        OnPropertyChanged(nameof(CanManagePrivateGroup));
        OnPropertyChanged(nameof(ShowPrivateGroupManagementBoundary));
        OnPropertyChanged(nameof(CanInvitePrivateGroupMember));
        OnPropertyChanged(nameof(CanRemovePrivateGroupMember));
        OnPropertyChanged(nameof(CanTransferPrivateGroupOwnership));
        OnPropertyChanged(nameof(CanExitPrivateGroup));
    }

    partial void OnIsClearConversationCacheBusyChanged(bool value) => OnPropertyChanged(nameof(CanClearConversationCache));

    partial void OnSelectedChannelChanged(ChannelItem? value)
    {
        OnPropertyChanged(nameof(HasSelectedChannel));
        OnPropertyChanged(nameof(TopicListHeight));
        OnPropertyChanged(nameof(ChannelListHeight));
        OnPropertyChanged(nameof(ShowTopicPicker));
        OnPropertyChanged(nameof(ShowEmptyChannelTopicState));
        OnPropertyChanged(nameof(SelectedChannelMuteLabel));
        OnPropertyChanged(nameof(SelectedChannelPinLabel));
        RefreshNavigationSelectionProjection();
    }

    partial void OnSelectedTopicChanged(TopicItem? value)
    {
        OnPropertyChanged(nameof(HasSelectedTopic));
        RefreshNavigationSelectionProjection();
    }

    partial void OnShowChannelDetailsChanged(bool value) => OnPropertyChanged(nameof(ShowChannelActionBoundary));

    partial void OnSelectedDirectMessageChanged(NavigationItem? value)
    {
        RefreshNavigationSelectionProjection();
    }

    partial void OnSelectedConversationItemChanged(ConversationListItem? value) =>
        RefreshNavigationSelectionProjection();

    partial void OnConversationFilterQueryChanged(string value)
    {
        OnPropertyChanged(nameof(ShowConversationSearchIcon));
        StartConversationFilterSearch(value);
    }

    partial void OnIsConversationFilterBusyChanged(bool value) =>
        NotifyConversationFilterStatusProperties();

    partial void OnConversationFilterErrorChanged(string? value) =>
        NotifyConversationFilterStatusProperties();

    public void SelectFirstFilteredConversation()
    {
        if (FilteredConversations.FirstOrDefault() is { } conversation) ActivateConversation(conversation);
    }

    public void ClearConversationFilter() => ConversationFilterQuery = string.Empty;

    internal void ActivateChannel(ChannelItem channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ConversationFilterQuery = string.Empty;
        if (_expandedChannelId == channel.ChannelId)
        {
            SetExpandedChannel(null);
            return;
        }

        SetExpandedChannel(channel.ChannelId);
        _ = ActivateChannelAsync(channel);
    }

    internal void ActivateTopic(TopicItem topic)
    {
        ArgumentNullException.ThrowIfNull(topic);
        ConversationFilterQuery = string.Empty;
        SetExpandedChannel(topic.ChannelId);
        _ = ActivateConversationFromNavigationAsync(
            new ChannelTopic(topic.ChannelId, topic.Topic),
            Channels.FirstOrDefault(channel => channel.ChannelId == topic.ChannelId),
            topic,
            null);
    }

    internal void ActivateDirectMessage(NavigationItem directMessage)
    {
        ArgumentNullException.ThrowIfNull(directMessage);
        if (IsNavigationPending && string.Equals(
                _navigationConversationKey,
                directMessage.Conversation.CanonicalKey,
                StringComparison.Ordinal))
        {
            return;
        }
        ConversationFilterQuery = string.Empty;
        SetExpandedChannel(null);
        _ = ActivateConversationFromNavigationAsync(directMessage.Conversation, null, null, directMessage);
    }

    internal void ActivateConversation(ConversationListItem conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        if (IsNavigationPending && string.Equals(
                _navigationConversationKey,
                conversation.Conversation.CanonicalKey,
                StringComparison.Ordinal))
        {
            return;
        }

        var searchTargetMessageId = conversation.SearchTargetMessageId;
        ConversationFilterQuery = string.Empty;
        SetExpandedChannel(null);
        SelectedConversationItem = conversation;
        if (searchTargetMessageId is { } messageId)
        {
            _ = OpenConversationFilterMatchAsync(conversation.Conversation, messageId);
            return;
        }
        var channel = conversation.Conversation is ChannelTopic channelTopic
            ? Channels.FirstOrDefault(item => item.ChannelId == channelTopic.ChannelId)
            : null;
        var topic = conversation.Conversation is ChannelTopic selectedTopic
            ? new TopicItem(selectedTopic.ChannelId, selectedTopic.Topic, null, isSelected: true)
            : null;
        var direct = conversation.Conversation is DirectMessage
            ? DirectMessages.FirstOrDefault(item => string.Equals(
                item.Conversation.CanonicalKey,
                conversation.Conversation.CanonicalKey,
                StringComparison.Ordinal))
            : null;
        _ = ActivateConversationFromNavigationAsync(conversation.Conversation, channel, topic, direct);
    }

    private async Task OpenConversationFilterMatchAsync(ConversationKey conversation, long messageId)
    {
        if (!IsRelayCoveConversation(conversation, _projectedState)) return;
        RestoreConversationToList(conversation);
        if (await ExecuteSessionActionAsync(() => _session.OpenMessageAsync(conversation, messageId)))
        {
            ProjectLatestStateImmediately();
            QueueScrollToMessage(messageId);
            SelectedSection = ShellSection.Messages;
            if (IsNarrowLayout) IsConversationListVisibleOnNarrow = false;
        }
    }

    partial void OnIsNewChannelConversationModeChanged(bool value)
    {
        if (value)
        {
            foreach (var self in _allNewConversationChoices.Where(choice => choice.IsSelf)) self.IsSelected = false;
        }
        ProjectNewConversationChoices();
        NotifyNewConversationModeProperties();
    }

    partial void OnNewConversationChannelChanged(ChannelItem? value) =>
        OnPropertyChanged(nameof(CanStartNewChannelConversation));

    partial void OnIsNewConversationChannelLockedChanged(bool value) =>
        NotifyNewConversationModeProperties();

    partial void OnNewConversationTopicChanged(string value) =>
        OnPropertyChanged(nameof(CanStartNewChannelConversation));

    partial void OnNewPrivateGroupNameChanged(string value)
    {
        OnPropertyChanged(nameof(CanStartNewChannelConversation));
        OnPropertyChanged(nameof(PrivateGroupCreateDisabledReason));
        OnPropertyChanged(nameof(ShowPrivateGroupCreateDisabledReason));
    }

    partial void OnNewConversationErrorChanged(string? value) =>
        OnPropertyChanged(nameof(HasNewConversationError));

    private void NotifyNewConversationModeProperties()
    {
        OnPropertyChanged(nameof(IsNewDirectConversationMode));
        OnPropertyChanged(nameof(IsNewConversationChoicesVisible));
        OnPropertyChanged(nameof(IsNewConversationChoiceEmptyVisible));
        OnPropertyChanged(nameof(IsLockedChannelTopicComposer));
        OnPropertyChanged(nameof(IsNewUnlockedChannelTopicComposer));
        OnPropertyChanged(nameof(IsNewConversationModeSwitcherVisible));
        OnPropertyChanged(nameof(IsNewConversationChannelPickerVisible));
        OnPropertyChanged(nameof(IsLockedChannelTopicVisible));
        OnPropertyChanged(nameof(CanStartNewConversation));
        OnPropertyChanged(nameof(CanStartNewChannelConversation));
        OnPropertyChanged(nameof(CanCreatePrivateGroup));
        OnPropertyChanged(nameof(PrivateGroupCreateDisabledReason));
        OnPropertyChanged(nameof(ShowPrivateGroupCreateDisabledReason));
    }

    private async Task OpenChannelFromSettingsAsync(long channelId)
    {
        var channel = Channels.FirstOrDefault(item => item.ChannelId == channelId);
        if (channel is null) return;
        ChannelSettings.Close();
        SetExpandedChannel(channelId);
        await ActivateChannelAsync(channel);
    }

    private void OnChannelSettingsPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName is nameof(ChannelSettingsViewModel.IsOpen))
        {
            OnPropertyChanged(nameof(IsModalOverlayVisible));
            OnPropertyChanged(nameof(IsPrimaryShellEnabled));
            OnPropertyChanged(nameof(ChannelSettings));
            if (!ChannelSettings.IsOpen) ChannelMenuFocusRequest++;
        }
    }

    private async Task ActivateChannelAsync(ChannelItem channel)
    {
        var (generation, cancellation) = BeginNavigation();
        SelectedChannel = Channels.FirstOrDefault(item => item.ChannelId == channel.ChannelId) ?? channel;
        SelectedTopic = null;
        SelectedDirectMessage = null;
        _loadedTopics = [];
        _loadedTopicsChannelId = channel.ChannelId;
        _hasAuthoritativeTopics = false;
        ProjectTopics(_projectedState, channel.ChannelId);
        try
        {
            var topics = await _session.LoadTopicsAsync(channel.ChannelId, cancellation.Token);
            if (!IsNavigationCurrent(generation, cancellation)) return;

            _loadedTopics = topics;
            _loadedTopicsChannelId = channel.ChannelId;
            _hasAuthoritativeTopics = true;
            ProjectTopics(_projectedState, channel.ChannelId);
            if (Topics.Count == 0)
            {
                IsAuthoritativeEmptyChannel = true;
                return;
            }

            var remembered = _lastSelectedTopicByChannel.GetValueOrDefault(channel.ChannelId);
            var topic = remembered is null
                ? Topics.OrderByDescending(item => item.MaxMessageId)
                    .ThenBy(item => item.Topic, StringComparer.Ordinal)
                    .First()
                : Topics.FirstOrDefault(item => string.Equals(item.Topic, remembered, StringComparison.Ordinal)) ??
                  Topics.OrderByDescending(item => item.MaxMessageId)
                      .ThenBy(item => item.Topic, StringComparer.Ordinal)
                      .First();
            SelectedTopic = topic;
            await SelectConversationForNavigationAsync(
                new ChannelTopic(topic.ChannelId, topic.Topic),
                generation,
                cancellation);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (GatewayException exception)
        {
            SetNavigationFailure(generation, cancellation, DescribeGatewayFailure(exception));
        }
        catch (InvalidOperationException exception)
        {
            SetNavigationFailure(generation, cancellation, DescribeInvalidOperation(exception));
        }
        catch (Exception)
        {
            SetNavigationFailure(generation, cancellation, "无法读取频道话题，请稍后重试。");
        }
        finally
        {
            CompleteNavigation(generation, cancellation);
        }
    }

    private async Task<bool> ActivateConversationFromNavigationAsync(
        ConversationKey conversation,
        ChannelItem? channel,
        TopicItem? topic,
        NavigationItem? directMessage)
    {
        RestoreConversationToList(conversation);
        var (generation, cancellation) = BeginNavigation(conversation.CanonicalKey);
        SelectedConversationItem = Conversations.FirstOrDefault(item => string.Equals(
            item.Conversation.CanonicalKey,
            conversation.CanonicalKey,
            StringComparison.Ordinal));
        SelectedChannel = channel;
        SelectedTopic = topic;
        SelectedDirectMessage = directMessage;
        _hasAuthoritativeTopics = topic is not null && _loadedTopicsChannelId == topic.ChannelId && _hasAuthoritativeTopics;
        try
        {
            return await SelectConversationForNavigationAsync(conversation, generation, cancellation);
        }
        finally
        {
            CompleteNavigation(generation, cancellation);
        }
    }

    private async Task<bool> SelectConversationForNavigationAsync(
        ConversationKey conversation,
        long navigationGeneration,
        CancellationTokenSource cancellation)
    {
        var wasRepeatedActivation = string.Equals(
            _session.SelectedConversation?.CanonicalKey,
            conversation.CanonicalKey,
            StringComparison.Ordinal);
        _displayedConversationKey = conversation.CanonicalKey;
        _pendingActivationScrollConversationKey = conversation.CanonicalKey;
        _pendingActivationScrollGeneration = 0;
        _pendingActivationScrollReason = wasRepeatedActivation
            ? MessageScrollReason.ConversationReactivated
            : MessageScrollReason.ConversationActivated;
        var shouldDeferRevalidation = ActivateMessagePresentation(conversation.CanonicalKey);
        if (shouldDeferRevalidation)
        {
            _retainedActivationConversationKey = conversation.CanonicalKey;
            _retainedActivationLatestMessageId = Messages
                .Where(message => message.MessageId is not null)
                .Select(message => message.MessageId!.Value)
                .DefaultIfEmpty()
                .Max();
        }
        ProjectConversation(conversation, _projectedState);
        ProjectDraft(conversation);
        NotifyConversationAvailability();
        if (shouldDeferRevalidation)
        {
            // Return the click to WinUI after activating an already-realized
            // presentation. Cache/database revalidation must not block the visual
            // conversation swap on the UI thread.
            await Task.Yield();
            if (!IsNavigationCurrent(navigationGeneration, cancellation)) return false;
        }
        var selectionTask = _session.SelectConversationAsync(conversation, cancellation.Token);
        ProjectLatestStateImmediately();
        var success = await ExecuteSessionActionAsync(() => selectionTask);
        if (!IsNavigationCurrent(navigationGeneration, cancellation)) return false;
        if (!success)
        {
            SetNavigationFailure(
                navigationGeneration,
                cancellation,
                LoginError ?? "无法加载会话，请稍后重试。");
            return false;
        }

        var history = _session.HistoryState;
        if (!string.Equals(history.Conversation?.CanonicalKey, conversation.CanonicalKey, StringComparison.Ordinal) ||
            history.Error is not null)
        {
            SetNavigationFailure(navigationGeneration, cancellation, DescribeHistoryError(history.Error) ?? "无法加载会话，请稍后重试。");
            return false;
        }

        _displayedConversationKey = conversation.CanonicalKey;
        IsAuthoritativeEmptyChannel = false;
        HasConversationActivationError = false;
        MessageLoadError = null;
        if (conversation is ChannelTopic channelTopic)
        {
            _lastSelectedTopicByChannel[channelTopic.ChannelId] = channelTopic.Topic;
        }
        SynchronizeSelection(conversation);
        if (_lastActivationScrollGeneration != history.Generation ||
            !string.Equals(_lastActivationScrollConversationKey, conversation.CanonicalKey, StringComparison.Ordinal))
        {
            _pendingActivationScrollConversationKey = conversation.CanonicalKey;
            _pendingActivationScrollGeneration = history.Generation;
            _pendingActivationScrollReason = wasRepeatedActivation
                ? MessageScrollReason.ConversationReactivated
                : MessageScrollReason.ConversationActivated;
            TryPublishPendingActivationScroll();
        }
        if (IsNarrowLayout) IsConversationListVisibleOnNarrow = false;
        NotifyConversationAvailability();
        return true;
    }

    private (long Generation, CancellationTokenSource Cancellation) BeginNavigation(string? conversationKey = null)
    {
        if (IsDetailsOpen) CloseDetailsCore();
        CancelAutoMarkReadOperation(allowRetry: true);
        CancelNavigation();
        // A completed activation belongs to the previous visit. Re-entering the
        // same conversation must still position the newly realized native list
        // at its latest message, even when the history generation and target ID
        // are unchanged because the page came from memory/SQLite.
        _lastActivationScrollConversationKey = null;
        _lastActivationScrollGeneration = 0;
        _lastActivationScrollTargetMessageId = 0;
        _retainedActivationConversationKey = null;
        _retainedActivationLatestMessageId = 0;
        var cancellation = new CancellationTokenSource();
        _navigationCancellation = cancellation;
        var generation = ++_navigationGeneration;
        _navigationConversationKey = conversationKey;
        IsNavigationPending = true;
        IsConversationLoading = true;
        IsAuthoritativeEmptyChannel = false;
        HasConversationActivationError = false;
        MessageLoadError = null;
        _displayedConversationKey = conversationKey;
        _pendingActivationScrollConversationKey = null;
        _pendingActivationScrollReason = null;
        PendingMessageScrollRequest = null;
        NewMessageCount = 0;
        SetMessageViewportBeyondJumpThreshold(false);
        if (IsNarrowLayout) IsConversationListVisibleOnNarrow = false;
        NotifyConversationAvailability();
        return (generation, cancellation);
    }

    private bool IsNavigationCurrent(long generation, CancellationTokenSource cancellation) =>
        !cancellation.IsCancellationRequested &&
        generation == _navigationGeneration &&
        ReferenceEquals(_navigationCancellation, cancellation);

    private void SetNavigationFailure(long generation, CancellationTokenSource cancellation, string message)
    {
        if (!IsNavigationCurrent(generation, cancellation)) return;
        HasConversationActivationError = true;
        MessageLoadError = message;
        NotifyConversationAvailability();
    }

    private void CompleteNavigation(long generation, CancellationTokenSource cancellation)
    {
        if (!IsNavigationCurrent(generation, cancellation)) return;
        Interlocked.CompareExchange(ref _navigationCancellation, null, cancellation);
        _navigationConversationKey = null;
        cancellation.Dispose();
        IsNavigationPending = false;
        IsConversationLoading = false;
        NotifyConversationAvailability();
        ProjectLatestStateImmediately();
        _retainedActivationConversationKey = null;
        _retainedActivationLatestMessageId = 0;
    }

    private async Task<bool> ExecuteSessionActionAsync(
        Func<Task> action,
        string? failureMessage = null)
    {
        var wasCanceled = false;
        LoginError = null;
        try
        {
            await action();
            return true;
        }
        catch (CredentialVaultException)
        {
            LoginError = "凭据存储不可用，请重新登录。";
        }
        catch (GatewayException exception)
        {
            LoginError = DescribeGatewayFailure(exception);
        }
        catch (OperationCanceledException)
        {
            // Navigation cancellation is expected and must not leave an error banner.
            wasCanceled = true;
        }
        catch (InvalidOperationException exception)
        {
            LoginError = DescribeInvalidOperation(exception);
        }
        catch (Exception)
        {
            LoginError = failureMessage ?? "本地缓存操作失败，请稍后重试。";
        }
        finally
        {
            if (wasCanceled)
            {
                QueueProjection(_session.State);
            }
            else
            {
                ProjectLatestStateImmediately();
            }
        }
        return false;
    }

    private void OnStateChanged(object? sender, ClientStateChangedEventArgs eventArgs) =>
        QueueProjection(eventArgs.State);

    private void QueueProjection(ClientState state)
    {
        lock (_projectionGate)
        {
            if (_disposed) return;
            _pendingProjectionState = state;
            if (_projectionDispatchScheduled) return;
            _projectionDispatchScheduled = true;
        }

        _dispatcher.Dispatch(DrainProjectionQueue);
    }

    private void DrainProjectionQueue()
    {
        ClientState? state;
        lock (_projectionGate)
        {
            state = _pendingProjectionState;
            _pendingProjectionState = null;
        }

        if (!_disposed && state is not null) Project(state);

        lock (_projectionGate)
        {
            if (_disposed || _pendingProjectionState is null)
            {
                _projectionDispatchScheduled = false;
                return;
            }
        }

        _dispatcher.Dispatch(DrainProjectionQueue);
    }

    private void ProjectLatestStateImmediately()
    {
        lock (_projectionGate)
        {
            _pendingProjectionState = null;
        }
        if (!_disposed) Project(_session.State);
    }

    private void OnMessageMutationObserved(object? sender, MessageMutationObservedEventArgs eventArgs) =>
        _dispatcher.Dispatch(() =>
        {
            if (eventArgs.Deleted || eventArgs.IsStarred is false)
            {
                var ids = eventArgs.MessageIds.ToHashSet();
                foreach (var saved in SavedMessages.Where(item => ids.Contains(item.MessageId)).ToArray())
                {
                    SavedMessages.Remove(saved);
                }
                OnPropertyChanged(nameof(HasSavedMessages));
                OnPropertyChanged(nameof(IsSavedEmpty));
            }
            else if (eventArgs.IsStarred is true)
            {
                SavedRefreshSuggested = true;
            }
        });

    private void OnRealtimeMessageReceived(object? sender, RealtimeMessageReceivedEventArgs eventArgs) =>
        _dispatcher.Dispatch(() => NotifyRealtimeMessage(eventArgs.Message));

    private void NotifyRealtimeMessage(ChatMessage message)
    {
        if (_disposed || message.IsRead || _session.CurrentUserId == message.SenderId ||
            !IsRelayCoveConversation(message.Conversation, _projectedState) ||
            IsConversationMuted(message.Conversation) || DoNotDisturb ||
            IsIncomingMessageInActiveChat(message.Conversation))
        {
            return;
        }

        var senderName = message.SenderDisplayName ??
                         _projectedState.Users.GetValueOrDefault(message.SenderId)?.FullName ??
                         $"用户 {message.SenderId}";
        var title = message.Conversation is ChannelTopic channel
            ? $"{_projectedState.Subscriptions.GetValueOrDefault(channel.ChannelId)?.Name ?? "群聊"} · {senderName}"
            : senderName;
        var body = ShowMessagePreview
            ? CreateNotificationPreview(message)
            : "收到一条新消息";

        var notification = new AppMessageNotification(
            message.Conversation.CanonicalKey,
            title,
            body,
            GetMessageAvatarUrl(message, _projectedState.Users.GetValueOrDefault(message.SenderId)));
        _appNotificationService.UpdateTrayPreview(notification);
        if (SystemNotificationsEnabled)
        {
            _appNotificationService.ShowMessageNotification(notification);
        }
        if (TaskbarFlashEnabled) _appNotificationService.FlashTaskbar();
    }

    private bool IsConversationMuted(ConversationKey conversation) => conversation switch
    {
        ChannelTopic channel => _projectedState.Subscriptions.GetValueOrDefault(channel.ChannelId)?.IsMuted == true,
        DirectMessage when _session.AccountId is { } accountId =>
            _conversationPreferencesStore.Get(accountId, conversation.CanonicalKey).IsMuted,
        _ => false
    };

    private bool IsIncomingMessageInActiveChat(ConversationKey conversation) =>
        IsApplicationWindowForeground && IsMessagesSection && IsChatPaneVisible &&
        IsConversationContentVisible && IsMessageCollectionVisible &&
        !IsModalOverlayVisible && !IsNavigationPending &&
        string.Equals(_session.SelectedConversation?.CanonicalKey, conversation.CanonicalKey, StringComparison.Ordinal);

    private bool IsApplicationWindowForeground => _windowShellAdapter?.IsForeground ?? true;

    private string CreateNotificationPreview(ChatMessage message)
    {
        var presentation = MessageContentPresentation.Parse(message.Content, _session.ActiveRealm);
        if (!string.IsNullOrWhiteSpace(presentation.Body)) return TruncateForSearch(presentation.Body);
        if (presentation.Attachments.Count > 0) return "发来一个附件";
        if (presentation.Quotes.Count > 0) return "引用了一条消息";
        return "收到一条新消息";
    }

    private void OnAppNotificationStateChanged(object? sender, EventArgs eventArgs) =>
        _dispatcher.Dispatch(() =>
        {
            OnPropertyChanged(nameof(IsSystemNotificationSupported));
            OnPropertyChanged(nameof(SystemNotificationStatus));
            OnPropertyChanged(nameof(TaskbarBadgeStatus));
        });

    private void OnAppNotificationActivated(object? sender, AppNotificationActivatedEventArgs eventArgs) =>
        _dispatcher.Dispatch(() =>
        {
            var conversation = Conversations.FirstOrDefault(item => string.Equals(
                item.Conversation.CanonicalKey,
                eventArgs.ConversationKey,
                StringComparison.Ordinal));
            if (conversation is null) return;
            _appNotificationService.StopTaskbarFlash();
            _appNotificationService.StopTrayFlash();
            ShowMessages();
            ActivateConversation(conversation);
        });

    private void Project(ClientState state)
    {
        if (!ReferenceEquals(_projectedState.RealmEmojis, state.RealmEmojis))
        {
            EmojiChoices = state.RealmEmojis.Values
                .Where(emoji => !emoji.IsDeactivated)
                .OrderBy(emoji => emoji.Name, StringComparer.Ordinal)
                .Select(emoji => new EmojiChoice(
                    $":{emoji.Name}:", emoji.Name, emoji.Name, emoji.Id, "custom", "realm_emoji",
                    emoji.StillUrl ?? emoji.SourceUrl))
                .ToArray();
            OnPropertyChanged(nameof(EmojiChoices));
            OnPropertyChanged(nameof(RealmEmojis));
            SelectEmojiCategory(EmojiCategories[0]);
        }
        var previousUsers = _projectedState.Users;
        _projectedState = state;
        NotifyMessageActionPermissions();
        if (_avatarUploadAccountId != _session.AccountId)
        {
            _avatarUploadAccountId = _session.AccountId;
            _conversationMenuActionCancellation?.Cancel();
            UploadAvatarCommand.Cancel();
            AvatarUploadStatus = null;
            SaveOwnNameCommand.Cancel();
            _nameEditAccountId = null;
            IsOwnNameEditing = false;
            OwnNameDraft = string.Empty;
            OwnNameEditStatus = null;
        }
        if (_privateGroupRosterAccountId != _session.AccountId)
        {
            _privateGroupRosterAccountId = _session.AccountId;
            _privateGroupMembers.Clear();
            _privateGroupRosterLoadAttempts.Clear();
        }
        if (_searchAccountId != _session.AccountId)
        {
            CancelSearchInput();
            _searchAccountId = _session.AccountId;
            _searchBeforeMessageId = null;
            _serverSearchResults = [];
            SearchError = null;
            ProjectSearch();
            OnPropertyChanged(nameof(HasMoreSearchResults));
        }
        if (_savedAccountId != _session.AccountId)
        {
            CancelSavedLoad();
            _savedAccountId = _session.AccountId;
            _savedBeforeMessageId = null;
            SavedMessages.Clear();
            SavedRefreshSuggested = false;
            SavedError = null;
            OnPropertyChanged(nameof(HasSavedMessages));
            OnPropertyChanged(nameof(HasMoreSavedMessages));
            OnPropertyChanged(nameof(IsSavedEmpty));
        }
        if (_downloadHistoryAccountId != _session.AccountId)
        {
            _downloadHistoryAccountId = _session.AccountId;
            LoadDownloadHistory(_downloadHistoryAccountId);
        }
        if (SavedMessages.Count > 0)
        {
            var savedIds = SavedMessages.Select(item => item.MessageId).ToHashSet();
            foreach (var saved in SavedMessages.ToArray())
            {
                if (!IsRelayCoveConversation(saved.Conversation, state) ||
                    state.Messages.TryGetValue(saved.MessageId, out var message) && !message.IsStarred)
                {
                    SavedMessages.Remove(saved);
                }
            }
            if (state.Messages.Values.Any(message => message.IsStarred && !savedIds.Contains(message.Id)))
            {
                SavedRefreshSuggested = true;
            }
            OnPropertyChanged(nameof(HasSavedMessages));
        }
        IsLoggedIn = state.Connection.Status is
            RelayCove.Core.ConnectionStatus.Connected or
            RelayCove.Core.ConnectionStatus.Offline or
            RelayCove.Core.ConnectionStatus.Reconnecting or
            RelayCove.Core.ConnectionStatus.RateLimited ||
            state.Connection.Status == RelayCove.Core.ConnectionStatus.Faulted && _session.AccountId is not null;
        ConnectionStatus = DescribeConnection(state.Connection);

        ReconcileChannelItems(
            Channels,
            state.Subscriptions.Values
                .Where(subscription => subscription.IsActive)
                .OrderByDescending(subscription => subscription.IsPinned)
                .ThenBy(subscription => subscription.IsMuted)
                .ThenBy(subscription => subscription.Name, StringComparer.Ordinal)
                .Select(subscription => CreateChannelItem(state, subscription)),
            item => item.ChannelId);

        if (_expandedChannelId is { } expandedChannelId && !Channels.Any(item => item.ChannelId == expandedChannelId))
        {
            _expandedChannelId = null;
        }
        if (ActiveChannelAction is { } activeChannel && !Channels.Any(item => item.ChannelId == activeChannel.ChannelId))
        {
            CloseChannelMenuCore(restoreFocus: false);
        }
        OnPropertyChanged(nameof(ChannelListHeight));
        OnPropertyChanged(nameof(CanManageActiveChannel));
        OnPropertyChanged(nameof(ActiveChannelMuteLabel));
        OnPropertyChanged(nameof(ActiveChannelPinLabel));
        OnPropertyChanged(nameof(ActiveChannelMarkReadLabel));

        ReconcileNavigationItems(
            DirectMessages,
            _session.RecentDirectMessages
                .OfType<DirectMessage>()
                .OrderByDescending(IsDirectMessagePinned)
                .Select(item => CreateDirectNavigationItem(state, item)),
            item => item.Conversation.CanonicalKey);

        var directConversations = _session.RecentDirectMessages
            .OfType<DirectMessage>()
            .Concat(_session.SelectedConversation is DirectMessage selectedDirect ? [selectedDirect] : [])
            .Where(static conversation => conversation.OtherUserIds.Count <= 1)
            .DistinctBy(static conversation => conversation.CanonicalKey)
            .Select(conversation => CreateDirectConversationListItem(state, conversation));
        var privateGroups = state.Subscriptions.Values
            .Where(static subscription => PrivateGroupPolicy.IsEligible(subscription))
            .Select(subscription => CreatePrivateGroupConversationListItem(state, subscription))
            .OrderByDescending(static item => item.IsPinned)
            .ThenByDescending(static item => item.LatestMessageTimestamp)
            .ThenBy(static item => item.Conversation.CanonicalKey, StringComparer.Ordinal)
            .ToArray();
        ReconcileConversationListItems(
            PrivateGroupConversations,
            privateGroups,
            item => item.Conversation.CanonicalKey);
        ReconcileConversationListItems(
            Conversations,
            directConversations
                .Concat(privateGroups)
                .Where(item => !IsConversationHidden(state, item.Conversation))
                .OrderByDescending(static item => item.IsPinned)
                .ThenByDescending(static item => item.LatestMessageTimestamp)
                .ThenBy(static item => item.Conversation.CanonicalKey, StringComparer.Ordinal),
            item => item.Conversation.CanonicalKey);
        SchedulePrivateGroupRosterLoads(state);

        ProjectConversationFilter();

        Reconcile(
            KnownContacts,
            state.Users.Values
                .Where(user => user.IsActive)
                .OrderBy(user => user.FullName, StringComparer.Ordinal)
                .ThenBy(user => user.UserId)
                .Select(user => new ContactItem(user.UserId, user.FullName, user.DisplayAvatarUrl, user.IsBot)),
            item => item.UserId);

        if (IsDetailsOpen && _session.SelectedConversation is DirectMessage selectedAvatar)
            ProjectDirectMessageAvatar(selectedAvatar);
        foreach (var user in state.Users.Values.Where(user =>
                     user.DisplayAvatarUrl != previousUsers.GetValueOrDefault(user.UserId)?.DisplayAvatarUrl))
        {
            foreach (var choice in _allNewConversationChoices.Where(choice => choice.UserId == user.UserId))
                choice.AvatarUrl = user.DisplayAvatarUrl;
            foreach (var members in new[] { DetailsMembers, GroupInviteCandidates, GroupMemberActionCandidates })
            {
                for (var index = 0; index < members.Count; index++)
                    if (members[index].UserId == user.UserId && members[index].AvatarUrl != user.DisplayAvatarUrl)
                        members[index] = members[index] with { AvatarUrl = user.DisplayAvatarUrl };
            }
        }

        ProjectTopics(state, SelectedChannel?.ChannelId);

        var selected = IsRelayCoveConversation(_session.SelectedConversation, state)
            ? _session.SelectedConversation
            : null;
        var selectedKey = selected?.CanonicalKey;
        if (IsNavigationPending &&
            !string.IsNullOrWhiteSpace(_navigationConversationKey) &&
            !string.Equals(_navigationConversationKey, selectedKey, StringComparison.Ordinal))
        {
            // The visual presentation has already switched to the cached target.
            // A projection published by the previously selected conversation must
            // not reactivate that old native tree while Core is acquiring its
            // selection lock and beginning background revalidation.
            _ = ActivateMessagePresentation(_navigationConversationKey);
            ProjectUnread(state.Unread);
            ProjectSearch();
            NotifyProjectionProperties();
            return;
        }
        _ = ActivateMessagePresentation(selectedKey);
        var conversationChanged = !string.Equals(_projectedConversationKey, selectedKey, StringComparison.Ordinal);
        var previousNewestMessageId = conversationChanged ? null : _newestProjectedMessageId;
        var transientUnreadDividerCutoff = GetTransientUnreadDividerCutoff(
            state,
            selected,
            conversationChanged,
            previousNewestMessageId);
        var projectedMessages = BuildMessageItems(state, selected, transientUnreadDividerCutoff);
        var hasImmediateConversationCache = projectedMessages.Count > 0 || Messages.Count > 0;
        var deferInitialMessageProjection = conversationChanged &&
            IsNavigationPending &&
            selectedKey is not null &&
            !hasImmediateConversationCache;
        if (deferInitialMessageProjection)
        {
            _deferredInitialMessageProjectionConversationKey = selectedKey;
        }
        else if (conversationChanged)
        {
            _deferredInitialMessageProjectionConversationKey = null;
        }

        var isDeferringInitialMessageProjection = IsNavigationPending &&
            selectedKey is not null &&
            !hasImmediateConversationCache &&
            string.Equals(
                _deferredInitialMessageProjectionConversationKey,
                selectedKey,
                StringComparison.Ordinal);
        var publishDeferredInitialMessageProjection = !isDeferringInitialMessageProjection &&
            selectedKey is not null &&
            string.Equals(
                _deferredInitialMessageProjectionConversationKey,
                selectedKey,
                StringComparison.Ordinal);
        if (conversationChanged)
        {
            if (!isDeferringInitialMessageProjection)
            {
                Reconcile(Messages, projectedMessages, item => item.Id);
            }
        }
        else if (!isDeferringInitialMessageProjection)
        {
            if (publishDeferredInitialMessageProjection)
            {
                Reconcile(Messages, projectedMessages, item => item.Id);
                _deferredInitialMessageProjectionConversationKey = null;
            }
            else
            {
                Reconcile(Messages, projectedMessages, item => item.Id);
            }
        }

        RefreshAttachmentDownloads();
        var newestMessageId = projectedMessages
            .Select(message => message.MessageId)
            .Max();
        if (conversationChanged)
        {
            NewMessageCount = 0;
            SetMessageViewportBeyondJumpThreshold(false);
            _lastAutomaticLoadOlderMilliseconds = long.MinValue;
            PendingMessageScrollRequest = null;
            if (!IsNavigationPending &&
                selectedKey is not null &&
                _session.HistoryState is { Error: null } history &&
                string.Equals(history.Conversation?.CanonicalKey, selectedKey, StringComparison.Ordinal))
            {
                _displayedConversationKey = selectedKey;
                _pendingActivationScrollConversationKey = selectedKey;
                _pendingActivationScrollGeneration = history.Generation;
                _pendingActivationScrollReason = MessageScrollReason.ConversationActivated;
            }
        }
        else if (previousNewestMessageId is { } previousNewest &&
                 !IsNavigationPending &&
                 !IsLoadingOlder)
        {
            var appendedMessages = projectedMessages
                .Where(message => message.MessageId is { } messageId && messageId > previousNewest)
                .ToArray();
            var incomingCount = appendedMessages.Count(message => !message.IsOwn);
            if (incomingCount > 0)
            {
                if (_isMessageViewportNearBottom)
                {
                    QueueScrollToLatest(MessageScrollReason.RealtimeFollow);
                }
                else
                {
                    NewMessageCount += incomingCount;
                }
            }
        }
        _projectedConversationKey = selectedKey;
        _newestProjectedMessageId = newestMessageId;

        ProjectHistoryState(selected);
        RetargetPendingScrollIfNeeded();
        TryPublishPendingActivationScroll();

        if (!IsNavigationPending) SynchronizeSelection(selected);
        ProjectConversation(selected, state);
        ProjectDraft(selected);
        ProjectUnread(state.Unread);
        ProjectSearch();
        NotifyProjectionProperties();
        RequestAutoMarkDisplayedRead(state);
        if (_activateInitialConversationWhenAvailable) ActivateInitialConversationIfAvailable();
    }

    private void SchedulePrivateGroupRosterLoads(ClientState state)
    {
        if (_session.AccountId is not { } accountId ||
            state.Connection.Status != RelayCove.Core.ConnectionStatus.Connected) return;
        var channelIds = state.Subscriptions.Values
            .Where(static subscription => PrivateGroupPolicy.IsEligible(subscription))
            .Select(static subscription => subscription.ChannelId)
            .Where(channelId => !_privateGroupMembers.ContainsKey(channelId) && _privateGroupRosterLoadAttempts.Add(channelId))
            .OrderBy(static channelId => channelId)
            .ToArray();
        if (channelIds.Length == 0) return;
        _ = LoadPrivateGroupRostersAsync(accountId, channelIds);
    }

    private async Task LoadPrivateGroupRostersAsync(AccountId accountId, IReadOnlyList<long> channelIds)
    {
        try
        {
            var users = await _session.GetRealmUsersAsync(_lifetimeCancellation.Token);
            var usersById = users.ToDictionary(static user => user.UserId);
            var loaded = new Dictionary<long, IReadOnlyList<UserProfile>>();
            foreach (var channelId in channelIds)
            {
                var memberIds = await _session.GetChannelMemberIdsAsync(channelId, _lifetimeCancellation.Token);
                if (memberIds.Any(id => !usersById.ContainsKey(id))) continue;
                loaded[channelId] = memberIds
                    .Distinct()
                    .OrderBy(static id => id)
                    .Select(id => usersById[id])
                    .ToArray();
            }
            if (loaded.Count == 0) return;

            _dispatcher.Dispatch(() =>
            {
                if (_disposed || _session.AccountId != accountId) return;
                foreach (var (channelId, members) in loaded)
                {
                    if (PrivateGroupPolicy.IsEligible(_session.State.Subscriptions.GetValueOrDefault(channelId)))
                    {
                        _privateGroupMembers[channelId] = members;
                    }
                }
                Project(_session.State);
            });
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch
        {
            // The unified list remains usable with the group-name initial.
            // Settings performs its own authoritative load and can populate the roster later.
            _dispatcher.Dispatch(() =>
            {
                if (_disposed || _session.AccountId != accountId) return;
                foreach (var channelId in channelIds) _privateGroupRosterLoadAttempts.Remove(channelId);
            });
        }
    }

    private long? GetTransientUnreadDividerCutoff(
        ClientState state,
        ConversationKey? selected,
        bool conversationChanged,
        long? previousNewestMessageId)
    {
        var history = _session.HistoryState;
        var canSuppress = !conversationChanged &&
            selected is not null &&
            _isWindowActive &&
            IsApplicationWindowForeground &&
            IsMessagesSection &&
            IsChatPaneVisible &&
            IsConversationContentVisible &&
            !IsModalOverlayVisible &&
            !IsNavigationPending &&
            _isMessageViewportNearBottom &&
            state.Connection.Status == RelayCove.Core.ConnectionStatus.Connected &&
            !history.IsLoading &&
            history.Error is null &&
            string.Equals(history.Conversation?.CanonicalKey, selected.CanonicalKey, StringComparison.Ordinal);
        if (!canSuppress)
        {
            _transientUnreadDividerSuppressionConversationKey = null;
            _transientUnreadDividerSuppressionAfterMessageId = null;
            return null;
        }

        var selectedKey = selected!.CanonicalKey;
        var existingCutoff = string.Equals(
            _transientUnreadDividerSuppressionConversationKey,
            selectedKey,
            StringComparison.Ordinal)
            ? _transientUnreadDividerSuppressionAfterMessageId
            : null;
        var cutoff = existingCutoff ?? previousNewestMessageId ?? long.MinValue;
        var hasSuppressedUnread = state.Messages.Values.Any(message =>
            message.Conversation == selected &&
            message.Id > cutoff &&
            message.SenderId != _session.CurrentUserId &&
            !message.IsRead);
        if (!hasSuppressedUnread)
        {
            _transientUnreadDividerSuppressionConversationKey = null;
            _transientUnreadDividerSuppressionAfterMessageId = null;
            return null;
        }

        _transientUnreadDividerSuppressionConversationKey = selectedKey;
        _transientUnreadDividerSuppressionAfterMessageId = cutoff;
        return cutoff;
    }

    private List<MessageItem> BuildMessageItems(
        ClientState state,
        ConversationKey? selected,
        long? suppressUnreadDividerAfterMessageId = null)
    {
        var projected = new List<MessageItem>();
        if (selected is null) return projected;
        var existingById = GetMessageItemConversationCache(selected.CanonicalKey);
        var currentUserId = _session.CurrentUserId;
        DateOnly? previousDate = null;
        var unreadDividerAdded = false;
        long? previousMessageId = null;
        long? previewUnreadAfterMessageId = null;
        string? previewUnreadDividerLabel = null;
#if DEBUG
        if (_session is NativeShellPreviewSession previewSession)
        {
            previewUnreadAfterMessageId = previewSession.UnreadDividerAfterMessageId;
            previewUnreadDividerLabel = previewSession.UnreadDividerLabel;
        }
#endif
        foreach (var message in state.Messages.Values
                     .Where(message => message.Conversation == selected)
                     .OrderBy(message => message.Id))
        {
            var user = state.Users.GetValueOrDefault(message.SenderId);
            var localTime = message.Timestamp.LocalDateTime;
            var date = DateOnly.FromDateTime(localTime);
            var isOwn = currentUserId == message.SenderId;
            var isUnread = !isOwn && !message.IsRead;
            var contributesUnreadDivider = isUnread &&
                !(suppressUnreadDividerAfterMessageId is { } cutoff && message.Id > cutoff);
            var showPreviewUnreadDivider = previewUnreadAfterMessageId is { } previewAfter &&
                previousMessageId == previewAfter;
            var mutation = state.MessageMutations.GetValueOrDefault(message.Id);
            var projectionId = string.IsNullOrWhiteSpace(message.ClientLocalId)
                ? message.Id.ToString()
                : $"local-{message.ClientLocalId}";
            var reactions = message.Reactions
                .GroupBy(reaction => reaction.Identity.CanonicalKey, StringComparer.Ordinal)
                .Select(group =>
                {
                    var identity = group.First().Identity;
                    var participants = group
                        .Select(reaction => reaction.UserFullName ?? state.Users.GetValueOrDefault(reaction.UserId)?.FullName ?? $"用户 {reaction.UserId}")
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(name => name, StringComparer.Ordinal)
                        .ToArray();
                    return new ReactionItem(
                        message.Id,
                        identity,
                        _unicodeReactionChoices.FirstOrDefault(choice =>
                            string.Equals(choice.Identity.CanonicalKey, identity.CanonicalKey, StringComparison.Ordinal))?.Emoji ?? identity.EmojiName,
                        group.Count(),
                        currentUserId is { } current && group.Any(reaction => reaction.UserId == current),
                        string.Join("、", participants));
                })
                .OrderBy(reaction => reaction.Identity.CanonicalKey, StringComparer.Ordinal)
                .ToArray();
            var item = new MessageItem(
                projectionId,
                message.Id,
                message.SenderId,
                message.SenderDisplayName ?? user?.FullName ?? $"用户 {message.SenderId}",
                message.Content,
                localTime.ToString("t"),
                isOwn,
                isUnread,
                user?.IsBot ?? false,
                GetMessageAvatarUrl(message, user),
                message.IsStarred,
                reactions,
                CreatePermalink(message.Id),
                previousDate != date,
                DescribeDate(date, localTime),
                showPreviewUnreadDivider || contributesUnreadDivider && !unreadDividerAdded,
                showPreviewUnreadDivider ? previewUnreadDividerLabel : null,
                DescribeMutation(mutation),
                mutation?.Status is MessageMutationStatus.Submitting or MessageMutationStatus.Uncertain,
                realm: _session.ActiveRealm,
                realmEmojis: state.RealmEmojis,
                isEdited: message.IsEdited);
            projected.Add(ReuseMessageItem(existingById, item));
            previousDate = date;
            previousMessageId = message.Id;
            if (showPreviewUnreadDivider || contributesUnreadDivider) unreadDividerAdded = true;
        }

        foreach (var entry in state.Outbox.Values.Concat(_unsubmittedMessages.Values)
                     .Where(entry => entry.Conversation == selected)
                     .OrderBy(entry => entry.CreatedAt))
        {
            var localTime = entry.CreatedAt.LocalDateTime;
            var date = DateOnly.FromDateTime(localTime);
            var currentUser = currentUserId is { } ownUserId
                ? state.Users.GetValueOrDefault(ownUserId)
                : null;
            var item = new MessageItem(
                $"local-{entry.LocalId}",
                null,
                currentUserId,
                currentUser?.FullName ?? "你",
                entry.Content,
                localTime.ToString("t"),
                isOwn: true,
                isBot: currentUser?.IsBot ?? false,
                senderAvatarUrl: currentUser?.DisplayAvatarUrl,
                showDateDivider: previousDate != date,
                dateDividerLabel: DescribeDate(date, localTime),
                deliveryState: DescribeOutbox(entry),
                isDeliveryFailure: IsOutboxDeliveryFailure(entry),
                canRecover: entry.State is OutboxState.WaitExpired or OutboxState.Failed,
                recoverCommand: RecoverOutboxCommand,
                realm: _session.ActiveRealm,
                animateInsertion: entry.State == OutboxState.Hidden,
                realmEmojis: state.RealmEmojis);
            projected.Add(ReuseMessageItem(existingById, item));
            previousDate = date;
        }

        var projectedIds = projected.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var staleId in existingById.Keys.Where(id => !projectedIds.Contains(id)).ToArray())
        {
            existingById.Remove(staleId);
        }

        return projected;
    }

    private static MessageItem ReuseMessageItem(
        IDictionary<string, MessageItem> existingById,
        MessageItem candidate)
    {
        if (!existingById.TryGetValue(candidate.Id, out var existing))
        {
            existingById[candidate.Id] = candidate;
            return candidate;
        }

        existing.ApplyFrom(candidate);
        return existing;
    }

    private bool ActivateMessagePresentation(string? conversationKey)
    {
        ResetMessagePresentationCacheForAccount();
        if (string.IsNullOrWhiteSpace(conversationKey))
        {
            if (_activeMessagePresentation is null) return false;
            _activeMessagePresentation.IsActive = false;
            _activeMessagePresentation = null;
            OnPropertyChanged(nameof(Messages));
            NotifyConversationAvailability();
            return false;
        }

        var presentationExisted = _messagePresentationsByConversation.TryGetValue(conversationKey, out var existingPresentation);
        var presentation = existingPresentation;
        if (presentation is null)
        {
            presentation = new ConversationMessagePresentation(conversationKey, this);
            _messagePresentationsByConversation[conversationKey] = presentation;
            MessagePresentations.Add(presentation);
        }

        var shouldDeferRevalidation = presentationExisted &&
            presentation.Messages.Count > 0 &&
            !ReferenceEquals(_activeMessagePresentation, presentation);
        TouchMessagePresentation(conversationKey);
        if (!ReferenceEquals(_activeMessagePresentation, presentation))
        {
            if (_activeMessagePresentation is not null) _activeMessagePresentation.IsActive = false;
            _activeMessagePresentation = presentation;
            presentation.IsActive = true;
            OnPropertyChanged(nameof(Messages));
            NotifyConversationAvailability();
        }

        TrimMessagePresentations();
        return shouldDeferRevalidation;
    }

    private void ResetMessagePresentationCacheForAccount()
    {
        if (_messagePresentationAccountId == _session.AccountId) return;
        _unsubmittedMessages.Clear();
        _activeMessagePresentation = null;
        _messagePresentationsByConversation.Clear();
        _messagePresentationLru.Clear();
        MessagePresentations.Clear();
        _messagePresentationAccountId = _session.AccountId;
        OnPropertyChanged(nameof(Messages));
    }

    private void TouchMessagePresentation(string conversationKey)
    {
        var existing = _messagePresentationLru.Find(conversationKey);
        if (existing is not null) _messagePresentationLru.Remove(existing);
        _messagePresentationLru.AddLast(conversationKey);
    }

    private void TrimMessagePresentations()
    {
        while (_messagePresentationsByConversation.Count > MessagePresentationCacheLimit &&
               _messagePresentationLru.First is { } oldest)
        {
            _messagePresentationLru.RemoveFirst();
            if (!_messagePresentationsByConversation.Remove(oldest.Value, out var presentation)) continue;
            MessagePresentations.Remove(presentation);
        }
    }

    private Dictionary<string, MessageItem> GetMessageItemConversationCache(string conversationKey)
    {
        if (_messageItemCacheAccountId != _session.AccountId)
        {
            _messageItemsByConversation.Clear();
            _messageItemConversationLru.Clear();
            _messageItemCacheAccountId = _session.AccountId;
        }

        if (!_messageItemsByConversation.TryGetValue(conversationKey, out var items))
        {
            items = new Dictionary<string, MessageItem>(StringComparer.Ordinal);
            _messageItemsByConversation[conversationKey] = items;
        }

        var existingNode = _messageItemConversationLru.Find(conversationKey);
        if (existingNode is not null) _messageItemConversationLru.Remove(existingNode);
        _messageItemConversationLru.AddLast(conversationKey);
        while (_messageItemsByConversation.Count > MessageItemConversationCacheLimit &&
               _messageItemConversationLru.First is { } oldest)
        {
            _messageItemConversationLru.RemoveFirst();
            _messageItemsByConversation.Remove(oldest.Value);
        }

        return items;
    }

    private async Task RunServerSearchAsync(string query, CancellationToken cancellationToken)
    {
        CancelSearchInput();
        var filter = SelectedSearchFilter;
        if (!IsSearchOpen) return;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _searchInputCancellation = cancellation;
        var accountId = _session.AccountId;
        if (accountId is null)
        {
            CancelSearchInput();
            return;
        }
        _searchAccountId = accountId;
        await RunServerSearchCoreAsync(
            query.Trim(),
            filter,
            debounce: false,
            ++_searchInputGeneration,
            accountId.Value,
            cancellation).ConfigureAwait(false);
    }

    private void ScheduleServerSearch(string query)
    {
        if (!IsSearchOpen || string.IsNullOrWhiteSpace(query)) return;

        CancelSearchInput();
        var cancellation = new CancellationTokenSource();
        _searchInputCancellation = cancellation;
        var accountId = _session.AccountId;
        if (accountId is null)
        {
            CancelSearchInput();
            return;
        }

        _searchAccountId = accountId;
        IsSearchBusy = true;
        _ = RunServerSearchCoreAsync(
            query.Trim(),
            SelectedSearchFilter,
            debounce: true,
            ++_searchInputGeneration,
            accountId.Value,
            cancellation);
    }

    private async Task RunServerSearchCoreAsync(
        string query,
        MessageSearchFilter filter,
        bool debounce,
        long generation,
        AccountId accountId,
        CancellationTokenSource cancellation)
    {
        try
        {
            if (!IsSearchCurrent(generation, accountId) || !IsSearchOpen) return;
            _hasSubmittedSearch = true;
            IsSearchBusy = true;
            SearchError = null;
            _searchBeforeMessageId = null;
            if (debounce) await Task.Delay(TimeSpan.FromMilliseconds(300), cancellation.Token).ConfigureAwait(false);
            var page = await _session.SearchMessagesAsync(
                query,
                null,
                50,
                cancellation.Token,
                filter,
                _searchConversation).ConfigureAwait(false);
            _dispatcher.Dispatch(() =>
            {
                if (!IsSearchCurrent(generation, accountId) ||
                    !IsSearchOpen ||
                    SelectedSearchFilter != filter ||
                    !string.Equals(SearchQuery.Trim(), query, StringComparison.Ordinal)) return;
                _serverSearchResults = page.Messages
                    .Where(message => IsRelayCoveConversation(message.Conversation, _projectedState))
                    .OrderByDescending(message => message.Id)
                    .Select(message => ToSearchResult(message, filter))
                    .ToArray();
                ProjectSearch();
                _searchBeforeMessageId = page.FoundOldest ? null : page.OldestFetchedMessageId;
                OnPropertyChanged(nameof(HasMoreSearchResults));
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (GatewayException exception)
        {
            _dispatcher.Dispatch(() =>
            {
                if (IsSearchCurrent(generation, accountId)) SearchError = DescribeGatewayFailure(exception);
            });
        }
        catch (Exception)
        {
            _dispatcher.Dispatch(() =>
            {
                if (IsSearchCurrent(generation, accountId)) SearchError = "服务器搜索失败，请稍后重试。";
            });
        }
        finally
        {
            _dispatcher.Dispatch(() =>
            {
                if (IsSearchCurrent(generation, accountId)) IsSearchBusy = false;
                if (ReferenceEquals(_searchInputCancellation, cancellation)) _searchInputCancellation = null;
                cancellation.Dispose();
            });
        }
    }

    private async Task StartSavedLoadAsync(bool replace, CancellationToken cancellationToken)
    {
        var prior = _savedLoadCancellation;
        prior?.Cancel();
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _savedLoadCancellation = linked;
        var generation = ++_savedLoadGeneration;
        var accountId = _session.AccountId;
        if (accountId is null)
        {
            if (ReferenceEquals(_savedLoadCancellation, linked)) _savedLoadCancellation = null;
            linked.Dispose();
            return;
        }
        if (replace)
        {
            _savedBeforeMessageId = null;
            SavedRefreshSuggested = false;
        }
        await LoadSavedPageAsync(replace, accountId.Value, generation, linked).ConfigureAwait(false);
    }

    private async Task LoadSavedPageAsync(
        bool replace,
        AccountId accountId,
        long generation,
        CancellationTokenSource cancellation)
    {
        try
        {
            IsSavedLoading = true;
            SavedError = null;
            var beforeMessageId = replace ? null : _savedBeforeMessageId;
            var page = await _session.LoadSavedMessagesAsync(beforeMessageId, 50, cancellation.Token).ConfigureAwait(false);
            if (!IsSavedLoadCurrent(accountId, generation)) return;
            if (!replace && !page.FoundAnchor)
            {
                _savedBeforeMessageId = null;
                SavedError = "已保存消息已变化，请刷新列表。";
                OnPropertyChanged(nameof(HasMoreSavedMessages));
                return;
            }
            var items = page.Messages
                .Where(message => IsRelayCoveConversation(message.Conversation, _projectedState))
                .OrderByDescending(message => message.Id)
                .Select(ToSavedMessage)
                .ToArray();
            if (replace)
            {
                Reconcile(SavedMessages, items, item => item.MessageId);
            }
            else
            {
                var known = SavedMessages.Select(item => item.MessageId).ToHashSet();
                foreach (var item in items.Where(item => known.Add(item.MessageId))) SavedMessages.Add(item);
            }
            _savedBeforeMessageId = page.FoundOldest ? null : page.Messages.MinBy(message => message.Id)?.Id;
            _savedAccountId = accountId;
            OnPropertyChanged(nameof(HasSavedMessages));
            OnPropertyChanged(nameof(HasMoreSavedMessages));
            OnPropertyChanged(nameof(IsSavedEmpty));
        }
        catch (OperationCanceledException)
        {
        }
        catch (GatewayException exception)
        {
            if (IsSavedLoadCurrent(accountId, generation)) SavedError = DescribeGatewayFailure(exception);
        }
        catch (Exception)
        {
            if (IsSavedLoadCurrent(accountId, generation)) SavedError = "已保存消息读取失败，请稍后重试。";
        }
        finally
        {
            if (IsSavedLoadCurrent(accountId, generation))
            {
                IsSavedLoading = false;
                if (ReferenceEquals(_savedLoadCancellation, cancellation)) _savedLoadCancellation = null;
                OnPropertyChanged(nameof(IsSavedEmpty));
            }
            cancellation.Dispose();
        }
    }

    private bool IsSavedLoadCurrent(AccountId accountId, long generation) =>
        generation == _savedLoadGeneration && _session.AccountId == accountId;

    private bool IsSearchCurrent(long generation, AccountId accountId) =>
        generation == _searchInputGeneration && _session.AccountId == accountId;

    private void CancelSavedLoad()
    {
        _savedLoadGeneration++;
        _savedLoadCancellation?.Cancel();
        _savedLoadCancellation = null;
    }

    private SearchResultItem ToSearchResult(ChatMessage message, MessageSearchFilter filter)
    {
        var sender = message.SenderDisplayName ?? _projectedState.Users.GetValueOrDefault(message.SenderId)?.FullName ?? $"用户 {message.SenderId}";
        var contentKinds = SearchContentClassifier.Classify(message.Content, _session.ActiveRealm);
        var presentation = MessageContentPresentation.Parse(message.Content, _session.ActiveRealm);
        var images = presentation.Attachments.Where(attachment => attachment.IsImage).ToArray();
        var subtitle = images.Length == 0
            ? message.Content
            : string.Join("\n", new[] { MessageContentPresentation.ToPlainText(presentation.Body, _session.ActiveRealm) }
                .Concat(presentation.Attachments.Where(attachment => attachment.IsFile)
                    .Select(attachment => $"[文件] {attachment.Name}")));
        return new SearchResultItem(
            $"server-message:{message.Id}",
            DescribeSearchResultKind(filter, "消息"),
            sender,
            TruncateForSearch(subtitle),
            message.Conversation,
            message.Id,
            ContentKinds: contentKinds,
            TimestampText: message.Timestamp.LocalDateTime.ToString("yyyy-MM-dd HH:mm"))
        {
            Images = images
        };
    }

    private SavedMessageItem ToSavedMessage(ChatMessage message)
    {
        var sender = message.SenderDisplayName ?? _projectedState.Users.GetValueOrDefault(message.SenderId)?.FullName ?? $"用户 {message.SenderId}";
        return new SavedMessageItem(message.Id, message.Conversation, sender, TruncateForSearch(message.Content), message.Timestamp.LocalDateTime.ToString("g"));
    }

    private static bool IsRelayCoveConversation(ConversationKey? conversation, ClientState state) => conversation switch
    {
        DirectMessage direct => direct.OtherUserIds.Count <= 1,
        ChannelTopic { Topic.Length: 0 } channel =>
            PrivateGroupPolicy.IsEligible(state.Subscriptions.GetValueOrDefault(channel.ChannelId)),
        _ => false
    };

    private void CancelSearchInput()
    {
        _searchInputCancellation?.Cancel();
        _searchInputCancellation = null;
        _searchInputGeneration++;
    }

    private void ProjectSearch()
    {
        var filter = SearchCategories.FirstOrDefault(category => category.IsSelected)?.Filter;
        if (!IsSearchOpen || !_hasSubmittedSearch)
        {
            Reconcile(SearchResults, [], item => item.Id);
            OnPropertyChanged(nameof(HasSearchResults));
            OnPropertyChanged(nameof(IsSearchEmpty));
            return;
        }
        IEnumerable<SearchResultItem> localMatches = _searchConversation is null
            ? []
            : _projectedState.Messages.Values
                .Where(message => message.Conversation.CanonicalKey == _searchConversation.CanonicalKey &&
                                  (string.IsNullOrWhiteSpace(SearchQuery) || Contains(message.Content, SearchQuery.Trim())) &&
                                  MatchesSearchFilter(SearchContentClassifier.Classify(message.Content, _session.ActiveRealm), filter))
                .OrderByDescending(message => message.Id)
                .Select(message => ToSearchResult(message, SelectedSearchFilter));
        Reconcile(
            SearchResults,
            _serverSearchResults
                .Where(result => IsRelayCoveConversation(result.Conversation, _projectedState) &&
                                 (_searchConversation is null ||
                                  result.Conversation?.CanonicalKey == _searchConversation.CanonicalKey) &&
                                 MatchesSearchFilter(result.ContentKinds, filter))
                .Concat(localMatches)
                .DistinctBy(result => result.MessageId),
            item => item.Id);
        OnPropertyChanged(nameof(HasSearchResults));
        OnPropertyChanged(nameof(IsSearchEmpty));
    }

    private MessageSearchFilter SelectedSearchFilter =>
        SearchCategories.FirstOrDefault(category => category.IsSelected)?.Filter ?? MessageSearchFilter.Messages;

    private static bool MatchesSearchFilter(SearchContentKind contentKinds, MessageSearchFilter? filter) => filter switch
    {
        null => true,
        MessageSearchFilter.Messages => contentKinds == SearchContentKind.Message,
        MessageSearchFilter.Files => contentKinds.HasFlag(SearchContentKind.File),
        MessageSearchFilter.Images => contentKinds.HasFlag(SearchContentKind.Image),
        MessageSearchFilter.Videos => contentKinds.HasFlag(SearchContentKind.Video),
        MessageSearchFilter.Links => contentKinds.HasFlag(SearchContentKind.Link),
        _ => false
    };

    private static string DescribeSearchResultKind(MessageSearchFilter filter, string messageLabel) => filter switch
    {
        MessageSearchFilter.Files => "文件",
        MessageSearchFilter.Images => "图片",
        MessageSearchFilter.Videos => "视频",
        MessageSearchFilter.Links => "链接",
        _ => messageLabel
    };

    private void ProjectNewConversationChoices()
    {
        var query = NewConversationQuery.Trim();
        Reconcile(
            NewConversationChoices,
            _allNewConversationChoices.Where(choice =>
                (!IsNewChannelConversationMode || !choice.IsSelf) &&
                (query.Length == 0 || Contains(choice.Name, query) || choice.IsSelf && Contains("自己", query))),
            choice => choice.UserId);
        OnPropertyChanged(nameof(HasNewConversationChoices));
        OnPropertyChanged(nameof(IsNewConversationChoiceEmpty));
        OnPropertyChanged(nameof(IsNewConversationChoicesVisible));
        OnPropertyChanged(nameof(IsNewConversationChoiceEmptyVisible));
        OnPropertyChanged(nameof(CanStartNewConversation));
    }

    private void ProjectConversationFilter()
    {
        var query = ConversationFilterQuery.Trim();
        Reconcile(
            FilteredChannels,
            Channels.Where(item => query.Length == 0 || Contains(item.DisplayTitle, query) || Contains(item.Detail, query)),
            item => item.ChannelId);
        Reconcile(
            FilteredDirectMessages,
            DirectMessages.Where(item => query.Length == 0 || Contains(item.Title, query) || Contains(item.Detail, query)),
            item => item.Conversation.CanonicalKey);
        ReconcileConversationListItems(
            FilteredConversations,
            BuildConversationFilterResults(query),
            item => item.ProjectionKey);
        OnPropertyChanged(nameof(ChannelListHeight));
        OnPropertyChanged(nameof(ConversationFilterEmptyText));
    }

    private IReadOnlyList<ConversationListItem> BuildConversationFilterResults(string query)
    {
        if (query.Length == 0) return Conversations.ToArray();

        var results = new List<ConversationListItem>();
        var included = new HashSet<string>(StringComparer.Ordinal);
        void Add(ConversationListItem item)
        {
            if (included.Add(item.ProjectionKey)) results.Add(item);
        }

        foreach (var conversation in Conversations.Where(item => MatchesConversationIdentity(item, query)))
        {
            Add(conversation);
        }

        foreach (var message in _projectedState.Messages.Values
                     .Where(message => IsRelayCoveConversation(message.Conversation, _projectedState) &&
                                       (Contains(message.Content, query) ||
                                        Contains(message.SenderDisplayName, query) ||
                                        Contains(_projectedState.Users.GetValueOrDefault(message.SenderId)?.FullName, query)))
                     .OrderByDescending(message => message.Id))
        {
            if (CreateConversationFilterMatch(message) is { } match) Add(match);
        }

        foreach (var conversation in Conversations.Where(item =>
                     Contains(item.Detail, query) &&
                     !results.Any(result => string.Equals(
                         result.Conversation.CanonicalKey,
                         item.Conversation.CanonicalKey,
                         StringComparison.Ordinal))))
        {
            Add(conversation);
        }

        if (string.Equals(_conversationFilterServerQuery, query, StringComparison.Ordinal) &&
            _conversationFilterAccountId == _session.AccountId)
        {
            foreach (var message in _conversationFilterServerMatches.Values.OrderByDescending(message => message.Id))
            {
                if (CreateConversationFilterMatch(message) is { } match) Add(match);
            }
        }

        return results;
    }

    private bool MatchesConversationIdentity(ConversationListItem item, string query)
    {
        if (Contains(item.Title, query)) return true;
        return item.Conversation is ChannelTopic channel &&
               _privateGroupMembers.GetValueOrDefault(channel.ChannelId)?
                   .Any(member => Contains(member.FullName, query)) == true;
    }

    private ConversationListItem? CreateConversationFilterMatch(ChatMessage message)
    {
        ConversationListItem? conversation = message.Conversation switch
        {
            DirectMessage { OtherUserIds.Count: <= 1 } direct =>
                CreateDirectConversationListItem(_projectedState, direct),
            ChannelTopic { Topic.Length: 0 } channel
                when PrivateGroupPolicy.IsEligible(
                    _projectedState.Subscriptions.GetValueOrDefault(channel.ChannelId)) =>
                CreatePrivateGroupConversationListItem(
                    _projectedState,
                    _projectedState.Subscriptions[channel.ChannelId]),
            _ => null
        };
        if (conversation is null) return null;
        var sender = message.SenderDisplayName ??
                     _projectedState.Users.GetValueOrDefault(message.SenderId)?.FullName ??
                     $"用户 {message.SenderId}";
        var detail = message.Conversation is ChannelTopic
            ? $"{sender}: {TruncateForSearch(message.Content)}"
            : TruncateForSearch(message.Content);
        return conversation.WithSearchMatch(message.Id, detail, message.Timestamp);
    }

    private void StartConversationFilterSearch(string value)
    {
        CancelConversationFilterSearch();
        _conversationFilterServerMatches = new Dictionary<long, ChatMessage>();
        _conversationFilterBeforeMessageId = null;
        OnPropertyChanged(nameof(HasMoreConversationFilterResults));
        OnPropertyChanged(nameof(ShowMoreConversationFilterResults));
        ConversationFilterError = null;
        ProjectConversationFilter();

        var query = value.Trim();
        var accountId = _session.AccountId;
        if (query.Length == 0 || accountId is null) return;

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        _conversationFilterCancellation = cancellation;
        _conversationFilterAccountId = accountId;
        _conversationFilterServerQuery = query;
        var generation = ++_conversationFilterGeneration;
        IsConversationFilterBusy = true;
        _ = RunConversationFilterSearchAsync(
            query,
            null,
            append: false,
            generation,
            accountId.Value,
            cancellation);
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private Task LoadMoreConversationFilterAsync()
    {
        var query = ConversationFilterQuery.Trim();
        var accountId = _session.AccountId;
        if (_conversationFilterBeforeMessageId is not { } beforeMessageId ||
            query.Length == 0 || accountId is null || IsConversationFilterBusy)
        {
            return Task.CompletedTask;
        }

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        _conversationFilterCancellation = cancellation;
        _conversationFilterAccountId = accountId;
        _conversationFilterServerQuery = query;
        var generation = ++_conversationFilterGeneration;
        IsConversationFilterBusy = true;
        return RunConversationFilterSearchAsync(
            query,
            beforeMessageId,
            append: true,
            generation,
            accountId.Value,
            cancellation);
    }

    private async Task RunConversationFilterSearchAsync(
        string query,
        long? beforeMessageId,
        bool append,
        long generation,
        AccountId accountId,
        CancellationTokenSource cancellation)
    {
        try
        {
            if (!append) await Task.Delay(TimeSpan.FromMilliseconds(300), cancellation.Token).ConfigureAwait(false);
            var page = await _session.SearchMessagesAsync(query, beforeMessageId, 50, cancellation.Token).ConfigureAwait(false);
            var matches = page.Messages
                .Where(message => IsRelayCoveConversation(message.Conversation, _projectedState))
                .GroupBy(message => message.Id)
                .Select(group => group.First())
                .ToDictionary(message => message.Id);
            long? nextBeforeMessageId = !page.FoundOldest && page.Messages.Count > 0
                ? page.Messages.Min(message => message.Id)
                : null;
            _dispatcher.Dispatch(() => CompleteConversationFilterSearch(
                query,
                generation,
                accountId,
                cancellation,
                matches,
                nextBeforeMessageId,
                append,
                null));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (GatewayException exception)
        {
            _dispatcher.Dispatch(() => CompleteConversationFilterSearch(
                query,
                generation,
                accountId,
                cancellation,
                null,
                beforeMessageId,
                append,
                DescribeGatewayFailure(exception)));
        }
        catch (Exception)
        {
            _dispatcher.Dispatch(() => CompleteConversationFilterSearch(
                query,
                generation,
                accountId,
                cancellation,
                null,
                beforeMessageId,
                append,
                "服务器搜索失败；已显示本机匹配结果。"));
        }
    }

    private void CompleteConversationFilterSearch(
        string query,
        long generation,
        AccountId accountId,
        CancellationTokenSource cancellation,
        IReadOnlyDictionary<long, ChatMessage>? matches,
        long? nextBeforeMessageId,
        bool append,
        string? error)
    {
        if (!IsConversationFilterSearchCurrent(query, generation, accountId, cancellation)) return;
        _conversationFilterCancellation = null;
        if (matches is not null)
        {
            if (append)
            {
                var merged = new Dictionary<long, ChatMessage>(_conversationFilterServerMatches);
                foreach (var (key, message) in matches)
                {
                    merged[key] = message;
                }
                _conversationFilterServerMatches = merged;
            }
            else
            {
                _conversationFilterServerMatches = matches;
            }
            _conversationFilterBeforeMessageId = nextBeforeMessageId;
            OnPropertyChanged(nameof(HasMoreConversationFilterResults));
            OnPropertyChanged(nameof(ShowMoreConversationFilterResults));
        }
        ConversationFilterError = error;
        IsConversationFilterBusy = false;
        ProjectConversationFilter();
        cancellation.Dispose();
    }

    private bool IsConversationFilterSearchCurrent(
        string query,
        long generation,
        AccountId accountId,
        CancellationTokenSource cancellation) =>
        !_disposed && !cancellation.IsCancellationRequested &&
        ReferenceEquals(_conversationFilterCancellation, cancellation) &&
        generation == _conversationFilterGeneration &&
        _session.AccountId == accountId &&
        string.Equals(ConversationFilterQuery.Trim(), query, StringComparison.Ordinal);

    private void CancelConversationFilterSearch()
    {
        var cancellation = Interlocked.Exchange(ref _conversationFilterCancellation, null);
        _conversationFilterAccountId = null;
        _conversationFilterServerQuery = null;
        _conversationFilterGeneration++;
        IsConversationFilterBusy = false;
        if (cancellation is null) return;
        cancellation.Cancel();
        cancellation.Dispose();
    }

    private void NotifyConversationFilterStatusProperties()
    {
        OnPropertyChanged(nameof(HasConversationFilterStatus));
        OnPropertyChanged(nameof(ConversationFilterStatus));
        OnPropertyChanged(nameof(ConversationFilterEmptyText));
        OnPropertyChanged(nameof(ShowMoreConversationFilterResults));
    }

    private void OnNewConversationChoiceChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(ConversationContactChoice.IsSelected))
        {
            if (sender is ConversationContactChoice selected && selected.IsSelected && IsNewDirectConversationMode)
            {
                foreach (var other in _allNewConversationChoices.Where(choice => !ReferenceEquals(choice, selected) && choice.IsSelected))
                    other.IsSelected = false;
            }
            OnPropertyChanged(nameof(CanStartNewConversation));
            OnPropertyChanged(nameof(CanStartNewChannelConversation));
            OnPropertyChanged(nameof(PrivateGroupCreateDisabledReason));
            OnPropertyChanged(nameof(ShowPrivateGroupCreateDisabledReason));
        }
    }

    private void ClearNewConversationChoices()
    {
        foreach (var choice in _allNewConversationChoices)
        {
            choice.PropertyChanged -= OnNewConversationChoiceChanged;
        }
        _allNewConversationChoices.Clear();
        Reconcile(NewConversationChoices, [], choice => choice.UserId);
        OnPropertyChanged(nameof(HasNewConversationChoices));
        OnPropertyChanged(nameof(IsNewConversationChoiceEmpty));
        OnPropertyChanged(nameof(IsNewConversationChoicesVisible));
        OnPropertyChanged(nameof(IsNewConversationChoiceEmptyVisible));
        OnPropertyChanged(nameof(CanStartNewConversation));
        OnPropertyChanged(nameof(CanStartNewChannelConversation));
    }

    private void ProjectTopics(ClientState state, long? selectedChannelId)
    {
        if (selectedChannelId is null)
        {
            Reconcile(Topics, [], item => item.CanonicalKey);
            UpdateExpandedChannelTopicCount();
            OnPropertyChanged(nameof(HasTopics));
            OnPropertyChanged(nameof(ShowTopicPicker));
            OnPropertyChanged(nameof(ShowEmptyChannelTopicState));
            OnPropertyChanged(nameof(TopicListHeight));
            OnPropertyChanged(nameof(ChannelListHeight));
            return;
        }

        var authoritativeTopicsAvailable = _hasAuthoritativeTopics && _loadedTopicsChannelId == selectedChannelId;
        var topicSource = authoritativeTopicsAvailable
            ? _loadedTopics.Where(topic => topic.ChannelId == selectedChannelId)
            : state.Topics.Values
                .Where(topic => topic.ChannelId == selectedChannelId)
                .Concat(_loadedTopics.Where(topic => topic.ChannelId == selectedChannelId));
        var topicMap = topicSource
            .GroupBy(topic => new ChannelTopic(topic.ChannelId, topic.Topic).CanonicalKey, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(topic => topic.MaxMessageId).First())
            .ToDictionary(
                topic => new ChannelTopic(topic.ChannelId, topic.Topic).CanonicalKey,
                topic => topic,
                StringComparer.Ordinal);

        if (!authoritativeTopicsAvailable &&
            _session.SelectedConversation is ChannelTopic selected &&
            selected.ChannelId == selectedChannelId)
        {
            topicMap.TryAdd(selected.CanonicalKey, new TopicSummary(selected.ChannelId, selected.Topic));
        }

        ReconcileTopicItems(
            Topics,
            topicMap.Values
                .OrderByDescending(topic => topic.MaxMessageId)
                .ThenBy(topic => topic.Topic, StringComparer.Ordinal)
                .Select(topic => new TopicItem(
                    topic.ChannelId,
                    topic.Topic,
                    topic.MaxMessageId,
                    GetConversationUnread(state.Unread, new ChannelTopic(topic.ChannelId, topic.Topic)),
                    string.Equals(SelectedTopic?.CanonicalKey, new ChannelTopic(topic.ChannelId, topic.Topic).CanonicalKey, StringComparison.Ordinal),
                    topic.VisibilityPolicy,
                    TopicResolution.IsResolved(topic.Topic))),
            item => item.CanonicalKey);
        UpdateExpandedChannelTopicCount();
        OnPropertyChanged(nameof(HasTopics));
        OnPropertyChanged(nameof(ShowTopicPicker));
        OnPropertyChanged(nameof(ShowEmptyChannelTopicState));
        OnPropertyChanged(nameof(TopicListHeight));
        OnPropertyChanged(nameof(ChannelListHeight));
    }

    private void SelectOnlyTopicWhenUnambiguous()
    {
        if (Topics.Count == 1 && !string.Equals(
                SelectedTopic?.CanonicalKey,
                Topics[0].CanonicalKey,
                StringComparison.Ordinal))
        {
            SelectedTopic = Topics[0];
        }
    }

    private ChannelItem CreateChannelItem(ClientState state, Subscription subscription)
    {
        var recentTopic = state.Topics.Values
            .Where(topic => topic.ChannelId == subscription.ChannelId)
            .OrderByDescending(topic => topic.MaxMessageId)
            .FirstOrDefault();
        var conversation = recentTopic is null
            ? null
            : new ChannelTopic(subscription.ChannelId, recentTopic.Topic);
        var latestMessage = conversation is not null && state.ConversationSummaries.TryGetValue(conversation.CanonicalKey, out var summary)
            ? summary.LatestMessage
            : conversation is null
                ? null
                : state.Messages.Values
                .Where(message => message.Conversation == conversation)
                .OrderByDescending(message => message.Id)
                .FirstOrDefault();
        var sender = latestMessage is null
            ? null
            : latestMessage.SenderDisplayName ??
              state.Users.GetValueOrDefault(latestMessage.SenderId)?.FullName ??
              $"用户 {latestMessage.SenderId}";
        var preview = latestMessage is null
            ? null
            : $"{sender}: {CreateConversationPreview(latestMessage.Content)}";

        return new ChannelItem(
            subscription.ChannelId,
            subscription.Name,
            GetChannelUnread(state.Unread, subscription.ChannelId),
            recentTopic?.Topic,
            preview,
            latestMessage is null ? null : FormatConversationTimestamp(latestMessage.Timestamp.LocalDateTime),
            subscription.IsMuted,
            subscription.IsPinned,
            SelectedChannel?.ChannelId == subscription.ChannelId,
            _expandedChannelId == subscription.ChannelId,
            subscription.Color);
    }

    private void SynchronizeSelection(ConversationKey? selected)
    {
        var unified = selected is null
            ? null
            : Conversations.FirstOrDefault(item => string.Equals(
                item.Conversation.CanonicalKey,
                selected.CanonicalKey,
                StringComparison.Ordinal));
        if (!string.Equals(
                SelectedConversationItem?.Conversation.CanonicalKey,
                unified?.Conversation.CanonicalKey,
                StringComparison.Ordinal))
        {
            SelectedConversationItem = unified;
        }

        switch (selected)
        {
            case ChannelTopic channelTopic:
                {
                    var channel = Channels.FirstOrDefault(item => item.ChannelId == channelTopic.ChannelId);
                    var selectedChannelStillExists = SelectedChannel is not null &&
                        Channels.Any(item => item.ChannelId == SelectedChannel.ChannelId);
                    if (!selectedChannelStillExists && SelectedChannel?.ChannelId != channel?.ChannelId)
                    {
                        SelectedChannel = channel;
                    }

                    var topic = Topics.FirstOrDefault(item =>
                        string.Equals(item.CanonicalKey, channelTopic.CanonicalKey, StringComparison.Ordinal));
                    if (!string.Equals(SelectedTopic?.CanonicalKey, topic?.CanonicalKey, StringComparison.Ordinal))
                    {
                        SelectedTopic = topic;
                    }

                    if (SelectedDirectMessage is not null)
                    {
                        SelectedDirectMessage = null;
                    }
                    break;
                }
            case DirectMessage:
                {
                    var direct = DirectMessages.FirstOrDefault(item =>
                        string.Equals(item.Conversation.CanonicalKey, selected.CanonicalKey, StringComparison.Ordinal));
                    if (!string.Equals(SelectedDirectMessage?.Conversation.CanonicalKey, direct?.Conversation.CanonicalKey, StringComparison.Ordinal))
                    {
                        SelectedDirectMessage = direct;
                    }

                    if (SelectedChannel is not null) SelectedChannel = null;
                    if (SelectedTopic is not null) SelectedTopic = null;
                    break;
                }
            default:
                if (SelectedChannel is not null) SelectedChannel = null;
                if (SelectedTopic is not null) SelectedTopic = null;
                if (SelectedDirectMessage is not null) SelectedDirectMessage = null;
                break;
        }
    }

    private void ProjectConversation(ConversationKey? selected, ClientState state)
    {
        if (!IsRelayCoveConversation(selected, state)) selected = null;
        switch (selected)
        {
            case ChannelTopic channelTopic:
                {
                    var subscription = state.Subscriptions.GetValueOrDefault(channelTopic.ChannelId);
                    var channelName = subscription?.Name ?? $"频道 {channelTopic.ChannelId}";
                    var unreadCount = GetConversationUnread(state.Unread, channelTopic);
                    if (channelTopic.Topic.Length == 0 && PrivateGroupPolicy.IsEligible(subscription))
                    {
                        var memberCount = _privateGroupMembers.GetValueOrDefault(channelTopic.ChannelId)?.Count;
                        ConversationTitle = channelName;
                        ConversationSubtitle = memberCount is > 0 ? $"{memberCount} 位成员" : "群聊";
                        DetailsTitle = channelName;
                        DetailsBody = ConversationSubtitle;
                        DetailsKindLabel = "私有群聊";
                        DetailsGlyph = string.Empty;
                        DetailsIdentifierLabel = memberCount is > 0
                            ? $"私有群聊 · {memberCount} 位成员"
                            : "私有群聊";
                        DetailsStateLabel = unreadCount > 0 ? $"未读 {unreadCount} 条" : "无未读";
                        DetailsAvailableMessage = string.Empty;
                        DetailsUnavailableMessage = string.Empty;
                        ShowChannelDetails = true;
                        ShowDirectMessageSettings = false;
                        break;
                    }
                    var topic = SelectedTopic is { } selectedTopic &&
                        string.Equals(selectedTopic.CanonicalKey, channelTopic.CanonicalKey, StringComparison.Ordinal)
                            ? selectedTopic
                            : null;
                    var topicState = topic is null
                        ? null
                        : $"{topic.VisibilityLabel}{(topic.IsResolved ? " · 已解决" : string.Empty)}";
                    ConversationTitle = string.IsNullOrEmpty(channelTopic.Topic) ? "（无主题）" : channelTopic.Topic;
                    ConversationSubtitle = $"# {channelName}";
                    DetailsTitle = $"# {channelName}";
                    DetailsBody = $"话题：{ConversationTitle}";
                    DetailsKindLabel = "频道话题";
                    DetailsGlyph = "#";
                    DetailsIdentifierLabel = $"频道 ID：{channelTopic.ChannelId} · {(subscription?.IsActive == true ? "已订阅" : "订阅状态不可用")}";
                    DetailsStateLabel = string.Join(" · ", new[]
                    {
                        DescribeConnection(state.Connection),
                        unreadCount > 0 ? $"未读 {unreadCount} 条" : "无未读",
                        subscription?.IsMuted == true ? "频道已静音" : "频道未静音",
                        subscription?.IsPinned == true ? "已置顶" : "未置顶",
                        topicState
                    }.Where(static value => !string.IsNullOrWhiteSpace(value)));
                    DetailsAvailableMessage = "当前频道与话题身份、未读状态、频道静音、置顶和退出已接通；话题级操作可从右上角菜单进入。";
                    DetailsUnavailableMessage = "此详情面板不加载频道描述、隐私、创建者、订阅者列表、共同频道、文件夹、邮箱地址或权限组。频道成员和管理信息请进入“频道设置”；所有写操作仍由 Zulip 服务端最终裁决。";
                    ShowChannelDetails = true;
                    ShowDirectMessageSettings = false;
                    break;
                }
            case DirectMessage directMessage:
                {
                    ConversationTitle = DescribeDirectMessage(directMessage, state.Users, _session.CurrentUserId);
                    ConversationSubtitle = DescribeDirectMessagePresence(directMessage, state.Presence)
                        ?? DescribeDirectMessageKind(directMessage);
                    DetailsTitle = ConversationTitle;
                    DetailsBody = directMessage.OtherUserIds.Count switch
                    {
                        0 => "仅你自己可见",
                        1 => $"与 {ConversationTitle} 的私信",
                        _ => $"{directMessage.OtherUserIds.Count + 1} 位参与者：你、{DescribeDirectMessageParticipants(directMessage, state.Users)}"
                    };
                    DetailsKindLabel = directMessage.OtherUserIds.Count switch
                    {
                        0 => "给自己",
                        1 => "私信",
                        _ => "群组私信"
                    };
                    DetailsGlyph = "@";
                    DetailsIdentifierLabel = directMessage.OtherUserIds.Count switch
                    {
                        0 => "给自己的私信",
                        1 => "一对一私信 · 2 位参与者",
                        _ => $"群组私信 · {directMessage.OtherUserIds.Count + 1} 位参与者"
                    };
                    DetailsStateLabel = string.Empty;
                    DetailsAvailableMessage = string.Empty;
                    DetailsUnavailableMessage = string.Empty;
                    ShowChannelDetails = false;
                    ShowDirectMessageSettings = directMessage.OtherUserIds.Count <= 1;
                    break;
                }
            default:
                ConversationTitle = "选择会话";
                ConversationSubtitle = "从左侧选择会话开始";
                DetailsTitle = "会话详情";
                DetailsBody = "选择会话后显示可靠的会话信息。";
                DetailsKindLabel = "会话";
                DetailsGlyph = "•";
                DetailsIdentifierLabel = "尚未选择会话";
                DetailsStateLabel = DescribeConnection(state.Connection);
                DetailsAvailableMessage = "选择会话后显示已经接通的能力。";
                DetailsUnavailableMessage = "未选择会话时不会推断成员关系、共同频道或管理权限。";
                ShowChannelDetails = false;
                ShowDirectMessageSettings = false;
                CloseDetailsCore();
                break;
        }
    }

    private void ProjectDraft(ConversationKey? selected)
    {
        var key = selected?.CanonicalKey;
        if (string.Equals(_activeDraftKey, key, StringComparison.Ordinal)) return;
        PasteAttachmentsCommand.Cancel();
        DropAttachmentsCommand.Cancel();
        _activeDraftKey = key;
        SetComposerTextWithoutTracking(key is not null && _drafts.TryGetValue(key, out var draft)
            ? draft
            : string.Empty);
        Reconcile(
            Attachments,
            key is not null && _attachmentDrafts.TryGetValue(key, out var attachments)
                ? attachments
                : [],
            item => item.Id);
        AttachmentError = null;
        NotifyAttachmentProperties();
    }

    private void ProjectUnread(UnreadState unread)
    {
        HasNavigationUnread = unread.IsTruncated || unread.Total > 0;
        NavigationUnreadLabel = unread.IsTruncated
            ? "有未读"
            : unread.Total > 99
                ? "99+"
                : unread.Total > 0 ? unread.Total.ToString() : string.Empty;
        _appNotificationService.UpdateTrayUnread(unread.Total, unread.IsTruncated);
        SynchronizeTaskbarBadge(unread);
        if (!HasNavigationUnread) _appNotificationService.StopTaskbarFlash();
    }

    private void SynchronizeTaskbarBadge(UnreadState unread) =>
        _appNotificationService.UpdateUnreadBadge(
            TaskbarBadgeEnabled ? unread.Total : 0,
            TaskbarBadgeEnabled && unread.IsTruncated);

    private void RequestAutoMarkDisplayedRead(ClientState state)
    {
        var selected = _session.SelectedConversation;
        var history = _session.HistoryState;
        if (_disposed || !_isWindowActive || !IsApplicationWindowForeground || IsNativePreview || selected is null ||
            !IsMessagesSection || !IsChatPaneVisible || !IsConversationContentVisible ||
            !IsMessageCollectionVisible || IsModalOverlayVisible || IsNavigationPending ||
            !_isMessageViewportNearBottom ||
            (PendingMessageScrollRequest is { Reason: not MessageScrollReason.RealtimeFollow }) ||
            state.Connection.Status != RelayCove.Core.ConnectionStatus.Connected ||
            history.IsLoading || history.Error is not null ||
            !string.Equals(history.Conversation?.CanonicalKey, selected.CanonicalKey, StringComparison.Ordinal))
        {
            CancelAutoMarkReadOperation(allowRetry: true);
            return;
        }

        if (!TryGetDisplayedUnreadMessage(state, selected, out var maxUnreadMessageId))
        {
            lock (_autoMarkReadSync)
            {
                _autoMarkReadPending = false;
            }
            return;
        }

        var accountKey = _session.AccountId?.Value ?? "anonymous";
        var attemptKey = $"{accountKey}|{selected.CanonicalKey}";
        CancellationTokenSource operation;
        lock (_autoMarkReadSync)
        {
            if (_autoMarkReadInFlight)
            {
                _autoMarkReadPending = true;
                if (!string.Equals(_autoMarkReadAttemptKey, attemptKey, StringComparison.Ordinal))
                {
                    RemoveCurrentAutoMarkAttemptForRetry();
                    _autoMarkReadCancellation?.Cancel();
                }
                return;
            }

            if (_autoMarkReadAttemptedThrough.GetValueOrDefault(attemptKey) >= maxUnreadMessageId)
            {
                _autoMarkReadPending = false;
                return;
            }

            operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
            _autoMarkReadAttemptedThrough[attemptKey] = maxUnreadMessageId;
            _autoMarkReadCancellation = operation;
            _autoMarkReadAttemptKey = attemptKey;
            _autoMarkReadMessageId = maxUnreadMessageId;
            _autoMarkReadInFlight = true;
            _autoMarkReadPending = false;
        }

        _ = AutoMarkDisplayedReadAsync(selected, attemptKey, maxUnreadMessageId, operation);
    }

    private bool TryGetDisplayedUnreadMessage(
        ClientState state,
        ConversationKey selected,
        out long maxUnreadMessageId)
    {
        maxUnreadMessageId = state.Messages.Values
            .Where(message => message.Conversation == selected)
            .OrderByDescending(message => message.Id)
            .Take(50)
            .Where(message => !message.IsRead)
            .Select(message => message.Id)
            .DefaultIfEmpty()
            .Max();
        return maxUnreadMessageId > 0;
    }

    private async Task AutoMarkDisplayedReadAsync(
        ConversationKey expectedConversation,
        string attemptKey,
        long maxUnreadMessageId,
        CancellationTokenSource operation)
    {
        var allowRetry = false;
        try
        {
            await _session.MarkDisplayedReadAsync(expectedConversation, operation.Token);
            allowRetry = _session.SelectedConversation != expectedConversation;
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            allowRetry = true;
        }
        catch (Exception exception) when (exception is CredentialVaultException or GatewayException or InvalidOperationException)
        {
            // The authoritative unread state remains unchanged. Automatic reads
            // are not retried for the same message without a new visibility event.
        }
        catch
        {
            // Keep unread state authoritative and leave explicit "mark read" available.
        }
        finally
        {
            bool shouldRecheck;
            lock (_autoMarkReadSync)
            {
                if (allowRetry &&
                    _autoMarkReadAttemptedThrough.GetValueOrDefault(attemptKey) == maxUnreadMessageId)
                {
                    _autoMarkReadAttemptedThrough.Remove(attemptKey);
                }
                if (ReferenceEquals(_autoMarkReadCancellation, operation))
                {
                    _autoMarkReadCancellation = null;
                    _autoMarkReadAttemptKey = null;
                    _autoMarkReadMessageId = 0;
                }
                _autoMarkReadInFlight = false;
                shouldRecheck = _autoMarkReadPending;
                _autoMarkReadPending = false;
            }
            operation.Dispose();

            if (shouldRecheck && !_disposed)
            {
                _dispatcher.Dispatch(() => RequestAutoMarkDisplayedRead(_projectedState));
            }
        }
    }

    private void CancelAutoMarkReadOperation(bool allowRetry)
    {
        lock (_autoMarkReadSync)
        {
            _autoMarkReadPending = false;
            // A new visibility interval must also release completed failed attempts.
            // Their in-flight key has already been cleared by the completion handler.
            if (allowRetry) _autoMarkReadAttemptedThrough.Clear();
            _autoMarkReadCancellation?.Cancel();
        }
    }

    private void RemoveCurrentAutoMarkAttemptForRetry()
    {
        if (_autoMarkReadAttemptKey is not { } attemptKey || _autoMarkReadMessageId <= 0) return;
        if (_autoMarkReadAttemptedThrough.GetValueOrDefault(attemptKey) == _autoMarkReadMessageId)
        {
            _autoMarkReadAttemptedThrough.Remove(attemptKey);
        }
    }

    private void NotifyProjectionProperties()
    {
        OnPropertyChanged(nameof(HasSelectedConversation));
        OnPropertyChanged(nameof(CanOpenConversationSettings));
        OnPropertyChanged(nameof(CanCreatePrivateGroup));
        OnPropertyChanged(nameof(PrivateGroupCreateDisabledReason));
        OnPropertyChanged(nameof(ShowPrivateGroupCreateDisabledReason));
        OnPropertyChanged(nameof(HasSelectedTopic));
        OnPropertyChanged(nameof(HasMessages));
        OnPropertyChanged(nameof(IsMessageListEmpty));
        OnPropertyChanged(nameof(IsConversationContentVisible));
        OnPropertyChanged(nameof(IsMessageCollectionVisible));
        OnPropertyChanged(nameof(ShowMessageEmptyState));
        OnPropertyChanged(nameof(ShowConversationLoadingIndicator));
        OnPropertyChanged(nameof(ShowEmptyChannelTopicState));
        OnPropertyChanged(nameof(HasKnownContacts));
        OnPropertyChanged(nameof(CanCompose));
        NotifyCanSendChanged();
        OnPropertyChanged(nameof(CanMarkRead));
        OnPropertyChanged(nameof(CanUnsubscribeSelectedChannel));
        OnPropertyChanged(nameof(CanManageSelectedChannel));
        OnPropertyChanged(nameof(ShowChannelActionBoundary));
        OnPropertyChanged(nameof(IsSelectedChannelMuted));
        OnPropertyChanged(nameof(IsSelectedChannelPinned));
        OnPropertyChanged(nameof(CanClearConversationCache));
        NotifyPrivateGroupActionProperties();
        OnPropertyChanged(nameof(ClearConversationCacheDescription));
        OnPropertyChanged(nameof(SelectedChannelMuteLabel));
        OnPropertyChanged(nameof(SelectedChannelPinLabel));
        OnPropertyChanged(nameof(MessageEmptyTitle));
        OnPropertyChanged(nameof(WorkspaceDisplayName));
        OnPropertyChanged(nameof(IsNativePreview));
        OnPropertyChanged(nameof(ShowLoadOlderButton));
        OnPropertyChanged(nameof(ShowNewMessagesButton));
        OnPropertyChanged(nameof(NewMessagesButtonText));
        OnPropertyChanged(nameof(ShowConnectionStatus));
        OnPropertyChanged(nameof(ChannelGroupCountLabel));
        OnPropertyChanged(nameof(DirectMessageGroupCountLabel));
        OnPropertyChanged(nameof(CurrentUserDisplayName));
        OnPropertyChanged(nameof(CurrentUserInitial));
        OnPropertyChanged(nameof(CurrentUserAvatarUrl));
        UploadAvatarCommand.NotifyCanExecuteChanged();
        EditOwnNameCommand.NotifyCanExecuteChanged();
        SaveOwnNameCommand.NotifyCanExecuteChanged();
        NotifyOwnPresenceProperties();
        OnPropertyChanged(nameof(ChannelListHeight));
        OnPropertyChanged(nameof(TopicListHeight));
        OnPropertyChanged(nameof(HasCurrentConversationUnread));
        NotifyLayoutProperties();
    }

    private void NotifyOwnPresenceProperties()
    {
        OnPropertyChanged(nameof(OwnPresenceStatus));
        OnPropertyChanged(nameof(HasOwnPresenceStatus));
        OnPropertyChanged(nameof(OwnPresenceLabel));
        OnPropertyChanged(nameof(IsOwnPresenceOnline));
        OnPropertyChanged(nameof(IsOwnPresenceIdle));
        OnPropertyChanged(nameof(IsOwnPresenceOffline));
        OnPropertyChanged(nameof(OwnPresenceBrush));
        OnPropertyChanged(nameof(OwnPresenceStatusText));
        OnPropertyChanged(nameof(OwnStatusSummary));
        OnPropertyChanged(nameof(HasOwnStatusSummary));
    }

    private void ProjectHistoryState(ConversationKey? selected)
    {
        var history = _session.HistoryState;
        var matchesSelected = selected is not null &&
            string.Equals(history.Conversation?.CanonicalKey, selected.CanonicalKey, StringComparison.Ordinal);
        HasReachedOldestMessage = matchesSelected && history.FoundOldest;
        if (!HasConversationActivationError)
        {
            MessageLoadError = matchesSelected ? DescribeHistoryError(history.Error) : null;
        }
        OnPropertyChanged(nameof(ShowLoadOlderButton));
    }

    private void QueueScrollToLatest(MessageScrollReason reason)
    {
        var selected = _session.SelectedConversation;
        var history = _session.HistoryState;
        if (selected is null ||
            (history.IsLoading && reason != MessageScrollReason.RealtimeFollow) ||
            history.Error is not null ||
            !string.Equals(history.Conversation?.CanonicalKey, selected.CanonicalKey, StringComparison.Ordinal))
        {
            return;
        }

        var targetMessageId = Messages
            .Where(message => message.MessageId is not null)
            .Select(message => message.MessageId!.Value)
            .DefaultIfEmpty()
            .Max();
        if (targetMessageId <= 0) return;
        PendingMessageScrollRequest = new MessageScrollRequest(
            ++_messageScrollSequence,
            selected.CanonicalKey,
            history.Generation,
            targetMessageId,
            reason);
    }

    private void QueueScrollToMessage(long messageId)
    {
        var selected = _session.SelectedConversation;
        var history = _session.HistoryState;
        if (selected is null ||
            history.IsLoading ||
            history.Error is not null ||
            !string.Equals(history.Conversation?.CanonicalKey, selected.CanonicalKey, StringComparison.Ordinal) ||
            Messages.All(message => message.MessageId != messageId))
        {
            return;
        }

        _messageAnchorHistory = (_session.AccountId, selected.CanonicalKey, history.Generation);
        _pendingActivationScrollConversationKey = null;
        _pendingActivationScrollReason = null;
        PendingMessageScrollRequest = new MessageScrollRequest(
            ++_messageScrollSequence,
            selected.CanonicalKey,
            history.Generation,
            messageId,
            MessageScrollReason.MessageAnchor);
    }

    private void CancelActivationScrollForUserInteraction(ConversationKey conversation)
    {
        if (string.Equals(_pendingActivationScrollConversationKey, conversation.CanonicalKey, StringComparison.Ordinal))
        {
            _pendingActivationScrollConversationKey = null;
            _pendingActivationScrollReason = null;
        }
        if (string.Equals(_retainedActivationConversationKey, conversation.CanonicalKey, StringComparison.Ordinal))
        {
            _retainedActivationConversationKey = null;
            _retainedActivationLatestMessageId = 0;
        }

        if (PendingMessageScrollRequest is
            {
                ConversationKey: var requestConversationKey,
                Reason: MessageScrollReason.ConversationActivated or MessageScrollReason.ConversationReactivated
            } &&
            string.Equals(requestConversationKey, conversation.CanonicalKey, StringComparison.Ordinal))
        {
            PendingMessageScrollRequest = null;
        }
    }

    private void SetExpandedChannel(long? channelId)
    {
        _expandedChannelId = channelId;
        foreach (var channel in Channels)
        {
            channel.IsExpanded = channel.ChannelId == channelId;
        }
        UpdateExpandedChannelTopicCount();
        OnPropertyChanged(nameof(ShowTopicPicker));
        OnPropertyChanged(nameof(ChannelListHeight));
        OnPropertyChanged(nameof(TopicListHeight));
    }

    private void UpdateExpandedChannelTopicCount()
    {
        foreach (var channel in Channels)
        {
            channel.ExpandedTopicCount = channel.IsExpanded ? Topics.Count : 0;
            channel.TreeTopics = channel.IsExpanded ? Topics : null;
        }
    }

    private void TryPublishPendingActivationScroll()
    {
        if (_pendingActivationScrollConversationKey is not { } conversationKey ||
            _pendingActivationScrollReason is not { } reason)
        {
            return;
        }

        var history = _session.HistoryState;
        if (!string.Equals(history.Conversation?.CanonicalKey, conversationKey, StringComparison.Ordinal))
        {
            return;
        }
        if (_pendingActivationScrollGeneration == 0)
        {
            _pendingActivationScrollGeneration = history.Generation;
        }
        if (history.Generation != _pendingActivationScrollGeneration) return;
        if (history.Error is not null)
        {
            _pendingActivationScrollConversationKey = null;
            _pendingActivationScrollReason = null;
            return;
        }
        var targetMessageId = Messages
            .Where(message => message.MessageId is not null)
            .Select(message => message.MessageId!.Value)
            .DefaultIfEmpty()
            .Max();
        var isAlreadyDisplayedByRetainedPresentation =
            string.Equals(_retainedActivationConversationKey, conversationKey, StringComparison.Ordinal) &&
            targetMessageId > 0 &&
            targetMessageId <= _retainedActivationLatestMessageId;
        if (targetMessageId > 0 &&
            !isAlreadyDisplayedByRetainedPresentation &&
            (!string.Equals(_lastActivationScrollConversationKey, conversationKey, StringComparison.Ordinal) ||
             _lastActivationScrollGeneration != history.Generation ||
             _lastActivationScrollTargetMessageId != targetMessageId))
        {
            _lastActivationScrollConversationKey = conversationKey;
            _lastActivationScrollGeneration = history.Generation;
            _lastActivationScrollTargetMessageId = targetMessageId;
            PendingMessageScrollRequest = new MessageScrollRequest(
                ++_messageScrollSequence,
                conversationKey,
                history.Generation,
                targetMessageId,
                reason);
        }

        // A memory/SQLite hit is already useful UI. Keep the activation intent
        // alive while the authoritative page revalidates, but only publish a
        // replacement request if that merge contributes a genuinely newer ID.
        if (history.IsLoading || targetMessageId <= 0)
        {
            return;
        }
        _pendingActivationScrollConversationKey = null;
        _pendingActivationScrollReason = null;
    }

    private void RetargetPendingScrollIfNeeded()
    {
        var request = PendingMessageScrollRequest;
        if (request is null ||
            Messages.Any(message => message.MessageId == request.TargetMessageId))
        {
            return;
        }

        var targetMessageId = Messages
            .Where(message => message.MessageId is not null)
            .Select(message => message.MessageId!.Value)
            .DefaultIfEmpty()
            .Max();
        PendingMessageScrollRequest = targetMessageId <= 0
            ? null
            : request with { Sequence = ++_messageScrollSequence, TargetMessageId = targetMessageId };
    }

    internal bool IsMessageScrollRequestCurrent(MessageScrollRequest request) =>
        PendingMessageScrollRequest?.Sequence == request.Sequence &&
        _session.HistoryState.Generation == request.Generation &&
        string.Equals(_session.SelectedConversation?.CanonicalKey, request.ConversationKey, StringComparison.Ordinal);

    internal void AcknowledgeMessageScrollRequest(MessageScrollRequest request)
    {
        if (!IsMessageScrollRequestCurrent(request)) return;
        PendingMessageScrollRequest = null;
        _isMessageViewportNearBottom = request.Reason != MessageScrollReason.MessageAnchor;
        if (request.Reason == MessageScrollReason.ManualJumpToLatest)
        {
            NewMessageCount = 0;
            SetMessageViewportBeyondJumpThreshold(false);
        }
        RequestAutoMarkDisplayedRead(_projectedState);
    }

    private void SetMessageViewportBeyondJumpThreshold(bool value)
    {
        if (_isMessageViewportBeyondJumpThreshold == value) return;
        _isMessageViewportBeyondJumpThreshold = value;
        OnPropertyChanged(nameof(ShowNewMessagesButton));
    }

    internal string? CurrentConversationKey => _displayedConversationKey ?? _session.SelectedConversation?.CanonicalKey;

    internal long CurrentHistoryGeneration => _session.HistoryState.Generation;

    private void NotifyConversationAvailability()
    {
        OnPropertyChanged(nameof(IsConversationContentVisible));
        OnPropertyChanged(nameof(IsMessageCollectionVisible));
        OnPropertyChanged(nameof(ShowMessageEmptyState));
        OnPropertyChanged(nameof(ShowConversationLoadingIndicator));
        OnPropertyChanged(nameof(ShowEmptyChannelTopicState));
        OnPropertyChanged(nameof(CanCompose));
        NotifyCanSendChanged();
        OnPropertyChanged(nameof(CanMarkRead));
        OnPropertyChanged(nameof(HasMessageLoadError));
        OnPropertyChanged(nameof(ShowHistoryRetry));
    }

    private static string? DescribeHistoryError(string? error) => error switch
    {
        null or "" => null,
        "offline" => "当前离线，无法加载更早消息。",
        _ => "无法加载更早消息，请稍后重试。"
    };

    private void NotifyLayoutProperties()
    {
        OnPropertyChanged(nameof(IsWideLayout));
        OnPropertyChanged(nameof(IsCompactLayout));
        OnPropertyChanged(nameof(IsIntermediateLayout));
        OnPropertyChanged(nameof(IsNarrowLayout));
        OnPropertyChanged(nameof(IsNotNarrowLayout));
        OnPropertyChanged(nameof(IsConversationPaneVisible));
        OnPropertyChanged(nameof(IsWorkspaceContentPaneVisible));
        OnPropertyChanged(nameof(IsChatPaneVisible));
        OnPropertyChanged(nameof(IsInlineDetailsVisible));
        OnPropertyChanged(nameof(IsOverlayDetailsVisible));
        OnPropertyChanged(nameof(IsPrimaryShellEnabled));
        OnPropertyChanged(nameof(ConversationPaneWidth));
        OnPropertyChanged(nameof(ChatPaneWidth));
        OnPropertyChanged(nameof(InlineDetailsWidth));
        OnPropertyChanged(nameof(MessageRowMaximumWidth));
    }

    private void NotifyOverlayProperties()
    {
        OnPropertyChanged(nameof(IsModalOverlayVisible));
        OnPropertyChanged(nameof(IsPrimaryShellEnabled));
        RequestAutoMarkDisplayedRead(_projectedState);
    }

    private void ApplyUiPreferences(UiPreferences preferences)
    {
        var previous = _suppressUiPreferenceSave;
        _suppressUiPreferenceSave = true;
        try
        {
            DensityMode = preferences.Density;
            FontScaleMode = preferences.FontScale;
            ConversationWidthMode = preferences.ConversationWidth;
            _fontSize = preferences.FontSize ?? FontScaleSliderValue;
            _conversationPaneWidth = preferences.ConversationPaneWidth ?? ConversationWidthSliderValue;
            _persistedComposerHeight = Math.Clamp(
                preferences.ComposerHeight ?? DefaultComposerHeight,
                MinimumComposerHeight,
                MaximumComposerHeight);
            ComposerHeight = _persistedComposerHeight;
            AreChannelsExpanded = preferences.ChannelsExpanded;
            AreDirectMessagesExpanded = preferences.DirectMessagesExpanded;
        }
        finally
        {
            _suppressUiPreferenceSave = previous;
        }
        NotifyLayoutProperties();
    }

    private void SaveUiPreferences()
    {
        if (_suppressUiPreferenceSave) return;
        _uiPreferencesService.Save(new UiPreferences(
            DensityMode,
            FontScaleMode,
            ConversationWidthMode,
            AreChannelsExpanded,
            AreDirectMessagesExpanded,
            FontScaleSliderValue,
            ConversationWidthSliderValue,
            _persistedComposerHeight));
    }

    private void SetComposerHeightWithoutPersistence(double height)
    {
        var previous = _suppressUiPreferenceSave;
        _suppressUiPreferenceSave = true;
        try
        {
            ComposerHeight = height;
        }
        finally
        {
            _suppressUiPreferenceSave = previous;
        }
    }

    private void ApplyNotificationPreferences(NotificationPreferences preferences)
    {
        var previous = _suppressNotificationPreferenceSave;
        _suppressNotificationPreferenceSave = true;
        try
        {
            SystemNotificationsEnabled = preferences.SystemNotificationsEnabled;
            TaskbarFlashEnabled = preferences.TaskbarFlashEnabled;
            TaskbarBadgeEnabled = preferences.TaskbarBadgeEnabled;
            ShowMessagePreview = preferences.ShowMessagePreview;
            DoNotDisturb = preferences.DoNotDisturb;
        }
        finally
        {
            _suppressNotificationPreferenceSave = previous;
        }
    }

    private void SaveNotificationPreferences()
    {
        if (_suppressNotificationPreferenceSave) return;
        _notificationPreferencesService.Save(new NotificationPreferences(
            SystemNotificationsEnabled,
            TaskbarFlashEnabled,
            TaskbarBadgeEnabled,
            ShowMessagePreview,
            DoNotDisturb));
    }

    private void SetComposerTextWithoutTracking(string value)
    {
        _suppressDraftTracking = true;
        try
        {
            ComposerText = value;
        }
        finally
        {
            _suppressDraftTracking = false;
        }
    }

    private void ClearSubmittedComposerDraft(string key)
    {
        _drafts.Remove(key);
        _attachmentDrafts.Remove(key);
        if (string.Equals(_activeDraftKey, key, StringComparison.Ordinal))
        {
            PasteAttachmentsCommand.Cancel();
            DropAttachmentsCommand.Cancel();
            SetComposerTextWithoutTracking(string.Empty);
            Reconcile(Attachments, [], item => item.Id);
            AttachmentError = null;
            NotifyAttachmentProperties();
        }
    }

    private void ResetDrafts()
    {
        CancelAttachmentUploads(createReplacement: true);
        _drafts.Clear();
        _attachmentDrafts.Clear();
        _unsubmittedMessages.Clear();
        _activeDraftKey = null;
        SetComposerTextWithoutTracking(string.Empty);
        Reconcile(Attachments, [], item => item.Id);
        AttachmentError = null;
        NotifyAttachmentProperties();
    }

    private void SaveCurrentAttachmentDrafts()
    {
        if (_activeDraftKey is null) return;
        if (Attachments.Count == 0) _attachmentDrafts.Remove(_activeDraftKey);
        else _attachmentDrafts[_activeDraftKey] = Attachments.ToList();
    }

    private void NotifyAttachmentProperties()
    {
        OnPropertyChanged(nameof(HasAttachments));
        OnPropertyChanged(nameof(ComposerDisplayHeight));
        NotifyCanSendChanged();
    }

    private void NotifyCanSendChanged()
    {
        OnPropertyChanged(nameof(CanSend));
        SendCommand.NotifyCanExecuteChanged();
    }

    private void CancelAttachmentUploads(bool createReplacement)
    {
        var cancellation = _attachmentUploadsCancellation;
        cancellation.Cancel();
        cancellation.Dispose();
        if (createReplacement) _attachmentUploadsCancellation = new CancellationTokenSource();
    }

    private void UpdateMediaDownloadProgress(RealmMediaTransferProgress progress)
    {
        var transferred = Math.Max(0, progress.BytesTransferred);
        var total = progress.TotalBytes is > 0 ? progress.TotalBytes : null;
        HasKnownMediaDownloadLength = total is not null;
        MediaDownloadProgress = total is { } length
            ? Math.Clamp(transferred / (double)length, 0d, 1d)
            : 0d;
        MediaDownloadProgressText = total is { } knownLength
            ? $"{MediaDownloadProgress:P0} · {AttachmentDraftItem.FormatBytes(transferred)} / {AttachmentDraftItem.FormatBytes(knownLength)}"
            : $"已下载 {AttachmentDraftItem.FormatBytes(transferred)}";
    }

    private void RecordCompletedDownload(AccountId accountId, DownloadHistoryEntry entry)
    {
        try
        {
            var persisted = _downloadHistoryStore.Load(accountId)
                .Where(existing => existing.Id != entry.Id &&
                    !string.Equals(existing.FilePath, entry.FilePath, StringComparison.OrdinalIgnoreCase))
                .Prepend(entry)
                .Take(RecentDownloadLimit)
                .ToArray();
            _downloadHistoryStore.Save(accountId, persisted);
        }
        catch
        {
        }

        if (_session.AccountId != accountId || _downloadHistoryAccountId != accountId) return;
        foreach (var existingItem in RecentDownloads.Where(item => item.Id == entry.Id ||
                     string.Equals(item.FilePath, entry.FilePath, StringComparison.OrdinalIgnoreCase)).ToArray())
            RecentDownloads.Remove(existingItem);
        RecentDownloads.Insert(0, new DownloadHistoryItem(entry, !DownloadedFileExists(entry.FilePath)));
        while (RecentDownloads.Count > RecentDownloadLimit) RecentDownloads.RemoveAt(RecentDownloads.Count - 1);
        if (!IsDownloadCenterOpen) HasUnseenCompletedDownloads = true;
        NotifyDownloadHistoryProperties();
    }

    private void LoadDownloadHistory(AccountId? accountId)
    {
        RecentDownloads.Clear();
        HasUnseenCompletedDownloads = false;
        HasUnseenDownloadFailure = false;
        DownloadCenterStatus = null;
        if (accountId is { } current)
        {
            try
            {
                foreach (var entry in _downloadHistoryStore.Load(current).Take(RecentDownloadLimit))
                {
                    RecentDownloads.Add(new DownloadHistoryItem(entry, !DownloadedFileExists(entry.FilePath)));
                }
            }
            catch
            {
                DownloadCenterStatus = "无法读取本机下载记录";
            }
        }
        NotifyDownloadHistoryProperties();
    }

    private void RefreshDownloadAvailability()
    {
        foreach (var item in RecentDownloads)
        {
            item.IsMissing = !DownloadedFileExists(item.FilePath);
        }
        RefreshAttachmentDownloads();
    }

    private bool DownloadedFileExists(string path)
    {
        try
        {
            return _fileSaveService.DownloadedFileExists(path);
        }
        catch
        {
            return false;
        }
    }

    private void PersistCurrentDownloadHistory()
    {
        if (_downloadHistoryAccountId is not { } accountId || _session.AccountId != accountId) return;
        try
        {
            _downloadHistoryStore.Save(accountId, RecentDownloads.Select(item => item.Entry).ToArray());
        }
        catch
        {
            DownloadCenterStatus = "无法更新本机下载记录";
        }
    }

    private void NotifyDownloadHistoryProperties()
    {
        RefreshAttachmentDownloads();
        OnPropertyChanged(nameof(HasRecentDownloads));
        OnPropertyChanged(nameof(IsDownloadCenterEmpty));
    }

    private void SetFailedMediaDownload(MessageAttachmentItem? attachment)
    {
        if (Equals(_failedMediaDownloadAttachment, attachment)) return;
        _failedMediaDownloadAttachment = attachment;
        HasUnseenDownloadFailure = attachment is not null && !IsDownloadCenterOpen;
        OnPropertyChanged(nameof(CanRetryMediaDownload));
        OnPropertyChanged(nameof(ShowDownloadCenterCurrentTask));
        OnPropertyChanged(nameof(IsDownloadCenterEmpty));
        OnPropertyChanged(nameof(HasDownloadButtonAttention));
        OnPropertyChanged(nameof(HasDownloadFailure));
        OnPropertyChanged(nameof(DownloadButtonDescription));
    }

    private void CancelMediaStatusClear()
    {
        var cancellation = Interlocked.Exchange(ref _mediaStatusClearCancellation, null);
        if (cancellation is null) return;
        cancellation.Cancel();
        cancellation.Dispose();
    }

    private void ScheduleMediaStatusClear()
    {
        CancelMediaStatusClear();
        var cancellation = new CancellationTokenSource();
        _mediaStatusClearCancellation = cancellation;
        _ = ClearMediaStatusAfterDelayAsync(cancellation);
    }

    private async Task ClearMediaStatusAfterDelayAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellation.Token);
            _dispatcher.Dispatch(() =>
            {
                if (!ReferenceEquals(_mediaStatusClearCancellation, cancellation)) return;
                _mediaStatusClearCancellation = null;
                MediaActionStatus = null;
                MediaDownloadFileName = null;
                MediaDownloadProgressText = null;
                cancellation.Dispose();
            });
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
    }

    private static void Reconcile<T, TKey>(
        ObservableCollection<T> target,
        IEnumerable<T> items,
        Func<T, TKey> keySelector)
        where TKey : notnull
    {
        var desired = items.ToArray();
        var comparer = EqualityComparer<TKey>.Default;
        for (var index = 0; index < desired.Length; index++)
        {
            var desiredItem = desired[index];
            var desiredKey = keySelector(desiredItem);
            var existingIndex = -1;
            for (var candidate = index; candidate < target.Count; candidate++)
            {
                if (comparer.Equals(keySelector(target[candidate]), desiredKey))
                {
                    existingIndex = candidate;
                    break;
                }
            }

            if (existingIndex < 0)
            {
                target.Insert(index, desiredItem);
                continue;
            }

            if (existingIndex != index)
            {
                target.Move(existingIndex, index);
            }

            if (!EqualityComparer<T>.Default.Equals(target[index], desiredItem))
            {
                target[index] = desiredItem;
            }
        }

        while (target.Count > desired.Length)
        {
            target.RemoveAt(target.Count - 1);
        }
    }

    private static void ReconcileChannelItems(
        ObservableCollection<ChannelItem> target,
        IEnumerable<ChannelItem> items,
        Func<ChannelItem, long> keySelector)
    {
        var desired = items.ToArray();
        for (var index = 0; index < desired.Length; index++)
        {
            var candidate = desired[index];
            var key = keySelector(candidate);
            var existingIndex = -1;
            for (var searchIndex = index; searchIndex < target.Count; searchIndex++)
            {
                if (keySelector(target[searchIndex]) == key)
                {
                    existingIndex = searchIndex;
                    break;
                }
            }

            if (existingIndex < 0)
            {
                target.Insert(index, candidate);
                continue;
            }

            if (existingIndex != index)
            {
                target.Move(existingIndex, index);
            }

            target[index].ApplyFrom(candidate);
        }

        while (target.Count > desired.Length)
        {
            target.RemoveAt(target.Count - 1);
        }
    }

    private static void ReconcileTopicItems(
        ObservableCollection<TopicItem> target,
        IEnumerable<TopicItem> items,
        Func<TopicItem, string> keySelector)
    {
        var desired = items.ToArray();
        for (var index = 0; index < desired.Length; index++)
        {
            var candidate = desired[index];
            var existingIndex = -1;
            for (var searchIndex = index; searchIndex < target.Count; searchIndex++)
            {
                if (string.Equals(keySelector(target[searchIndex]), keySelector(candidate), StringComparison.Ordinal))
                {
                    existingIndex = searchIndex;
                    break;
                }
            }
            if (existingIndex < 0)
            {
                target.Insert(index, candidate);
                continue;
            }
            if (existingIndex != index) target.Move(existingIndex, index);
            target[index].ApplyFrom(candidate);
        }
        while (target.Count > desired.Length) target.RemoveAt(target.Count - 1);
    }

    private static void ReconcileNavigationItems(
        ObservableCollection<NavigationItem> target,
        IEnumerable<NavigationItem> items,
        Func<NavigationItem, string> keySelector)
    {
        var desired = items.ToArray();
        for (var index = 0; index < desired.Length; index++)
        {
            var candidate = desired[index];
            var key = keySelector(candidate);
            var existingIndex = -1;
            for (var searchIndex = index; searchIndex < target.Count; searchIndex++)
            {
                if (string.Equals(keySelector(target[searchIndex]), key, StringComparison.Ordinal))
                {
                    existingIndex = searchIndex;
                    break;
                }
            }

            if (existingIndex < 0)
            {
                target.Insert(index, candidate);
                continue;
            }

            if (existingIndex != index)
            {
                target.Move(existingIndex, index);
            }

            target[index].ApplyFrom(candidate);
        }

        while (target.Count > desired.Length)
        {
            target.RemoveAt(target.Count - 1);
        }
    }

    private static void ReconcileConversationListItems(
        ObservableCollection<ConversationListItem> target,
        IEnumerable<ConversationListItem> items,
        Func<ConversationListItem, string> keySelector)
    {
        var desired = items.ToArray();
        for (var index = 0; index < desired.Length; index++)
        {
            var candidate = desired[index];
            var key = keySelector(candidate);
            var existingIndex = -1;
            for (var searchIndex = index; searchIndex < target.Count; searchIndex++)
            {
                if (string.Equals(keySelector(target[searchIndex]), key, StringComparison.Ordinal))
                {
                    existingIndex = searchIndex;
                    break;
                }
            }

            if (existingIndex < 0)
            {
                target.Insert(index, candidate);
                continue;
            }
            if (existingIndex != index) target.Move(existingIndex, index);
            target[index].ApplyFrom(candidate);
        }

        while (target.Count > desired.Length) target.RemoveAt(target.Count - 1);
    }

    private static int GetChannelUnread(UnreadState unread, long channelId)
    {
        var prefix = $"channel:{channelId}:";
        return unread.Counts
            .Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal))
            .Sum(pair => pair.Value);
    }

    private static int GetConversationUnread(UnreadState unread, ConversationKey conversation) =>
        unread.Counts.GetValueOrDefault(conversation.CanonicalKey);

    private static string? TryGetRealmHost(string realm) =>
        Uri.TryCreate(realm, UriKind.Absolute, out var uri) ? uri.Host : null;

    private string? CreatePermalink(long messageId)
    {
        var realm = _session.ActiveRealm;
        if (realm is null) return null;
        return new Uri(realm.Uri, $"#narrow/near/{messageId}").AbsoluteUri;
    }

    private static string DescribeDate(DateOnly date, DateTime firstMessageTime)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (date == today) return $"今天 {firstMessageTime:H:mm}";
        if (date == today.AddDays(-1)) return "昨天";
        return date.ToString("yyyy年M月d日");
    }

    private static string? DescribeMutation(MessageMutationState? mutation) => mutation?.Status switch
    {
        MessageMutationStatus.Submitting when mutation.Kind == MessageMutationKind.Star => null,
        MessageMutationStatus.Submitting => mutation.Kind == MessageMutationKind.Delete ? "正在永久删除…" : "正在提交更改…",
        MessageMutationStatus.Uncertain => "结果不确定；请刷新会话确认，系统不会自动重试",
        MessageMutationStatus.Failed => "更改失败；请检查权限或刷新后重试",
        _ => null
    };

    private static bool Contains(string? source, string query) =>
        source?.Contains(query, StringComparison.CurrentCultureIgnoreCase) == true;

    private static string TruncateForSearch(string value)
    {
        var singleLine = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return singleLine.Length <= 96 ? singleLine : string.Concat(singleLine.AsSpan(0, 93), "…");
    }

    private string CreateConversationPreview(string content)
    {
        // Extract the reply before truncating, so the quote permalink cannot
        // consume the whole preview. Quote-only messages show the quoted text.
        var quotes = MessageQuote.ParseLeadingSequence(content, out var body);
        var text = quotes.Count > 0 && string.IsNullOrWhiteSpace(body)
            ? string.Join(" ", quotes.Select(quote => MessageContentPresentation.ToPlainText(quote.Body, _session.ActiveRealm, RealmEmojis)))
            : MessageContentPresentation.ToPlainText(body, _session.ActiveRealm, RealmEmojis);
        return TruncateForSearch(System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " "));
    }

    private static string? ValidateAttachmentSelection(
        IReadOnlyCollection<AttachmentDraftItem> existing,
        IReadOnlyCollection<SelectedAttachmentFile> selected,
        long maxFileBytes)
    {
        const int maximumCount = 10;
        const long maximumTotalBytes = 100L * 1024 * 1024;
        if (existing.Count + selected.Count > maximumCount)
        {
            return $"每条消息最多添加 {maximumCount} 个附件。";
        }
        foreach (var file in selected)
        {
            var name = file.FileName;
            if (string.IsNullOrWhiteSpace(name) || name.Length > 256 ||
                name.Any(character => character < 0x20 || character == 0x7f))
            {
                return "附件文件名为空、过长或包含不可用字符。";
            }
            if (file.Length <= 0) return $"附件“{name}”为空文件，不能上传。";
            if (file.Length > maxFileBytes)
            {
                return $"附件“{name}”不能超过 {AttachmentDraftItem.FormatBytes(maxFileBytes)}。";
            }
        }
        var totalLimit = Math.Min(maximumTotalBytes, Math.Max(maxFileBytes, maxFileBytes * 4));
        var total = existing.Sum(item => item.Length) + selected.Sum(file => file.Length);
        return total > totalLimit
            ? $"本条消息的附件总大小不能超过 {AttachmentDraftItem.FormatBytes(totalLimit)}。"
            : null;
    }

    internal static string BuildUploadedAttachmentMarkdown(UploadedAttachment uploaded, bool isImage)
    {
        var normalized = new string(uploaded.FileName
                .Select(character => character < 0x20 || character == 0x7f ? '_' : character)
                .ToArray())
            .Trim();
        if (normalized.Length > 256) normalized = normalized[..256];
        if (normalized.Length == 0) normalized = "file";
        var label = normalized
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal)
            .Replace("]", "\\]", StringComparison.Ordinal)
            .Replace("(", "\\(", StringComparison.Ordinal)
            .Replace(")", "\\)", StringComparison.Ordinal);
        return $"{(isImage ? "!" : string.Empty)}[{label}]({uploaded.Url})";
    }

    private static string DescribeGatewayFailure(GatewayException exception) => exception.Kind switch
    {
        GatewayErrorKind.IncompatibleRealm => "此 Realm 与 RichChat 不兼容。",
        GatewayErrorKind.AuthenticationFailed or GatewayErrorKind.ReauthRequired => "邮箱、密码或 API 凭据无效。",
        GatewayErrorKind.RateLimited => "服务器正在限流，请稍后再试。",
        GatewayErrorKind.Offline => "无法连接到服务器，请检查网络和 Realm 地址。",
        _ => "服务器请求失败，请稍后再试。"
    };

    private static string DescribePrivateGroupCreationFailure(GatewayException exception)
    {
        var reason = exception.Code switch
        {
            GatewayErrorCode.ChannelAlreadyExists => "群名已存在，请换一个名称。",
            GatewayErrorCode.PermissionDenied => "服务器拒绝了当前账号的创建或成员访问权限，请联系管理员检查权限。",
            GatewayErrorCode.InvalidChannelName => "群名为空、过长或含有不支持的字符，请修改群名。",
            GatewayErrorCode.InvalidChannelMembers => "所选成员已停用或不存在，请刷新联系人后重新选择。",
            GatewayErrorCode.RequestTimedOut => "请求超时。",
            GatewayErrorCode.NetworkError => "网络连接中断，请检查网络。",
            GatewayErrorCode.InvalidResponse => "服务器返回的数据异常，或不支持所需的群聊设置。",
            GatewayErrorCode.RedirectNotAllowed => "服务器要求跳转，请检查登录时填写的服务器地址。",
            _ => exception.Kind switch
            {
                GatewayErrorKind.AuthenticationFailed or GatewayErrorKind.ReauthRequired => "登录已失效，请重新登录。",
                GatewayErrorKind.RateLimited => "操作过于频繁，服务器正在限流，请稍后再试。",
                GatewayErrorKind.Offline => "无法连接服务器，请检查网络。",
                GatewayErrorKind.Server => "服务器内部错误，请联系管理员或稍后再试。",
                GatewayErrorKind.IncompatibleRealm => "服务器版本或配置不支持当前群聊功能。",
                _ => exception.StatusCode switch
                {
                    403 => "服务器拒绝访问，请联系管理员检查群聊权限。",
                    404 or 405 => "服务器未提供群聊创建接口，请检查服务器版本或配置。",
                    400 => "服务器拒绝了群聊名称、成员或设置，请检查后再试。",
                    _ => "服务器未提供可识别的失败原因。"
                }
            }
        };
        var status = exception.StatusCode is { } statusCode ? $"（HTTP {statusCode}）" : string.Empty;
        var resultUncertain = exception.Kind is GatewayErrorKind.Offline or GatewayErrorKind.Server or GatewayErrorKind.Protocol;
        return resultUncertain
            ? $"群聊创建结果无法确认：{reason}{status}请先检查会话列表中是否已创建，勿直接重试。"
            : $"群聊创建失败：{reason}{status}";
    }

    private static string DescribeInvalidOperation(InvalidOperationException exception)
    {
        var message = exception.Message;
        if (message.Contains("selected", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("available", StringComparison.OrdinalIgnoreCase))
        {
            return "当前会话或消息已不可用，请刷新后重试。";
        }
        if (message.Contains("own", StringComparison.OrdinalIgnoreCase))
        {
            return "只能修改或删除自己发送的消息。";
        }
        if (message.Contains("reconciled", StringComparison.OrdinalIgnoreCase))
        {
            return "上一项消息更改结果仍未知，请刷新确认后再操作。";
        }
        if (message.Contains("connected", StringComparison.OrdinalIgnoreCase))
        {
            return "当前离线，连接恢复后再操作。";
        }
        return "当前操作无法完成，请刷新后重试。";
    }

    private static string DescribeConnection(ConnectionState state) => state.Status switch
    {
        RelayCove.Core.ConnectionStatus.SignedOut => "已注销",
        RelayCove.Core.ConnectionStatus.Locked => "本地缓存已锁定",
        RelayCove.Core.ConnectionStatus.Offline when state.Detail == "cache_first" => "正在连接",
        RelayCove.Core.ConnectionStatus.Offline => "连接已中断",
        RelayCove.Core.ConnectionStatus.Connecting => "正在连接",
        RelayCove.Core.ConnectionStatus.Connected => "已连接",
        RelayCove.Core.ConnectionStatus.Reconnecting or RelayCove.Core.ConnectionStatus.RateLimited
            when state.Detail == "retry_wait" && state.RetryAttempt > 0 => DescribeConnectionRetry(state),
        RelayCove.Core.ConnectionStatus.Reconnecting when state.Detail == "retrying" && state.RetryAttempt > 0 =>
            $"正在重新连接（第 {state.RetryAttempt} 次）",
        RelayCove.Core.ConnectionStatus.Reconnecting when state.Detail == "retry_wait" => "等待重新连接",
        RelayCove.Core.ConnectionStatus.Reconnecting => "正在重新连接",
        RelayCove.Core.ConnectionStatus.RateLimited => "服务器繁忙，等待重新连接",
        RelayCove.Core.ConnectionStatus.ReauthRequired => "需要重新认证",
        _ => "连接故障"
    };

    private static string DescribeConnectionRetry(ConnectionState state)
    {
        var reason = state.FailureCode switch
        {
            GatewayErrorCode.NetworkError => "无法连接服务器",
            GatewayErrorCode.RequestTimedOut => "连接超时",
            GatewayErrorCode.RateLimited => "服务器繁忙",
            GatewayErrorCode.InvalidResponse => "服务器响应异常",
            GatewayErrorCode.BadEventQueueId => "连接已失效",
            _ when state.FailureStatusCode == 408 => "连接超时",
            _ when state.FailureStatusCode is >= 500 and <= 599 => $"服务器错误（{state.FailureStatusCode}）",
            GatewayErrorCode.ServerError => "服务器错误",
            _ => "连接已中断"
        };
        var interval = state.RetryDelay is { } delay ? $"，间隔 {Math.Ceiling(delay.TotalSeconds)} 秒" : string.Empty;
        return $"{reason}，等待第 {state.RetryAttempt} 次重连{interval}";
    }

    private static string DescribeDirectMessage(
        DirectMessage message,
        IReadOnlyDictionary<long, UserProfile> users,
        long? currentUserId) =>
        message.OtherUserIds.Count == 0
            ? currentUserId is { } current && users.GetValueOrDefault(current) is { } currentUser
                ? $"{currentUser.FullName}（自己）"
                : "给自己"
            : string.Join(", ", message.OtherUserIds.Select(id => users.GetValueOrDefault(id)?.FullName ?? $"用户 {id}"));

    private static string DescribeDirectMessageParticipants(
        DirectMessage message,
        IReadOnlyDictionary<long, UserProfile> users) =>
        string.Join("、", message.OtherUserIds.Select(id => users.GetValueOrDefault(id)?.FullName ?? $"用户 {id}"));

    private static string DescribeDirectMessageKind(DirectMessage message) => message.OtherUserIds.Count switch
    {
        0 => "self-DM",
        1 => "私信",
        _ => "群组私信"
    };

    private static string? GetMessageAvatarUrl(ChatMessage message, UserProfile? user) =>
        user is { AvatarSource: not UserAvatarSource.Unknown } ? user.DisplayAvatarUrl : user?.DisplayAvatarUrl ?? message.SenderAvatarUrl;

    private static UserProfile? GetDirectMessageAvatar(
        DirectMessage message,
        IReadOnlyDictionary<long, UserProfile> users,
        long? currentUserId) => message.OtherUserIds.Count switch
        {
            0 when currentUserId is { } current => users.GetValueOrDefault(current),
            1 => users.GetValueOrDefault(message.OtherUserIds[0]),
            _ => null
        };

    private static UserPresenceStatus? GetDirectMessagePresence(
        DirectMessage message,
        PresenceState presence) => message.OtherUserIds.Count == 1
        ? presence.ResolveStatus(message.OtherUserIds[0], DateTimeOffset.UtcNow)
        : null;

    private static string? DescribeDirectMessagePresence(
        DirectMessage message,
        PresenceState presence) => GetDirectMessagePresence(message, presence) switch
        {
            UserPresenceStatus.Active => "在线",
            UserPresenceStatus.Idle => "忙碌",
            UserPresenceStatus.Offline => "离线",
            _ => null
        };

    private ConversationListItem CreateDirectConversationListItem(ClientState state, DirectMessage conversation)
    {
        var avatar = GetDirectMessageAvatar(conversation, state.Users, _session.CurrentUserId);
        var latestMessage = state.ConversationSummaries.TryGetValue(conversation.CanonicalKey, out var summary)
            ? summary.LatestMessage
            : state.Messages.Values
                .Where(message => message.Conversation == conversation)
                .OrderByDescending(message => message.Id)
                .FirstOrDefault();
        var existing = Conversations.FirstOrDefault(item => string.Equals(
            item.Conversation.CanonicalKey,
            conversation.CanonicalKey,
            StringComparison.Ordinal));
        var preference = _session.AccountId is { } accountId
            ? _conversationPreferencesStore.Get(accountId, conversation.CanonicalKey)
            : new ConversationPreference();
        return new ConversationListItem(
            conversation,
            DescribeDirectMessage(conversation, state.Users, _session.CurrentUserId),
            latestMessage is null ? existing?.Detail ?? DescribeDirectMessageKind(conversation) : CreateConversationPreview(latestMessage.Content),
            GetConversationUnread(state.Unread, conversation),
            avatar?.DisplayAvatarUrl,
            avatar?.IsBot ?? false,
            latestMessage is null ? existing?.Timestamp : FormatConversationTimestamp(latestMessage.Timestamp.LocalDateTime),
            latestMessage?.Timestamp,
            string.Equals(_session.SelectedConversation?.CanonicalKey, conversation.CanonicalKey, StringComparison.Ordinal),
            preference.IsMuted,
            preference.IsPinned,
            presenceStatus: GetDirectMessagePresence(conversation, state.Presence));
    }

    private ConversationListItem CreatePrivateGroupConversationListItem(ClientState state, Subscription subscription)
    {
        var conversation = new ChannelTopic(subscription.ChannelId, string.Empty);
        var latestMessage = state.ConversationSummaries.TryGetValue(conversation.CanonicalKey, out var summary)
            ? summary.LatestMessage
            : state.Messages.Values
                .Where(message => message.Conversation == conversation)
                .OrderByDescending(message => message.Id)
                .FirstOrDefault();
        var existing = Conversations.FirstOrDefault(item => string.Equals(
            item.Conversation.CanonicalKey,
            conversation.CanonicalKey,
            StringComparison.Ordinal));
        var sender = latestMessage is null
            ? null
            : latestMessage.SenderDisplayName ??
              state.Users.GetValueOrDefault(latestMessage.SenderId)?.FullName ??
              $"用户 {latestMessage.SenderId}";
        var detail = latestMessage is null
            ? existing?.Detail ?? "群聊"
            : $"{sender}: {CreateConversationPreview(latestMessage.Content)}";
        var tiles = _privateGroupMembers.GetValueOrDefault(subscription.ChannelId)?
            .Select(user => state.Users.TryGetValue(user.UserId, out var current) &&
                (current.AvatarVersion ?? 0) >= (user.AvatarVersion ?? 0) ? current : user)
            .OrderBy(static user => user.UserId)
            .Take(4)
            .Select((user, index) => new ConversationAvatarTile(
                user.UserId,
                user.FullName,
                user.DisplayAvatarUrl,
                user.IsBot,
                index / 2,
                index % 2))
            .ToArray() ?? [];
        return new ConversationListItem(
            conversation,
            subscription.Name,
            detail,
            GetConversationUnread(state.Unread, conversation),
            timestamp: latestMessage is null ? existing?.Timestamp : FormatConversationTimestamp(latestMessage.Timestamp.LocalDateTime),
            latestMessageTimestamp: latestMessage?.Timestamp,
            isSelected: string.Equals(_session.SelectedConversation?.CanonicalKey, conversation.CanonicalKey, StringComparison.Ordinal),
            isMuted: subscription.IsMuted,
            isPinned: subscription.IsPinned,
            avatarTiles: tiles);
    }

    private NavigationItem CreateDirectNavigationItem(ClientState state, DirectMessage conversation)
    {
        var avatar = GetDirectMessageAvatar(conversation, state.Users, _session.CurrentUserId);
        var latestMessage = state.ConversationSummaries.TryGetValue(conversation.CanonicalKey, out var summary)
            ? summary.LatestMessage
            : state.Messages.Values
                .Where(message => message.Conversation == conversation)
                .OrderByDescending(message => message.Id)
                .FirstOrDefault();
        var existing = DirectMessages.FirstOrDefault(item =>
            string.Equals(item.Conversation.CanonicalKey, conversation.CanonicalKey, StringComparison.Ordinal));

        // The bounded history window is deliberately cleared while a new
        // conversation loads. Keep the existing navigation preview during
        // that transient state so every direct-message row is not replaced
        // (and its avatar media control needlessly recreated) twice.
        var detail = latestMessage is null
            ? existing?.Detail ?? DescribeDirectMessageKind(conversation)
            : CreateConversationPreview(latestMessage.Content);
        var timestamp = latestMessage is null
            ? existing?.Timestamp
            : FormatConversationTimestamp(latestMessage.Timestamp.LocalDateTime);

        var preference = _session.AccountId is { } accountId
            ? _conversationPreferencesStore.Get(accountId, conversation.CanonicalKey)
            : new ConversationPreference();
        return new NavigationItem(
            conversation,
            DescribeDirectMessage(conversation, state.Users, _session.CurrentUserId),
            detail,
            GetConversationUnread(state.Unread, conversation),
            avatar?.DisplayAvatarUrl,
            avatar?.IsBot ?? false,
            timestamp,
            string.Equals(
                SelectedDirectMessage?.Conversation.CanonicalKey,
                conversation.CanonicalKey,
                StringComparison.Ordinal),
            preference.IsMuted,
            preference.IsPinned);
    }

    private void RefreshNavigationSelectionProjection()
    {
        foreach (var item in Channels)
        {
            var isSelected = SelectedChannel?.ChannelId == item.ChannelId;
            item.IsSelected = isSelected;
        }

        foreach (var item in Topics)
        {
            item.IsSelected = string.Equals(SelectedTopic?.CanonicalKey, item.CanonicalKey, StringComparison.Ordinal);
        }

        for (var index = 0; index < DirectMessages.Count; index++)
        {
            var item = DirectMessages[index];
            var isSelected = string.Equals(
                SelectedDirectMessage?.Conversation.CanonicalKey,
                item.Conversation.CanonicalKey,
                StringComparison.Ordinal);
            item.IsSelected = isSelected;
        }

        foreach (var item in Conversations)
        {
            item.IsSelected = string.Equals(
                _session.SelectedConversation?.CanonicalKey,
                item.Conversation.CanonicalKey,
                StringComparison.Ordinal);
        }

        ProjectConversationFilter();
    }

    private void SelectLastOrRecentTopic(long channelId)
    {
        var remembered = _lastSelectedTopicByChannel.GetValueOrDefault(channelId);
        var target = remembered is null
            ? Topics.OrderByDescending(item => item.MaxMessageId).FirstOrDefault()
            : Topics.FirstOrDefault(item => string.Equals(item.Topic, remembered, StringComparison.Ordinal));
        if (target is not null && !string.Equals(SelectedTopic?.CanonicalKey, target.CanonicalKey, StringComparison.Ordinal))
        {
            SelectedTopic = target;
            return;
        }
        SelectOnlyTopicWhenUnambiguous();
    }

    internal static string FormatConversationTimestamp(DateTime timestamp, DateTime? now = null)
    {
        var current = now ?? DateTime.Now;
        if (timestamp.Date == current.Date) return timestamp.ToString("H:mm");
        if (timestamp.Date == current.Date.AddDays(-1)) return "昨天";
        if (timestamp.Date >= current.Date.AddDays(-6))
        {
            return timestamp.DayOfWeek switch
            {
                DayOfWeek.Monday => "周一",
                DayOfWeek.Tuesday => "周二",
                DayOfWeek.Wednesday => "周三",
                DayOfWeek.Thursday => "周四",
                DayOfWeek.Friday => "周五",
                DayOfWeek.Saturday => "周六",
                _ => "周日"
            };
        }

        return timestamp.ToString("M/d");
    }

    private static string DescribeOutbox(OutboxEntry entry) => entry.State switch
    {
        OutboxState.WaitExpired => "发送结果未确认；再次发送可能重复",
        OutboxState.Failed when entry.Failure == OutboxFailureKind.NetworkResultUnknown =>
            "发送结果未确认；再次发送可能重复",
        OutboxState.Failed => "发送失败；恢复内容后可手动重试",
        _ => string.Empty
    };

    private static bool IsOutboxDeliveryFailure(OutboxEntry entry) =>
        entry.State == OutboxState.Failed && entry.Failure != OutboxFailureKind.NetworkResultUnknown;

    private void CancelNavigation()
    {
        var cancellation = Interlocked.Exchange(ref _navigationCancellation, null);
        _navigationConversationKey = null;
        if (cancellation is null) return;
        cancellation.Cancel();
        cancellation.Dispose();
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Updates?.Dispose();
        if (Stickers is not null) { Stickers.Sent -= OnStickerSent; Stickers.Dispose(); }
        _messageActionTimer?.Dispose();
        lock (_projectionGate)
        {
            _pendingProjectionState = null;
            _projectionDispatchScheduled = false;
        }
        CancelAutoMarkReadOperation(allowRetry: false);
        _lifetimeCancellation.Cancel();
        CancelAttachmentUploads(createReplacement: false);
        CancelNavigation();
        CancelDetailsLoad();
        CancelSearchInput();
        CancelConversationFilterSearch();
        CancelMediaStatusClear();
        ClearNewConversationChoices();
        ChannelSettings.PropertyChanged -= OnChannelSettingsPropertyChanged;
        ChannelSettings.Dispose();
        _session.StateChanged -= OnStateChanged;
        if (_session is IMessageMutationObserver observer) observer.MessageMutationObserved -= OnMessageMutationObserved;
        if (_session is IRealtimeMessageObserver realtimeObserver) realtimeObserver.RealtimeMessageReceived -= OnRealtimeMessageReceived;
        _appNotificationService.StateChanged -= OnAppNotificationStateChanged;
        _appNotificationService.NotificationActivated -= OnAppNotificationActivated;
        _lifetimeCancellation.Dispose();
    }
}
