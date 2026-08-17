using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Maui;
using RelayCove.App.Services;
using RelayCove.Core;

namespace RelayCove.App.ViewModels;

public sealed partial class ShellViewModel : ObservableObject, IDisposable
{
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

    private readonly IClientSession _session;
    private readonly ILastRealmStore _lastRealmStore;
    private readonly IUiDispatcher _dispatcher;
    private readonly IAppearanceService _appearanceService;
    private readonly IUiPreferencesService _uiPreferencesService;
    private readonly IPlatformInteractionService _platformInteractions;
    private readonly IFileSelectionService _fileSelectionService;
    private readonly IRealmMediaService _realmMediaService;
    private readonly IFileSaveService _fileSaveService;
    private readonly Dictionary<string, string> _drafts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<AttachmentDraftItem>> _attachmentDrafts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _draftVersions = new(StringComparer.Ordinal);
    private readonly List<ConversationContactChoice> _allNewConversationChoices = [];
    private readonly Dictionary<long, string> _lastSelectedTopicByChannel = [];
    private readonly ResettableObservableCollection<MessageItem> _messages = [];
    private readonly Dictionary<string, Dictionary<string, MessageItem>> _messageItemsByConversation = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _messageItemConversationLru = [];
    private readonly object _projectionGate = new();
    private readonly object _autoMarkReadSync = new();
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly Dictionary<string, long> _autoMarkReadAttemptedThrough = new(StringComparer.Ordinal);
    private ClientState? _pendingProjectionState;
    private bool _projectionDispatchScheduled;
    private IReadOnlyList<SearchResultItem> _serverSearchResults = [];
    private CancellationTokenSource? _navigationCancellation;
    private CancellationTokenSource? _autoMarkReadCancellation;
    private string? _autoMarkReadAttemptKey;
    private long _autoMarkReadMessageId;
    private long _navigationGeneration;
    private string? _navigationConversationKey;
    private bool _hasAuthoritativeTopics;
    private string? _displayedConversationKey;
    private string? _deferredInitialMessageProjectionConversationKey;
    private string? _pendingActivationScrollConversationKey;
    private long _pendingActivationScrollGeneration;
    private MessageScrollReason? _pendingActivationScrollReason;
    private string? _lastActivationScrollConversationKey;
    private long _lastActivationScrollGeneration;
    private long _lastActivationScrollTargetMessageId;
    private long _messageScrollSequence;
    private CancellationTokenSource? _searchInputCancellation;
    private long _searchInputGeneration;
    private long? _searchBeforeMessageId;
    private AccountId? _searchAccountId;
    private long? _savedBeforeMessageId;
    private CancellationTokenSource? _savedLoadCancellation;
    private long _savedLoadGeneration;
    private AccountId? _savedAccountId;
    private AccountId? _messageItemCacheAccountId;
    private ClientState _projectedState = ClientState.Empty;
    private IReadOnlyList<TopicSummary> _loadedTopics = [];
    private long? _loadedTopicsChannelId;
    private string? _activeDraftKey;
    private double _composerHeight = DefaultComposerHeight;
    private double _viewportWidth = 1440d;
    private long? _channelUnsubscribeTargetId;
    private long _channelBrowserGeneration;
    private AccountId? _channelBrowserAccountId;
    private CancellationTokenSource? _channelBrowserCancellation;
    private string? _projectedConversationKey;
    private long? _newestProjectedMessageId;
    private long _lastAutomaticLoadOlderMilliseconds = long.MinValue;
    private int _automaticLoadOlderInFlight;
    private bool _isMessageViewportNearBottom = true;
    private bool _isWindowActive;
    private bool _autoMarkReadInFlight;
    private bool _autoMarkReadPending;
    private int _initialized;
    private int _loginInFlight;
    private bool _suppressDraftTracking;
    private bool _suppressUiPreferenceSave = true;
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
        IFileSaveService fileSaveService)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _lastRealmStore = lastRealmStore ?? throw new ArgumentNullException(nameof(lastRealmStore));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _appearanceService = appearanceService ?? throw new ArgumentNullException(nameof(appearanceService));
        _uiPreferencesService = uiPreferencesService ?? throw new ArgumentNullException(nameof(uiPreferencesService));
        _platformInteractions = platformInteractions ?? throw new ArgumentNullException(nameof(platformInteractions));
        _fileSelectionService = fileSelectionService ?? throw new ArgumentNullException(nameof(fileSelectionService));
        _realmMediaService = realmMediaService ?? throw new ArgumentNullException(nameof(realmMediaService));
        _fileSaveService = fileSaveService ?? throw new ArgumentNullException(nameof(fileSaveService));
        Realm = _lastRealmStore.Get();
        AppearanceMode = _appearanceService.Current;
        ApplyUiPreferences(_uiPreferencesService.Current);
        _suppressUiPreferenceSave = false;
        _session.StateChanged += OnStateChanged;
        if (_session is IMessageMutationObserver observer) observer.MessageMutationObserved += OnMessageMutationObserved;
        Project(_session.State);
    }

    public ObservableCollection<ChannelItem> Channels { get; } = [];
    public ObservableCollection<ChannelItem> FilteredChannels { get; } = [];
    public ObservableCollection<TopicItem> Topics { get; } = [];
    public ObservableCollection<NavigationItem> DirectMessages { get; } = [];
    public ObservableCollection<NavigationItem> FilteredDirectMessages { get; } = [];
    public ObservableCollection<ContactItem> KnownContacts { get; } = [];
    public ObservableCollection<MessageItem> Messages => _messages;
    public ObservableCollection<SearchResultItem> SearchResults { get; } = [];
    public ObservableCollection<SavedMessageItem> SavedMessages { get; } = [];
    public ObservableCollection<AvailableChannelItem> AvailableChannels { get; } = [];
    public ObservableCollection<AttachmentDraftItem> Attachments { get; } = [];
    public ObservableCollection<ConversationContactChoice> NewConversationChoices { get; } = [];
    public IReadOnlyList<EmojiChoice> EmojiChoices { get; } =
    [
        new("😀", "开心", "grinning", "1f600"), new("😄", "大笑", "smile", "1f604"),
        new("😂", "笑哭", "joy", "1f602"), new("🥰", "喜爱", "smiling_face_with_3_hearts", "1f970"),
        new("😍", "喜欢", "heart_eyes", "1f60d"), new("🤔", "思考", "thinking", "1f914"),
        new("👍", "赞", "+1", "1f44d"), new("👎", "不赞同", "-1", "1f44e"),
        new("👏", "鼓掌", "clap", "1f44f"), new("🙌", "庆祝", "raised_hands", "1f64c"),
        new("🎉", "派对", "tada", "1f389"), new("❤️", "爱心", "heart", "2764"),
        new("🔥", "火热", "fire", "1f525"), new("✅", "完成", "check", "2705"),
        new("👀", "关注", "eyes", "1f440"), new("😭", "大哭", "sob", "1f62d"),
        new("😅", "汗颜", "sweat_smile", "1f605"), new("😮", "惊讶", "open_mouth", "1f62e"),
        new("🙏", "感谢", "pray", "1f64f"), new("💪", "加油", "muscle", "1f4aa"),
        new("🚀", "起飞", "rocket", "1f680"), new("💡", "想法", "bulb", "1f4a1"),
        new("🎯", "目标", "dart", "1f3af"), new("✨", "闪亮", "sparkles", "2728")
    ];

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
    public partial bool IsNewConversationOpen { get; set; }

    [ObservableProperty]
    public partial bool IsNewChannelConversationMode { get; set; }

    [ObservableProperty]
    public partial ChannelItem? NewConversationChannel { get; set; }

    [ObservableProperty]
    public partial string NewConversationTopic { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsChannelBrowserOpen { get; set; }

    [ObservableProperty]
    public partial bool IsChannelBrowserLoading { get; set; }

    [ObservableProperty]
    public partial string? ChannelBrowserError { get; set; }

    [ObservableProperty]
    public partial bool IsAccountMenuOpen { get; set; }

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
    public partial MessageItem? ActiveMessageAction { get; set; }

    [ObservableProperty]
    public partial bool IsMessageMenuOpen { get; set; }

    [ObservableProperty]
    public partial double MessageMenuAnchorX { get; set; }

    [ObservableProperty]
    public partial double MessageMenuAnchorY { get; set; }

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
    public partial bool OpenDetailsByDefault { get; set; }

    [ObservableProperty]
    public partial bool AreChannelsExpanded { get; set; } = true;

    [ObservableProperty]
    public partial bool AreDirectMessagesExpanded { get; set; } = true;

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
    public partial string DetailsUnavailableMessage { get; set; } = "成员关系、presence、共同频道与频道管理暂不可用。";

    [ObservableProperty]
    public partial string NavigationUnreadLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasNavigationUnread { get; set; }

    public double ComposerHeight
    {
        get => _composerHeight;
        set => SetProperty(ref _composerHeight, Math.Clamp(value, MinimumComposerHeight, MaximumComposerHeight));
    }

    public bool LoginVisible => !IsLoggedIn;
    public bool MainVisible => IsLoggedIn;
    public bool HasSelectedConversation => _session.SelectedConversation is not null;
    public bool IsConversationContentVisible =>
        !IsAuthoritativeEmptyChannel &&
        _session.SelectedConversation is { } selected &&
        string.Equals(_displayedConversationKey, selected.CanonicalKey, StringComparison.Ordinal);
    public string ComposerPlaceholder => HasSelectedConversation
        ? $"发送到 {ConversationTitle}"
        : "发送到当前会话";
    public bool HasMessages => Messages.Count > 0;
    public bool IsMessageListEmpty => !HasMessages;
    public bool IsMessageCollectionVisible => IsConversationContentVisible && HasMessages;
    public bool ShowMessageEmptyState =>
        !HasSelectedConversation && !IsNavigationPending ||
        IsConversationContentVisible && !HasMessages && !IsConversationLoading && !HasConversationActivationError;
    public bool ShowConversationLoadingIndicator => IsConversationLoading && !HasMessages;
    public bool HasTopics => Topics.Count > 0;
    public bool HasSelectedChannel => SelectedChannel is not null;
    public bool ShowTopicPicker => AreChannelsExpanded && HasSelectedChannel && Topics.Count > 1;
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
    public bool IsWideLayout => LayoutMode == ShellLayoutMode.Wide;
    public bool IsCompactLayout => LayoutMode == ShellLayoutMode.Compact;
    public bool IsIntermediateLayout => LayoutMode == ShellLayoutMode.Compact && _viewportWidth <= IntermediateLayoutMaximum;
    public bool IsNarrowLayout => LayoutMode == ShellLayoutMode.Narrow;
    public bool IsNotNarrowLayout => !IsNarrowLayout;
    public bool IsConversationPaneVisible =>
        IsMessagesSection && (!IsNarrowLayout || IsConversationListVisibleOnNarrow);
    public bool IsChatPaneVisible =>
        IsMessagesSection && (!IsNarrowLayout || !IsConversationListVisibleOnNarrow);
    public bool IsInlineDetailsVisible => IsMessagesSection && IsWideLayout && IsDetailsOpen;
    public bool IsOverlayDetailsVisible => IsMessagesSection && !IsWideLayout && IsDetailsOpen;
    public bool IsModalOverlayVisible => IsOverlayDetailsVisible || IsSearchOpen || IsMessageMenuOpen || IsAccountMenuOpen ||
        IsComposerEmojiPickerOpen || IsReactionPickerOpen || IsEditDialogOpen ||
        IsDeleteConfirmationOpen || IsChannelUnsubscribeConfirmationOpen || IsImageViewerOpen ||
        IsNewConversationOpen || IsChannelBrowserOpen || LogoutConfirmationVisible;
    public bool IsPrimaryShellEnabled => !IsModalOverlayVisible || IsMessageMenuOpen || IsAccountMenuOpen ||
        IsComposerEmojiPickerOpen || IsReactionPickerOpen;
    public bool CanCompose =>
        IsConversationContentVisible &&
        !HasConversationActivationError &&
        _projectedState.Connection.Status == RelayCove.Core.ConnectionStatus.Connected;
    public bool CanSend => CanCompose && (!string.IsNullOrWhiteSpace(ComposerText) || HasAttachments) &&
        Attachments.All(attachment => attachment.Status != AttachmentUploadStatus.Uploading);
    public bool CanMarkRead => CanCompose;
    public bool HasUnavailableFeatureMessage => !string.IsNullOrWhiteSpace(UnavailableFeatureMessage);
    public bool HasLoginError => !string.IsNullOrWhiteSpace(LoginError);
    public bool HasAttachments => Attachments.Count > 0;
    public bool HasAttachmentError => !string.IsNullOrWhiteSpace(AttachmentError);
    public bool HasMediaActionStatus => !string.IsNullOrWhiteSpace(MediaActionStatus);
    public bool HasMessageLoadError => !string.IsNullOrWhiteSpace(MessageLoadError);
    public bool ShowHistoryRetry => HasMessageLoadError && !HasConversationActivationError;
    public bool HasSearchResults => SearchResults.Count > 0;
    public bool IsSearchEmpty => !HasSearchResults;
    public bool HasSearchError => !string.IsNullOrWhiteSpace(SearchError);
    public bool HasMoreSearchResults => _searchBeforeMessageId is not null;
    public bool HasMoreSavedMessages => _savedBeforeMessageId is not null;
    public bool HasSavedMessages => SavedMessages.Count > 0;
    public bool IsSavedEmpty => !HasSavedMessages && !IsSavedLoading && string.IsNullOrWhiteSpace(SavedError);
    public bool HasSavedError => !string.IsNullOrWhiteSpace(SavedError);
    public bool HasNewConversationChoices => NewConversationChoices.Count > 0;
    public bool IsNewConversationChoiceEmpty => !HasNewConversationChoices;
    public bool IsNewConversationChoicesVisible => IsNewDirectConversationMode && HasNewConversationChoices;
    public bool IsNewConversationChoiceEmptyVisible => IsNewDirectConversationMode && IsNewConversationChoiceEmpty;
    public bool CanStartNewConversation => _allNewConversationChoices.Any(choice => choice.IsSelected);
    public bool IsNewDirectConversationMode => !IsNewChannelConversationMode;
    public bool CanStartNewChannelConversation => NewConversationChannel is not null &&
        !string.IsNullOrWhiteSpace(NewConversationTopic);
    public bool HasActiveMessageAction => ActiveMessageAction is not null;
    public bool CanEditActiveMessage => ActiveMessageAction?.CanEditOrDelete == true;
    public bool CanDeleteActiveMessage => ActiveMessageAction?.CanEditOrDelete == true;
    public bool CanStarActiveMessage => ActiveMessageAction?.CanMutate == true && CanCompose;
    public bool CanUnsubscribeSelectedChannel =>
        _session.SelectedConversation is ChannelTopic selected &&
        _projectedState.Subscriptions.ContainsKey(selected.ChannelId) &&
        _projectedState.Connection.Status == RelayCove.Core.ConnectionStatus.Connected;
    public bool HasChannelUnsubscribeError => !string.IsNullOrWhiteSpace(ChannelUnsubscribeError);
    public bool CanCloseChannelUnsubscribe => !IsChannelUnsubscribeBusy;
    public bool HasChannelBrowserError => !string.IsNullOrWhiteSpace(ChannelBrowserError);
    public bool CanManageSelectedChannel => CanUnsubscribeSelectedChannel;
    public string SelectedChannelMuteLabel => _session.SelectedConversation is ChannelTopic selected &&
        _projectedState.Subscriptions.GetValueOrDefault(selected.ChannelId)?.IsMuted == true ? "取消静音" : "静音频道";
    public string SelectedChannelPinLabel => _session.SelectedConversation is ChannelTopic selected &&
        _projectedState.Subscriptions.GetValueOrDefault(selected.ChannelId)?.IsPinned == true ? "取消置顶" : "置顶频道";
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
    public double ChannelListHeight => AreChannelsExpanded ? Math.Min(FilteredChannels.Count * 67d, 268d) : 0d;
    public double TopicListHeight => ShowTopicPicker
        ? Math.Min(Topics.Count * 36d, 144d)
        : 0d;
    public string CurrentUserDisplayName => _session.CurrentUserId is { } currentUserId &&
        _projectedState.Users.TryGetValue(currentUserId, out var currentUser)
            ? currentUser.FullName
            : "当前账户";
    public string CurrentUserInitial => string.IsNullOrWhiteSpace(CurrentUserDisplayName)
        ? "我"
        : CurrentUserDisplayName.Trim()[0].ToString().ToUpperInvariant();
    public string? CurrentUserAvatarUrl => _session.CurrentUserId is { } currentUserId &&
        _projectedState.Users.TryGetValue(currentUserId, out var currentUser)
            ? currentUser.AvatarUrl
            : null;
    public string WorkspaceDisplayName
    {
        get
        {
            var host = _session.ActiveRealm?.Uri.Host ?? TryGetRealmHost(Realm);
            return string.Equals(host, "preview.invalid", StringComparison.OrdinalIgnoreCase)
                ? "Acme Workspace"
                : host ?? "RelayCove";
        }
    }
    public bool IsNativePreview => string.Equals(
        _session.ActiveRealm?.Uri.Host,
        "preview.invalid",
        StringComparison.OrdinalIgnoreCase);
    public string NativePreviewStatus => "本地演示数据 · 不连接 Zulip";
    public bool ShowConnectionStatus => IsLoggedIn &&
        _projectedState.Connection.Status != RelayCove.Core.ConnectionStatus.Connected;
    public bool HasCurrentConversationUnread => _session.SelectedConversation is { } selected &&
        GetConversationUnread(_projectedState.Unread, selected) > 0;
    public bool ShowLoadOlderButton => IsConversationContentVisible && !IsNativePreview && !HasReachedOldestMessage;
    public bool ShowNewMessagesButton => NewMessageCount > 0;
    public string NewMessagesButtonText => $"{NewMessageCount} 条新消息";
    public string MessageEmptyTitle => !HasSelectedConversation
        ? "选择一个会话"
        : _projectedState.Connection.Status == RelayCove.Core.ConnectionStatus.Offline
            ? "当前离线缓存没有更多可显示消息"
            : "这里还没有消息";
    public string DetailsButtonDescription => IsDetailsOpen ? "收起会话详情" : "展开会话详情";
    public GridLength ConversationPaneWidth =>
        IsNarrowLayout
            ? IsConversationListVisibleOnNarrow ? GridLength.Star : new GridLength(0)
            : new GridLength(ConversationWidthSliderValue);
    public GridLength NavigationRailWidth => new(IsIntermediateLayout ? 48d : 60d);
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
            var chatPaneWidth = Math.Max(0d, _viewportWidth - NavigationRailWidth.Value - conversationWidth - detailsWidth);
            var messageContentWidth = Math.Max(0d, chatPaneWidth - 40d);
            var rowWidth = Math.Min(690d, messageContentWidth * (IsNarrowLayout ? 0.90d : 0.76d));

            return Math.Max(0d, rowWidth);
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0) return;
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
            if (OpenDetailsByDefault && IsWideLayout && _session.SelectedConversation is not null)
            {
                IsDetailsOpen = true;
            }
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
        if (!IsLoggedIn || _session.SelectedConversation is not null) return;
        if (_session.RecentDirectMessages.FirstOrDefault() is DirectMessage recentDirect)
        {
            var directItem = DirectMessages.FirstOrDefault(item => string.Equals(
                item.Conversation.CanonicalKey,
                recentDirect.CanonicalKey,
                StringComparison.Ordinal)) ?? CreateDirectNavigationItem(_projectedState, recentDirect);
            _ = ActivateConversationFromNavigationAsync(recentDirect, null, null, directItem);
            return;
        }

        var recentTopic = _projectedState.Topics.Values
            .OrderByDescending(topic => topic.MaxMessageId)
            .ThenBy(topic => topic.Topic, StringComparer.Ordinal)
            .FirstOrDefault();
        if (recentTopic is null) return;
        var topicItem = new TopicItem(
            recentTopic.ChannelId,
            recentTopic.Topic,
            recentTopic.MaxMessageId,
            GetConversationUnread(_projectedState.Unread, new ChannelTopic(recentTopic.ChannelId, recentTopic.Topic)),
            IsSelected: true);
        _ = ActivateConversationFromNavigationAsync(
            new ChannelTopic(recentTopic.ChannelId, recentTopic.Topic),
            Channels.FirstOrDefault(channel => channel.ChannelId == recentTopic.ChannelId),
            topicItem,
            null);
    }

    internal void ApplyNativePreviewScene(string? scene)
    {
        switch (scene?.Trim().ToLowerInvariant())
        {
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
            case "details":
                CloseTransientOverlays();
                IsDetailsOpen = true;
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

    public void UpdateViewport(double width)
    {
        if (!double.IsFinite(width) || width <= 0) return;
        _viewportWidth = width;
        var next = width >= WideLayoutMinimum
            ? ShellLayoutMode.Wide
            : width <= NarrowLayoutMaximum
                ? ShellLayoutMode.Narrow
                : ShellLayoutMode.Compact;
        if (next == LayoutMode)
        {
            OnPropertyChanged(nameof(MessageRowMaximumWidth));
            OnPropertyChanged(nameof(IsIntermediateLayout));
            OnPropertyChanged(nameof(NavigationRailWidth));
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
        if (_disposed || _isWindowActive == isActive) return;
        _isWindowActive = isActive;
        if (!isActive)
        {
            CancelAutoMarkReadOperation(allowRetry: true);
            return;
        }

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

    [RelayCommand(IncludeCancelCommand = true, AllowConcurrentExecutions = false)]
    private async Task LoadOlderAsync(CancellationToken cancellationToken)
    {
        if (_session.SelectedConversation is null || HasReachedOldestMessage) return;
        MessageLoadError = null;
        try
        {
            if (!await ExecuteSessionActionAsync(() => _session.LoadOlderAsync(cancellationToken)))
            {
                MessageLoadError = LoginError ?? "无法加载更早消息，请稍后重试。";
            }
        }
        finally
        {
            ProjectHistoryState(_session.SelectedConversation);
        }
    }

    internal async Task ReportMessageViewportAsync(
        int firstVisibleItemIndex,
        int lastVisibleItemIndex,
        double verticalOffset,
        long? timestampMilliseconds = null,
        double? bottomDistanceDip = null)
    {
        _ = verticalOffset;
        UpdateMessageViewportBottomState(bottomDistanceDip, lastVisibleItemIndex);

        // Programmatic realization and the final native jump both raise viewport
        // callbacks. Starting pagination from those transient positions lets a
        // prepend-anchor restore compete with the authoritative latest request.
        if (PendingMessageScrollRequest is not null || IsNavigationPending)
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

    [RelayCommand]
    private void ScrollToLatest()
    {
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

    internal void ReportMessageBottomDistance(double bottomDistanceDip)
    {
        if (!double.IsFinite(bottomDistanceDip)) return;
        UpdateMessageViewportBottomState(Math.Max(0d, bottomDistanceDip), -1);
    }

    private void UpdateMessageViewportBottomState(double? bottomDistanceDip, int lastVisibleItemIndex)
    {
        var isNearBottom = MessageViewportPolicy.IsNearBottom(bottomDistanceDip, lastVisibleItemIndex, Messages.Count);
        _isMessageViewportNearBottom = isNearBottom;
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

    [RelayCommand(IncludeCancelCommand = true, AllowConcurrentExecutions = false)]
    private async Task SendAsync(CancellationToken cancellationToken)
    {
        var conversation = _session.SelectedConversation;
        if (conversation is null) return;
        var content = ComposerText;
        var attachmentSnapshot = Attachments.ToArray();
        if (string.IsNullOrWhiteSpace(content) && attachmentSnapshot.Length == 0) return;
        CancelActivationScrollForUserInteraction(conversation);

        var key = conversation.CanonicalKey;
        var version = _draftVersions.GetValueOrDefault(key);
        await ExecuteSessionActionAsync(async () =>
        {
            AttachmentError = null;
            foreach (var attachment in attachmentSnapshot)
            {
                if (attachment.Uploaded is not null) continue;
                if (attachment.Status == AttachmentUploadStatus.Uncertain)
                {
                    throw new InvalidOperationException("An attachment upload must be explicitly retried.");
                }
                attachment.Status = AttachmentUploadStatus.Uploading;
                OnPropertyChanged(nameof(CanSend));
                try
                {
                    await using var stream = await attachment.File.OpenReadAsync(cancellationToken);
                    attachment.Uploaded = await _session.UploadAttachmentAsync(
                        new AttachmentUpload(
                            attachment.FileName,
                            attachment.File.ContentType,
                            attachment.Length,
                            stream),
                        cancellationToken);
                    attachment.Status = AttachmentUploadStatus.Uploaded;
                }
                catch (GatewayException exception)
                {
                    attachment.Status = exception.Kind is GatewayErrorKind.Offline or GatewayErrorKind.Server or GatewayErrorKind.Protocol
                        ? AttachmentUploadStatus.Uncertain
                        : AttachmentUploadStatus.Failed;
                    AttachmentError = attachment.Status == AttachmentUploadStatus.Uncertain
                        ? "附件上传结果未知；不会自动重试。请确认后显式重试或移除。"
                        : "附件上传失败；请检查限制或权限后显式重试。";
                    throw;
                }
                catch (OperationCanceledException)
                {
                    attachment.Status = AttachmentUploadStatus.Uncertain;
                    AttachmentError = "附件上传已取消且结果未知；不会自动重试。";
                    throw;
                }
                catch
                {
                    attachment.Status = AttachmentUploadStatus.Failed;
                    AttachmentError = "无法读取或上传附件。";
                    throw;
                }
                finally
                {
                    OnPropertyChanged(nameof(CanSend));
                }
            }

            var uploadedMarkdown = attachmentSnapshot
                .Select(attachment => attachment.Uploaded is { } uploaded
                    ? BuildUploadedAttachmentMarkdown(uploaded)
                    : null)
                .Where(markdown => markdown is not null)
                .Cast<string>()
                .ToArray();
            var sendContent = string.Join(
                "\n",
                new[] { content.TrimEnd() }.Where(value => value.Length > 0).Concat(uploadedMarkdown));
            await _session.SendAsync(conversation, sendContent, cancellationToken);
            QueueScrollToLatest(MessageScrollReason.RealtimeFollow);
            if (_draftVersions.GetValueOrDefault(key) != version) return;
            var current = _drafts.GetValueOrDefault(key, string.Empty);
            if (!string.Equals(current, content, StringComparison.Ordinal)) return;
            var currentAttachments = _attachmentDrafts.GetValueOrDefault(key) ?? [];
            if (currentAttachments.Count != attachmentSnapshot.Length ||
                !currentAttachments.SequenceEqual(attachmentSnapshot)) return;

            _drafts.Remove(key);
            _attachmentDrafts.Remove(key);
            _draftVersions[key] = version + 1;
            if (string.Equals(_activeDraftKey, key, StringComparison.Ordinal))
            {
                SetComposerTextWithoutTracking(string.Empty);
                Reconcile(Attachments, [], item => item.Id);
                NotifyAttachmentProperties();
            }
        });
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
        if (await ExecuteSessionActionAsync(
                () => _session.LogoutAsync(),
                "注销未完全完成，请重试以安全删除凭据并锁定本地缓存。"))
        {
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
            ComposerText = message.Content;
        }
    }

    [RelayCommand(IncludeCancelCommand = true, AllowConcurrentExecutions = false)]
    private async Task ConfirmClearCacheAsync(CancellationToken cancellationToken)
    {
        if (!ClearCacheConfirmationVisible) return;
        await ExecuteSessionActionAsync(() => _session.ClearLocalCacheAsync(cancellationToken));
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

    [RelayCommand(IncludeCancelCommand = true, AllowConcurrentExecutions = false)]
    private async Task ShowSavedAsync(CancellationToken cancellationToken)
    {
        CloseTransientOverlays();
        SelectedSection = ShellSection.Saved;
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

    [RelayCommand]
    private void ToggleChannels() => AreChannelsExpanded = !AreChannelsExpanded;

    [RelayCommand]
    private void ToggleDirectMessages() => AreDirectMessagesExpanded = !AreDirectMessagesExpanded;

    [RelayCommand]
    private void ShowAppearanceSettings() => SelectedSettingsCategory = SettingsCategory.Appearance;

    [RelayCommand]
    private void ShowGeneralSettings() => SelectedSettingsCategory = SettingsCategory.General;

    [RelayCommand]
    private void ShowNotificationSettings() => SelectedSettingsCategory = SettingsCategory.Notifications;

    [RelayCommand]
    private void ShowStorageSettings() => SelectedSettingsCategory = SettingsCategory.Storage;

    [RelayCommand]
    private void ShowAccountSettings() => SelectedSettingsCategory = SettingsCategory.Account;

    [RelayCommand]
    private void OpenSearch()
    {
        CloseTransientOverlays();
        IsSearchOpen = true;
        ProjectSearch();
        ScheduleServerSearch(SearchQuery, immediate: false);
    }

    [RelayCommand]
    private void CloseSearch()
    {
        CancelSearchInput();
        _serverSearchResults = [];
        IsSearchOpen = false;
        SelectedSearchResult = null;
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task SelectSearchResultAsync(SearchResultItem? result)
    {
        result ??= SelectedSearchResult;
        if (result is null) return;
        if (result.Conversation is null && result.ChannelId is { } channelId)
        {
            SelectedSection = ShellSection.Messages;
            if (Channels.FirstOrDefault(channel => channel.ChannelId == channelId) is { } channel)
            {
                ActivateChannel(channel);
            }
            CloseSearch();
            return;
        }
        if (result.Conversation is null) return;
        var opened = result.MessageId is { } messageId
            ? await ExecuteSessionActionAsync(() => _session.OpenMessageAsync(result.Conversation, messageId))
            : await ActivateConversationFromNavigationAsync(result.Conversation, null, null, null);
        if (opened)
        {
            SelectedSection = ShellSection.Messages;
            if (IsNarrowLayout) IsConversationListVisibleOnNarrow = false;
            CloseSearch();
        }
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private Task SearchNowAsync() => RunServerSearchAsync(SearchQuery, immediate: true, CancellationToken.None);

    [RelayCommand(IncludeCancelCommand = true, AllowConcurrentExecutions = false)]
    private async Task LoadOlderSearchAsync(CancellationToken cancellationToken)
    {
        var query = SearchQuery.Trim();
        if (_searchBeforeMessageId is null || string.IsNullOrWhiteSpace(query) || !IsSearchOpen) return;
        CancelSearchInput();
        var generation = ++_searchInputGeneration;
        var accountId = _session.AccountId;
        if (accountId is null) return;
        try
        {
            IsSearchBusy = true;
            SearchError = null;
            var page = await _session.SearchMessagesAsync(query, _searchBeforeMessageId, 50, cancellationToken).ConfigureAwait(false);
            if (!IsSearchCurrent(generation, accountId.Value) || !IsSearchOpen || !string.Equals(SearchQuery.Trim(), query, StringComparison.Ordinal)) return;
            if (!page.FoundAnchor)
            {
                _searchBeforeMessageId = null;
                SearchError = "搜索结果已变化，请刷新搜索。";
                OnPropertyChanged(nameof(HasMoreSearchResults));
                return;
            }
            var existing = _serverSearchResults.Select(result => result.Id).ToHashSet(StringComparer.Ordinal);
            var older = page.Messages.OrderByDescending(message => message.Id).Select(ToSearchResult)
                .Where(result => existing.Add(result.Id)).ToArray();
            _serverSearchResults = _serverSearchResults.Concat(older).ToArray();
            _searchBeforeMessageId = page.FoundOldest ? null : page.Messages.MinBy(message => message.Id)?.Id;
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
        if (message is null) return;
        if (await ExecuteSessionActionAsync(() => _session.OpenMessageAsync(message.Conversation, message.MessageId)))
        {
            SelectedSection = ShellSection.Messages;
            if (IsNarrowLayout) IsConversationListVisibleOnNarrow = false;
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
        NewConversationChannel = Channels.FirstOrDefault();
        NewConversationTopic = string.Empty;
        ClearNewConversationChoices();
        foreach (var user in _projectedState.Users.Values
                     .Where(user => user.IsActive && user.UserId != _session.CurrentUserId)
                     .OrderBy(user => user.FullName, StringComparer.Ordinal)
                     .ThenBy(user => user.UserId))
        {
            var choice = new ConversationContactChoice(user.UserId, user.FullName, user.AvatarUrl, user.IsBot);
            choice.PropertyChanged += OnNewConversationChoiceChanged;
            _allNewConversationChoices.Add(choice);
        }
        NewConversationQuery = string.Empty;
        ProjectNewConversationChoices();
        IsNewConversationOpen = true;
    }

    [RelayCommand]
    private void CloseNewConversation()
    {
        IsNewConversationOpen = false;
        NewConversationQuery = string.Empty;
        NewConversationTopic = string.Empty;
        ClearNewConversationChoices();
    }

    [RelayCommand]
    private void ShowNewDirectConversation() => IsNewChannelConversationMode = false;

    [RelayCommand]
    private void ShowNewChannelConversation()
    {
        if (!IsNewConversationOpen) OpenNewConversation();
        IsNewChannelConversationMode = true;
        NewConversationChannel ??= Channels.FirstOrDefault();
    }

    [RelayCommand]
    private void OpenNewChannelTopic()
    {
        OpenNewConversation();
        IsNewChannelConversationMode = true;
        NewConversationChannel = SelectedChannel ?? Channels.FirstOrDefault();
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task StartNewChannelConversationAsync()
    {
        var channel = NewConversationChannel;
        var topic = NewConversationTopic.Trim();
        if (channel is null || topic.Length == 0) return;
        var conversation = new ChannelTopic(channel.ChannelId, topic);
        _loadedTopics = _loadedTopics
            .Where(item => !string.Equals(
                new ChannelTopic(item.ChannelId, item.Topic).CanonicalKey,
                conversation.CanonicalKey,
                StringComparison.Ordinal))
            .Append(new TopicSummary(channel.ChannelId, topic))
            .ToArray();
        _loadedTopicsChannelId = channel.ChannelId;
        _hasAuthoritativeTopics = true;
        var topicItem = new TopicItem(channel.ChannelId, topic, null, IsSelected: true);
        if (await ActivateConversationFromNavigationAsync(conversation, channel, topicItem, null))
        {
            _lastSelectedTopicByChannel[channel.ChannelId] = topic;
            SelectedSection = ShellSection.Messages;
            if (IsNarrowLayout) IsConversationListVisibleOnNarrow = false;
            CloseNewConversation();
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
        if (userIds.Length == 0) return;
        if (await ActivateConversationFromNavigationAsync(new DirectMessage(userIds), null, null, null))
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

    private void AddAttachmentSelection(IReadOnlyList<SelectedAttachmentFile> selected)
    {
        if (selected.Count == 0) return;
        var error = ValidateAttachmentSelection(Attachments, selected, _session.MaxFileUploadBytes);
        if (error is not null)
        {
            AttachmentError = error;
            return;
        }
        foreach (var file in selected) Attachments.Add(new AttachmentDraftItem(file));
        ComposerHeight = Math.Max(ComposerHeight, 184d);
        SaveCurrentAttachmentDrafts();
        NotifyAttachmentProperties();
    }

    [RelayCommand]
    private void RemoveAttachment(AttachmentDraftItem? attachment)
    {
        if (attachment?.CanRemove != true || !Attachments.Remove(attachment)) return;
        SaveCurrentAttachmentDrafts();
        NotifyAttachmentProperties();
    }

    [RelayCommand]
    private void RetryAttachment(AttachmentDraftItem? attachment)
    {
        if (attachment?.CanRetry != true) return;
        attachment.Status = AttachmentUploadStatus.Pending;
        attachment.Uploaded = null;
        AttachmentError = null;
        SaveCurrentAttachmentDrafts();
        NotifyAttachmentProperties();
    }

    [RelayCommand]
    private void OpenImageViewer(MessageAttachmentItem? attachment)
    {
        if (attachment?.IsImage != true) return;
        CloseTransientOverlays();
        MediaActionStatus = null;
        ActiveImageAttachment = attachment;
        IsImageViewerOpen = true;
    }

    [RelayCommand]
    private void CloseImageViewer()
    {
        IsImageViewerOpen = false;
        ActiveImageAttachment = null;
        MediaActionStatus = null;
        MessageActionFocusRequest++;
    }

    [RelayCommand(IncludeCancelCommand = true, AllowConcurrentExecutions = false)]
    private async Task DownloadAttachmentAsync(
        MessageAttachmentItem? attachment,
        CancellationToken cancellationToken)
    {
        if (attachment is null || IsMediaActionBusy) return;
        IsMediaActionBusy = true;
        MediaActionStatus = $"正在下载 {attachment.Name}…";
        try
        {
            var result = await _realmMediaService.GetFileAsync(attachment.SourceUrl, cancellationToken);
            var saved = await _fileSaveService.SaveAsync(attachment.Name, result.Content, cancellationToken);
            MediaActionStatus = saved ? $"已保存 {attachment.Name}。" : "已取消保存。";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            MediaActionStatus = "下载已取消。";
        }
        catch (GatewayException)
        {
            MediaActionStatus = "无法安全下载附件；请检查连接、权限或文件限制。";
        }
        catch
        {
            MediaActionStatus = "无法保存附件；未泄露远端错误正文。";
        }
        finally
        {
            IsMediaActionBusy = false;
        }
    }

    [RelayCommand]
    private void DismissUnavailableFeature() => UnavailableFeatureMessage = null;

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
        if (choice is null) return;
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
        MessageMenuAnchorX = Math.Max(0d, request.AnchorX);
        MessageMenuAnchorY = Math.Max(0d, request.AnchorY);
        IsMessageMenuOpen = true;
    }

    [RelayCommand]
    private void CloseMessageMenu()
    {
        IsMessageMenuOpen = false;
        ActiveMessageAction = null;
        MessageActionFocusRequest++;
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

    [RelayCommand(AllowConcurrentExecutions = false)]
    private Task CopyMessageRawAsync(MessageItem? message) =>
        CopyMessageValueAsync(message ?? ActiveMessageAction, static item => item.Content, "消息正文");

    [RelayCommand(AllowConcurrentExecutions = false)]
    private Task CopyMessageIdAsync(MessageItem? message) =>
        CopyMessageValueAsync(message ?? ActiveMessageAction, static item => item.MessageId?.ToString(), "消息 ID");

    [RelayCommand(AllowConcurrentExecutions = false)]
    private Task CopyMessageLinkAsync(MessageItem? message) =>
        CopyMessageValueAsync(message ?? ActiveMessageAction, static item => item.Permalink, "消息链接");

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task OpenMessageInZulipAsync(MessageItem? message)
    {
        message ??= ActiveMessageAction;
        if (message?.Permalink is null || !Uri.TryCreate(message.Permalink, UriKind.Absolute, out var uri)) return;
        try
        {
            await _platformInteractions.OpenUriAsync(uri);
            CloseMessageMenu();
        }
        catch
        {
            LoginError = "无法打开 Zulip 消息链接。";
        }
    }

    [RelayCommand]
    private void OpenReactionPicker(MessageItem? message)
    {
        message ??= ActiveMessageAction;
        if (message?.CanMutate != true || !CanCompose) return;
        CloseTransientOverlays();
        ActiveMessageAction = message;
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
        if (choice is null || message?.MessageId is not { } messageId) return;
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
        return message?.MessageId is { } messageId
            ? ExecuteSessionActionAsync(() => _session.SetMessageStarredAsync(messageId, !message.IsStarred))
            : Task.CompletedTask;
    }

    [RelayCommand]
    private void OpenEditDialog(MessageItem? message)
    {
        message ??= ActiveMessageAction;
        if (message?.CanEditOrDelete != true) return;
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
        if (await ExecuteSessionActionAsync(() => _session.EditMessageAsync(messageId, EditMessageText)))
        {
            CancelEditDialog();
        }
    }

    [RelayCommand]
    private void RequestDeleteMessage(MessageItem? message)
    {
        message ??= ActiveMessageAction;
        if (message?.CanEditOrDelete != true) return;
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
        CloseTransientOverlays();
        _channelUnsubscribeTargetId = selected.ChannelId;
        ChannelUnsubscribeTargetName = subscription.Name;
        ChannelUnsubscribeError = null;
        IsChannelUnsubscribeConfirmationOpen = true;
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

    private async Task SetSelectedChannelPreferenceAsync(SubscriptionPreference preference)
    {
        if (_session.SelectedConversation is not ChannelTopic selected ||
            !_projectedState.Subscriptions.TryGetValue(selected.ChannelId, out var subscription)) return;
        var value = preference == SubscriptionPreference.Muted ? !subscription.IsMuted : !subscription.IsPinned;
        await ExecuteSessionActionAsync(() => _session.SetSubscriptionPreferenceAsync(selected.ChannelId, preference, value));
    }

    [RelayCommand]
    private void ToggleDetails()
    {
        if (!HasSelectedConversation) return;
        IsDetailsOpen = !IsDetailsOpen;
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

    private async Task CopyMessageValueAsync(
        MessageItem? message,
        Func<MessageItem, string?> selector,
        string description)
    {
        if (message is null) return;
        var value = selector(message);
        if (string.IsNullOrEmpty(value)) return;
        try
        {
            await _platformInteractions.CopyTextAsync(value);
            CloseMessageMenu();
            UnavailableFeatureMessage = $"已复制{description}。";
        }
        catch
        {
            LoginError = $"无法复制{description}。";
        }
    }

    private void CloseTransientOverlays()
    {
        IsSearchOpen = false;
        IsAccountMenuOpen = false;
        IsFileDragActive = false;
        IsComposerEmojiPickerOpen = false;
        IsReactionPickerOpen = false;
        IsMessageMenuOpen = false;
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
        MediaActionStatus = null;
        ActiveMessageAction = null;
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

    partial void OnIsChannelUnsubscribeBusyChanged(bool value) =>
        OnPropertyChanged(nameof(CanCloseChannelUnsubscribe));

    partial void OnComposerTextChanged(string value)
    {
        if (!_suppressDraftTracking && _activeDraftKey is not null)
        {
            _drafts[_activeDraftKey] = value;
            _draftVersions[_activeDraftKey] = _draftVersions.GetValueOrDefault(_activeDraftKey) + 1;
        }

        OnPropertyChanged(nameof(CanSend));
        ComposerCursorPosition = Math.Clamp(ComposerCursorPosition, 0, value.Length);
        ComposerSelectionLength = Math.Clamp(ComposerSelectionLength, 0, value.Length - ComposerCursorPosition);
    }

    partial void OnSearchQueryChanged(string value)
    {
        ProjectSearch();
        ScheduleServerSearch(value, immediate: false);
    }
    partial void OnSearchErrorChanged(string? value) => OnPropertyChanged(nameof(HasSearchError));
    partial void OnSavedErrorChanged(string? value)
    {
        OnPropertyChanged(nameof(HasSavedError));
        OnPropertyChanged(nameof(IsSavedEmpty));
    }
    partial void OnNewConversationQueryChanged(string value) => ProjectNewConversationChoices();
    partial void OnConversationTitleChanged(string value) =>
        OnPropertyChanged(nameof(ComposerPlaceholder));
    partial void OnAttachmentErrorChanged(string? value) =>
        OnPropertyChanged(nameof(HasAttachmentError));
    partial void OnMediaActionStatusChanged(string? value) =>
        OnPropertyChanged(nameof(HasMediaActionStatus));
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
    partial void OnIsAccountMenuOpenChanged(bool value) => NotifyOverlayProperties();
    partial void OnIsNewConversationOpenChanged(bool value)
    {
        if (!value) ClearNewConversationChoices();
        NotifyOverlayProperties();
    }
    partial void OnIsChannelBrowserOpenChanged(bool value) => NotifyOverlayProperties();
    partial void OnIsComposerEmojiPickerOpenChanged(bool value) => NotifyOverlayProperties();
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

    partial void OnIsMessageMenuOpenChanged(bool value) => NotifyOverlayProperties();
    partial void OnIsEditDialogOpenChanged(bool value) => NotifyOverlayProperties();
    partial void OnIsDeleteConfirmationOpenChanged(bool value) => NotifyOverlayProperties();
    partial void OnIsChannelUnsubscribeConfirmationOpenChanged(bool value) => NotifyOverlayProperties();
    partial void OnIsImageViewerOpenChanged(bool value) => NotifyOverlayProperties();
    partial void OnLogoutConfirmationVisibleChanged(bool value) => NotifyOverlayProperties();

    partial void OnActiveMessageActionChanged(MessageItem? value)
    {
        OnPropertyChanged(nameof(HasActiveMessageAction));
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
    }

    partial void OnLayoutModeChanged(ShellLayoutMode value) => NotifyLayoutProperties();

    partial void OnIsDetailsOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(IsInlineDetailsVisible));
        OnPropertyChanged(nameof(IsOverlayDetailsVisible));
        OnPropertyChanged(nameof(IsPrimaryShellEnabled));
        OnPropertyChanged(nameof(InlineDetailsWidth));
        OnPropertyChanged(nameof(MessageRowMaximumWidth));
        OnPropertyChanged(nameof(DetailsButtonDescription));
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

    partial void OnOpenDetailsByDefaultChanged(bool value) => SaveUiPreferences();

    partial void OnAreChannelsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(ChannelListHeight));
        OnPropertyChanged(nameof(TopicListHeight));
        OnPropertyChanged(nameof(ShowTopicPicker));
        OnPropertyChanged(nameof(ShowEmptyChannelTopicState));
        SaveUiPreferences();
    }

    partial void OnAreDirectMessagesExpandedChanged(bool value) => SaveUiPreferences();

    partial void OnUnavailableFeatureMessageChanged(string? value) =>
        OnPropertyChanged(nameof(HasUnavailableFeatureMessage));

    partial void OnSelectedChannelChanged(ChannelItem? value)
    {
        OnPropertyChanged(nameof(HasSelectedChannel));
        OnPropertyChanged(nameof(TopicListHeight));
        OnPropertyChanged(nameof(ShowTopicPicker));
        OnPropertyChanged(nameof(ShowEmptyChannelTopicState));
        OnPropertyChanged(nameof(SelectedChannelMuteLabel));
        OnPropertyChanged(nameof(SelectedChannelPinLabel));
        RefreshNavigationSelectionProjection();
    }

    partial void OnSelectedTopicChanged(TopicItem? value)
    {
        RefreshNavigationSelectionProjection();
    }

    partial void OnSelectedDirectMessageChanged(NavigationItem? value)
    {
        RefreshNavigationSelectionProjection();
    }

    partial void OnConversationFilterQueryChanged(string value) => ProjectConversationFilter();

    public void SelectFirstFilteredConversation()
    {
        if (FilteredChannels.FirstOrDefault() is { } channel)
        {
            ActivateChannel(channel);
            return;
        }
        if (FilteredDirectMessages.FirstOrDefault() is { } directMessage)
        {
            ActivateDirectMessage(directMessage);
        }
    }

    public void ClearConversationFilter() => ConversationFilterQuery = string.Empty;

    internal void ActivateChannel(ChannelItem channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ConversationFilterQuery = string.Empty;
        _ = ActivateChannelAsync(channel);
    }

    internal void ActivateTopic(TopicItem topic)
    {
        ArgumentNullException.ThrowIfNull(topic);
        ConversationFilterQuery = string.Empty;
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
        _ = ActivateConversationFromNavigationAsync(directMessage.Conversation, null, null, directMessage);
    }

    partial void OnIsNewChannelConversationModeChanged(bool value) =>
        NotifyNewConversationModeProperties();

    partial void OnNewConversationChannelChanged(ChannelItem? value) =>
        OnPropertyChanged(nameof(CanStartNewChannelConversation));

    partial void OnNewConversationTopicChanged(string value) =>
        OnPropertyChanged(nameof(CanStartNewChannelConversation));

    private void NotifyNewConversationModeProperties()
    {
        OnPropertyChanged(nameof(IsNewDirectConversationMode));
        OnPropertyChanged(nameof(IsNewConversationChoicesVisible));
        OnPropertyChanged(nameof(IsNewConversationChoiceEmptyVisible));
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
        var (generation, cancellation) = BeginNavigation(conversation.CanonicalKey);
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
        ProjectConversation(conversation, _projectedState);
        ProjectDraft(conversation);
        NotifyConversationAvailability();
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
        if (OpenDetailsByDefault && IsWideLayout) IsDetailsOpen = true;
        NotifyConversationAvailability();
        return true;
    }

    private (long Generation, CancellationTokenSource Cancellation) BeginNavigation(string? conversationKey = null)
    {
        CancelAutoMarkReadOperation(allowRetry: true);
        CancelNavigation();
        // A completed activation belongs to the previous visit. Re-entering the
        // same conversation must still position the newly realized native list
        // at its latest message, even when the history generation and target ID
        // are unchanged because the page came from memory/SQLite.
        _lastActivationScrollConversationKey = null;
        _lastActivationScrollGeneration = 0;
        _lastActivationScrollTargetMessageId = 0;
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
    }

    private async Task<bool> ExecuteSessionActionAsync(Func<Task> action, string? failureMessage = null)
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

    private void Project(ClientState state)
    {
        _projectedState = state;
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
        if (SavedMessages.Count > 0)
        {
            var savedIds = SavedMessages.Select(item => item.MessageId).ToHashSet();
            foreach (var saved in SavedMessages.ToArray())
            {
                if (state.Messages.TryGetValue(saved.MessageId, out var message) && !message.IsStarred)
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

        Reconcile(
            Channels,
            state.Subscriptions.Values
                .Where(subscription => subscription.IsActive)
                .OrderByDescending(subscription => subscription.IsPinned)
                .ThenBy(subscription => subscription.IsMuted)
                .ThenBy(subscription => subscription.Name, StringComparer.Ordinal)
                .Select(subscription => CreateChannelItem(state, subscription)),
            item => item.ChannelId);

        ReconcileNavigationItems(
            DirectMessages,
            _session.RecentDirectMessages
                .OfType<DirectMessage>()
                .Select(item => CreateDirectNavigationItem(state, item)),
            item => item.Conversation.CanonicalKey);

        ProjectConversationFilter();

        Reconcile(
            KnownContacts,
            state.Users.Values
                .Where(user => user.IsActive)
                .OrderBy(user => user.FullName, StringComparer.Ordinal)
                .ThenBy(user => user.UserId)
                .Select(user => new ContactItem(user.UserId, user.FullName, user.AvatarUrl, user.IsBot)),
            item => item.UserId);

        ProjectTopics(state, SelectedChannel?.ChannelId);

        var selected = _session.SelectedConversation;
        var selectedKey = selected?.CanonicalKey;
        var conversationChanged = !string.Equals(_projectedConversationKey, selectedKey, StringComparison.Ordinal);
        var previousNewestMessageId = conversationChanged ? null : _newestProjectedMessageId;
        var projectedMessages = BuildMessageItems(state, selected);
        var deferInitialMessageProjection = conversationChanged &&
            IsNavigationPending &&
            selectedKey is not null;
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
            _messages.ReplaceAll(isDeferringInitialMessageProjection ? [] : projectedMessages);
        }
        else if (!isDeferringInitialMessageProjection)
        {
            if (publishDeferredInitialMessageProjection)
            {
                _messages.ReplaceAll(projectedMessages);
                _deferredInitialMessageProjectionConversationKey = null;
            }
            else
            {
                Reconcile(Messages, projectedMessages, item => item.Id);
            }
        }

        var newestMessageId = projectedMessages
            .Select(message => message.MessageId)
            .Max();
        if (conversationChanged)
        {
            NewMessageCount = 0;
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
                 !_session.HistoryState.IsLoading)
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
    }

    private List<MessageItem> BuildMessageItems(ClientState state, ConversationKey? selected)
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
            var showPreviewUnreadDivider = previewUnreadAfterMessageId is { } previewAfter &&
                previousMessageId == previewAfter;
            var mutation = state.MessageMutations.GetValueOrDefault(message.Id);
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
                        EmojiChoices.FirstOrDefault(choice =>
                            string.Equals(choice.Identity.CanonicalKey, identity.CanonicalKey, StringComparison.Ordinal))?.Emoji ?? identity.EmojiName,
                        group.Count(),
                        currentUserId is { } current && group.Any(reaction => reaction.UserId == current),
                        string.Join("、", participants));
                })
                .OrderBy(reaction => reaction.Identity.CanonicalKey, StringComparer.Ordinal)
                .ToArray();
            var item = new MessageItem(
                message.Id.ToString(),
                message.Id,
                message.SenderId,
                message.SenderDisplayName ?? user?.FullName ?? $"用户 {message.SenderId}",
                message.Content,
                localTime.ToString("t"),
                isOwn,
                isUnread,
                user?.IsBot ?? false,
                message.SenderAvatarUrl ?? user?.AvatarUrl,
                message.IsStarred,
                reactions,
                CreatePermalink(message.Id),
                previousDate != date,
                DescribeDate(date, localTime),
                showPreviewUnreadDivider || isUnread && !unreadDividerAdded,
                showPreviewUnreadDivider ? previewUnreadDividerLabel : null,
                DescribeMutation(mutation),
                mutation?.Status is MessageMutationStatus.Submitting or MessageMutationStatus.Uncertain,
                realm: _session.ActiveRealm);
            projected.Add(ReuseMessageItem(existingById, item));
            previousDate = date;
            previousMessageId = message.Id;
            if (showPreviewUnreadDivider || isUnread) unreadDividerAdded = true;
        }

        foreach (var entry in state.Outbox.Values
                     .Where(entry => entry.Conversation == selected && entry.State != OutboxState.Hidden)
                     .OrderBy(entry => entry.CreatedAt))
        {
            var localTime = entry.CreatedAt.LocalDateTime;
            var date = DateOnly.FromDateTime(localTime);
            var item = new MessageItem(
                $"local-{entry.LocalId}",
                null,
                currentUserId,
                "你",
                entry.Content,
                localTime.ToString("t"),
                isOwn: true,
                showDateDivider: previousDate != date,
                dateDividerLabel: DescribeDate(date, localTime),
                deliveryState: DescribeOutbox(entry.State),
                canRecover: entry.State is OutboxState.WaitExpired or OutboxState.Failed,
                recoverCommand: RecoverOutboxCommand,
                realm: _session.ActiveRealm);
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

    private void ScheduleServerSearch(string query, bool immediate)
    {
        CancelSearchInput();
        if (!IsSearchOpen || string.IsNullOrWhiteSpace(query))
        {
            _serverSearchResults = [];
            IsSearchBusy = false;
            SearchError = null;
            return;
        }
        var cancellation = new CancellationTokenSource();
        _searchInputCancellation = cancellation;
        var generation = ++_searchInputGeneration;
        var accountId = _session.AccountId;
        if (accountId is null)
        {
            CancelSearchInput();
            return;
        }
        _searchAccountId = accountId;
        _ = RunServerSearchCoreAsync(query.Trim(), immediate, generation, accountId.Value, cancellation);
    }

    private async Task RunServerSearchAsync(string query, bool immediate, CancellationToken cancellationToken)
    {
        CancelSearchInput();
        if (!IsSearchOpen || string.IsNullOrWhiteSpace(query)) return;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _searchInputCancellation = cancellation;
        var accountId = _session.AccountId;
        if (accountId is null)
        {
            CancelSearchInput();
            return;
        }
        _searchAccountId = accountId;
        await RunServerSearchCoreAsync(query.Trim(), immediate, ++_searchInputGeneration, accountId.Value, cancellation).ConfigureAwait(false);
    }

    private async Task RunServerSearchCoreAsync(string query, bool immediate, long generation, AccountId accountId, CancellationTokenSource cancellation)
    {
        try
        {
            if (!immediate) await Task.Delay(TimeSpan.FromMilliseconds(300), cancellation.Token).ConfigureAwait(false);
            if (!IsSearchCurrent(generation, accountId) || !IsSearchOpen) return;
            IsSearchBusy = true;
            SearchError = null;
            _searchBeforeMessageId = null;
            var page = await _session.SearchMessagesAsync(query, null, 50, cancellation.Token).ConfigureAwait(false);
            if (!IsSearchCurrent(generation, accountId) || !IsSearchOpen || !string.Equals(SearchQuery.Trim(), query, StringComparison.Ordinal)) return;
            _serverSearchResults = page.Messages.OrderByDescending(message => message.Id).Select(ToSearchResult).ToArray();
            ProjectSearch();
            _searchBeforeMessageId = page.FoundOldest ? null : page.Messages.MinBy(message => message.Id)?.Id;
            OnPropertyChanged(nameof(HasMoreSearchResults));
        }
        catch (OperationCanceledException)
        {
        }
        catch (GatewayException exception)
        {
            if (IsSearchCurrent(generation, accountId)) SearchError = DescribeGatewayFailure(exception);
        }
        catch (Exception)
        {
            if (IsSearchCurrent(generation, accountId)) SearchError = "服务器搜索失败，请稍后重试。";
        }
        finally
        {
            if (IsSearchCurrent(generation, accountId)) IsSearchBusy = false;
            if (ReferenceEquals(_searchInputCancellation, cancellation)) _searchInputCancellation = null;
            cancellation.Dispose();
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
            var items = page.Messages.OrderByDescending(message => message.Id).Select(ToSavedMessage).ToArray();
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

    private SearchResultItem ToSearchResult(ChatMessage message)
    {
        var sender = message.SenderDisplayName ?? _projectedState.Users.GetValueOrDefault(message.SenderId)?.FullName ?? $"用户 {message.SenderId}";
        return new SearchResultItem($"server-message:{message.Id}", "服务器消息", sender, TruncateForSearch(message.Content), message.Conversation, message.Id);
    }

    private SavedMessageItem ToSavedMessage(ChatMessage message)
    {
        var sender = message.SenderDisplayName ?? _projectedState.Users.GetValueOrDefault(message.SenderId)?.FullName ?? $"用户 {message.SenderId}";
        return new SavedMessageItem(message.Id, message.Conversation, sender, TruncateForSearch(message.Content), message.Timestamp.LocalDateTime.ToString("g"));
    }

    private void CancelSearchInput()
    {
        _searchInputCancellation?.Cancel();
        _searchInputCancellation = null;
        _searchInputGeneration++;
    }

    private void ProjectSearch()
    {
        var query = SearchQuery.Trim();
        var results = new List<SearchResultItem>();
        foreach (var channel in _projectedState.Subscriptions.Values
                     .Where(channel => channel.IsActive && (query.Length == 0 || Contains(channel.Name, query)))
                     .OrderBy(channel => channel.Name, StringComparer.Ordinal))
        {
            results.Add(new SearchResultItem(
                $"channel:{channel.ChannelId}",
                "频道",
                $"# {channel.Name}",
                "打开频道话题列表",
                ChannelId: channel.ChannelId));
        }
        foreach (var topic in _projectedState.Topics.Values
                     .Where(topic => query.Length == 0 || Contains(topic.Topic, query) ||
                                     Contains(_projectedState.Subscriptions.GetValueOrDefault(topic.ChannelId)?.Name, query))
                     .OrderByDescending(topic => topic.MaxMessageId)
                     .ThenBy(topic => topic.Topic, StringComparer.Ordinal))
        {
            var conversation = new ChannelTopic(topic.ChannelId, topic.Topic);
            var channelName = _projectedState.Subscriptions.GetValueOrDefault(topic.ChannelId)?.Name ?? $"频道 {topic.ChannelId}";
            results.Add(new SearchResultItem(
                $"topic:{conversation.CanonicalKey}",
                "话题",
                string.IsNullOrEmpty(topic.Topic) ? "（无主题）" : topic.Topic,
                $"# {channelName}",
                conversation));
        }
        foreach (var user in _projectedState.Users.Values
                     .Where(user => user.IsActive && (query.Length == 0 || Contains(user.FullName, query)))
                     .OrderBy(user => user.FullName, StringComparer.Ordinal)
                     .ThenBy(user => user.UserId))
        {
            var conversation = user.UserId == _session.CurrentUserId
                ? new DirectMessage([])
                : new DirectMessage([user.UserId]);
            results.Add(new SearchResultItem(
                $"user:{user.UserId}",
                user.IsBot ? "机器人" : "联系人",
                user.FullName,
                "打开私信",
                conversation));
        }
        foreach (var message in _projectedState.Messages.Values
                     .Where(message => query.Length == 0 || Contains(message.Content, query) ||
                                       Contains(message.SenderDisplayName, query))
                     .OrderByDescending(message => message.Id))
        {
            var sender = message.SenderDisplayName ?? _projectedState.Users.GetValueOrDefault(message.SenderId)?.FullName ?? $"用户 {message.SenderId}";
            results.Add(new SearchResultItem(
                $"message:{message.Id}",
                "已加载消息",
                sender,
                TruncateForSearch(message.Content),
                message.Conversation,
                message.Id));
        }

        Reconcile(SearchResults, _serverSearchResults.Concat(results.Take(50)), item => item.Id);
        OnPropertyChanged(nameof(HasSearchResults));
        OnPropertyChanged(nameof(IsSearchEmpty));
    }

    private void ProjectNewConversationChoices()
    {
        var query = NewConversationQuery.Trim();
        Reconcile(
            NewConversationChoices,
            _allNewConversationChoices.Where(choice => query.Length == 0 || Contains(choice.Name, query)),
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
        OnPropertyChanged(nameof(ChannelListHeight));
    }

    private void OnNewConversationChoiceChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(ConversationContactChoice.IsSelected))
        {
            OnPropertyChanged(nameof(CanStartNewConversation));
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
    }

    private void ProjectTopics(ClientState state, long? selectedChannelId)
    {
        if (selectedChannelId is null)
        {
            Reconcile(Topics, [], item => item.CanonicalKey);
            OnPropertyChanged(nameof(HasTopics));
            OnPropertyChanged(nameof(ShowTopicPicker));
            OnPropertyChanged(nameof(ShowEmptyChannelTopicState));
            OnPropertyChanged(nameof(TopicListHeight));
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

        Reconcile(
            Topics,
            topicMap.Values
                .OrderByDescending(topic => topic.MaxMessageId)
                .ThenBy(topic => topic.Topic, StringComparer.Ordinal)
                .Select(topic => new TopicItem(
                    topic.ChannelId,
                    topic.Topic,
                    topic.MaxMessageId,
                    GetConversationUnread(state.Unread, new ChannelTopic(topic.ChannelId, topic.Topic)),
                    string.Equals(SelectedTopic?.CanonicalKey, new ChannelTopic(topic.ChannelId, topic.Topic).CanonicalKey, StringComparison.Ordinal))),
            item => item.CanonicalKey);
        OnPropertyChanged(nameof(HasTopics));
        OnPropertyChanged(nameof(ShowTopicPicker));
        OnPropertyChanged(nameof(ShowEmptyChannelTopicState));
        OnPropertyChanged(nameof(TopicListHeight));
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
            : $"{sender}: {TruncateForSearch(latestMessage.Content)}";

        return new ChannelItem(
            subscription.ChannelId,
            subscription.Name,
            GetChannelUnread(state.Unread, subscription.ChannelId),
            recentTopic?.Topic,
            preview,
            latestMessage is null ? null : FormatConversationTimestamp(latestMessage.Timestamp.LocalDateTime),
            subscription.IsMuted,
            subscription.IsPinned,
            SelectedChannel?.ChannelId == subscription.ChannelId);
    }

    private void SynchronizeSelection(ConversationKey? selected)
    {
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
        switch (selected)
        {
            case ChannelTopic channelTopic:
                {
                    var channelName = state.Subscriptions.GetValueOrDefault(channelTopic.ChannelId)?.Name ?? $"频道 {channelTopic.ChannelId}";
                    ConversationTitle = string.IsNullOrEmpty(channelTopic.Topic) ? "（无主题）" : channelTopic.Topic;
                    ConversationSubtitle = $"# {channelName}";
                    DetailsTitle = $"# {channelName}";
                    DetailsBody = $"当前话题：{ConversationTitle}";
                    DetailsUnavailableMessage = "成员数、频道成员关系与 presence 尚不可用；退出频道已接入真实 Realm。";
                    break;
                }
            case DirectMessage directMessage:
                ConversationTitle = DescribeDirectMessage(directMessage, state.Users, _session.CurrentUserId);
                ConversationSubtitle = DescribeDirectMessageKind(directMessage);
                DetailsTitle = ConversationTitle;
                DetailsBody = directMessage.OtherUserIds.Count == 0
                    ? "这是给自己的私信。"
                    : $"可靠参与者：{DescribeDirectMessage(directMessage, state.Users, _session.CurrentUserId)}";
                DetailsUnavailableMessage = "presence、共同频道、静音与成员管理尚不可用。";
                break;
            default:
                ConversationTitle = "选择会话";
                ConversationSubtitle = "从左侧选择会话开始";
                DetailsTitle = "会话详情";
                DetailsBody = "选择会话后显示可靠的会话信息。";
                DetailsUnavailableMessage = "成员关系、presence、共同频道与频道管理暂不可用。";
                IsDetailsOpen = false;
                break;
        }
    }

    private void ProjectDraft(ConversationKey? selected)
    {
        var key = selected?.CanonicalKey;
        if (string.Equals(_activeDraftKey, key, StringComparison.Ordinal)) return;
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
    }

    private void RequestAutoMarkDisplayedRead(ClientState state)
    {
        var selected = _session.SelectedConversation;
        var history = _session.HistoryState;
        if (_disposed || !_isWindowActive || IsNativePreview || selected is null ||
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
            .Where(message => !message.IsRead && message.SenderId != _session.CurrentUserId)
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
            if (allowRetry) RemoveCurrentAutoMarkAttemptForRetry();
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
        OnPropertyChanged(nameof(ComposerPlaceholder));
        OnPropertyChanged(nameof(HasMessages));
        OnPropertyChanged(nameof(IsMessageListEmpty));
        OnPropertyChanged(nameof(IsConversationContentVisible));
        OnPropertyChanged(nameof(IsMessageCollectionVisible));
        OnPropertyChanged(nameof(ShowMessageEmptyState));
        OnPropertyChanged(nameof(ShowConversationLoadingIndicator));
        OnPropertyChanged(nameof(ShowEmptyChannelTopicState));
        OnPropertyChanged(nameof(HasKnownContacts));
        OnPropertyChanged(nameof(CanCompose));
        OnPropertyChanged(nameof(CanSend));
        OnPropertyChanged(nameof(CanMarkRead));
        OnPropertyChanged(nameof(CanUnsubscribeSelectedChannel));
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
        OnPropertyChanged(nameof(ChannelListHeight));
        OnPropertyChanged(nameof(TopicListHeight));
        OnPropertyChanged(nameof(HasCurrentConversationUnread));
        NotifyLayoutProperties();
    }

    private void ProjectHistoryState(ConversationKey? selected)
    {
        var history = _session.HistoryState;
        var matchesSelected = selected is not null &&
            string.Equals(history.Conversation?.CanonicalKey, selected.CanonicalKey, StringComparison.Ordinal);
        IsLoadingOlder = matchesSelected && history.IsLoading;
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

    private void CancelActivationScrollForUserInteraction(ConversationKey conversation)
    {
        if (string.Equals(_pendingActivationScrollConversationKey, conversation.CanonicalKey, StringComparison.Ordinal))
        {
            _pendingActivationScrollConversationKey = null;
            _pendingActivationScrollReason = null;
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
        if (targetMessageId > 0 &&
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
        if (history.IsLoading ||
            targetMessageId <= 0 &&
            string.Equals(
                _deferredInitialMessageProjectionConversationKey,
                conversationKey,
                StringComparison.Ordinal))
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
        _isMessageViewportNearBottom = true;
        if (request.Reason == MessageScrollReason.ManualJumpToLatest)
        {
            NewMessageCount = 0;
        }
        RequestAutoMarkDisplayedRead(_projectedState);
    }

    internal string? CurrentConversationKey => _session.SelectedConversation?.CanonicalKey;

    internal long CurrentHistoryGeneration => _session.HistoryState.Generation;

    private void NotifyConversationAvailability()
    {
        OnPropertyChanged(nameof(IsConversationContentVisible));
        OnPropertyChanged(nameof(IsMessageCollectionVisible));
        OnPropertyChanged(nameof(ShowMessageEmptyState));
        OnPropertyChanged(nameof(ShowConversationLoadingIndicator));
        OnPropertyChanged(nameof(ShowEmptyChannelTopicState));
        OnPropertyChanged(nameof(CanCompose));
        OnPropertyChanged(nameof(CanSend));
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
        OnPropertyChanged(nameof(IsChatPaneVisible));
        OnPropertyChanged(nameof(IsInlineDetailsVisible));
        OnPropertyChanged(nameof(IsOverlayDetailsVisible));
        OnPropertyChanged(nameof(IsPrimaryShellEnabled));
        OnPropertyChanged(nameof(ConversationPaneWidth));
        OnPropertyChanged(nameof(NavigationRailWidth));
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
            OpenDetailsByDefault = preferences.OpenDetailsByDefault;
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
            OpenDetailsByDefault,
            AreChannelsExpanded,
            AreDirectMessagesExpanded,
            FontScaleSliderValue,
            ConversationWidthSliderValue));
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

    private void ResetDrafts()
    {
        _drafts.Clear();
        _attachmentDrafts.Clear();
        _draftVersions.Clear();
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
        _draftVersions[_activeDraftKey] = _draftVersions.GetValueOrDefault(_activeDraftKey) + 1;
    }

    private void NotifyAttachmentProperties()
    {
        OnPropertyChanged(nameof(HasAttachments));
        OnPropertyChanged(nameof(CanSend));
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

    private static string BuildUploadedAttachmentMarkdown(UploadedAttachment uploaded)
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
        return $"[{label}]({uploaded.Url})";
    }

    private static string DescribeGatewayFailure(GatewayException exception) => exception.Kind switch
    {
        GatewayErrorKind.IncompatibleRealm => "此 Realm 与 RelayCove 不兼容。",
        GatewayErrorKind.AuthenticationFailed or GatewayErrorKind.ReauthRequired => "邮箱、密码或 API 凭据无效。",
        GatewayErrorKind.RateLimited => "服务器正在限流，请稍后再试。",
        GatewayErrorKind.Offline => "无法连接到服务器，请检查网络和 Realm 地址。",
        _ => "服务器请求失败，请稍后再试。"
    };

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
        RelayCove.Core.ConnectionStatus.Offline => "离线缓存",
        RelayCove.Core.ConnectionStatus.Connecting => "正在连接",
        RelayCove.Core.ConnectionStatus.Connected => "已连接",
        RelayCove.Core.ConnectionStatus.Reconnecting => "正在重连",
        RelayCove.Core.ConnectionStatus.RateLimited => "服务器限流中",
        RelayCove.Core.ConnectionStatus.ReauthRequired => "需要重新认证",
        _ => "连接故障"
    };

    private static string DescribeDirectMessage(
        DirectMessage message,
        IReadOnlyDictionary<long, UserProfile> users,
        long? currentUserId) =>
        message.OtherUserIds.Count == 0
            ? currentUserId is { } current && users.GetValueOrDefault(current) is { } currentUser
                ? $"{currentUser.FullName}（自己）"
                : "给自己"
            : string.Join(", ", message.OtherUserIds.Select(id => users.GetValueOrDefault(id)?.FullName ?? $"用户 {id}"));

    private static string DescribeDirectMessageKind(DirectMessage message) => message.OtherUserIds.Count switch
    {
        0 => "self-DM",
        1 => "私信",
        _ => "群组私信"
    };

    private static UserProfile? GetDirectMessageAvatar(
        DirectMessage message,
        IReadOnlyDictionary<long, UserProfile> users,
        long? currentUserId) => message.OtherUserIds.Count switch
        {
            0 when currentUserId is { } current => users.GetValueOrDefault(current),
            1 => users.GetValueOrDefault(message.OtherUserIds[0]),
            _ => null
        };

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
            : TruncateForSearch(latestMessage.Content);
        var timestamp = latestMessage is null
            ? existing?.Timestamp
            : FormatConversationTimestamp(latestMessage.Timestamp.LocalDateTime);

        return new NavigationItem(
            conversation,
            DescribeDirectMessage(conversation, state.Users, _session.CurrentUserId),
            detail,
            GetConversationUnread(state.Unread, conversation),
            avatar?.AvatarUrl,
            avatar?.IsBot ?? false,
            timestamp,
            string.Equals(
                SelectedDirectMessage?.Conversation.CanonicalKey,
                conversation.CanonicalKey,
                StringComparison.Ordinal));
    }

    private void RefreshNavigationSelectionProjection()
    {
        for (var index = 0; index < Channels.Count; index++)
        {
            var item = Channels[index];
            var isSelected = SelectedChannel?.ChannelId == item.ChannelId;
            if (item.IsSelected != isSelected) Channels[index] = item with { IsSelected = isSelected };
        }

        for (var index = 0; index < Topics.Count; index++)
        {
            var item = Topics[index];
            var isSelected = string.Equals(SelectedTopic?.CanonicalKey, item.CanonicalKey, StringComparison.Ordinal);
            if (item.IsSelected != isSelected) Topics[index] = item with { IsSelected = isSelected };
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

    private static string DescribeOutbox(OutboxState state) => state switch
    {
        OutboxState.Waiting => "正在等待服务器事件",
        OutboxState.WaitExpired => "结果不确定；手动重试可能产生重复消息",
        OutboxState.Failed => "发送失败；恢复内容后手动重试可能产生重复消息",
        _ => string.Empty
    };

    private void CancelNavigation()
    {
        var cancellation = Interlocked.Exchange(ref _navigationCancellation, null);
        _navigationConversationKey = null;
        if (cancellation is null) return;
        cancellation.Cancel();
        cancellation.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_projectionGate)
        {
            _pendingProjectionState = null;
            _projectionDispatchScheduled = false;
        }
        CancelAutoMarkReadOperation(allowRetry: false);
        _lifetimeCancellation.Cancel();
        CancelNavigation();
        CancelSearchInput();
        ClearNewConversationChoices();
        _session.StateChanged -= OnStateChanged;
        if (_session is IMessageMutationObserver observer) observer.MessageMutationObserved -= OnMessageMutationObserved;
        _lifetimeCancellation.Dispose();
    }
}
