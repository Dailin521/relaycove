using CommunityToolkit.Mvvm.ComponentModel;
using RelayCove.Core;

namespace RelayCove.App.ViewModels;

public sealed class ConversationListItem : ObservableObject
{
    private ConversationKey _conversation;
    private string _title;
    private string? _detail;
    private int _unreadCount;
    private string? _avatarUrl;
    private bool _isBot;
    private string? _timestamp;
    private DateTimeOffset? _latestMessageTimestamp;
    private bool _isSelected;
    private bool _isMuted;
    private bool _isPinned;
    private long? _searchTargetMessageId;
    private IReadOnlyList<ConversationAvatarTile> _avatarTiles;
    private UserPresenceStatus? _presenceStatus;
    private UserStatusContent? _userStatus;

    public ConversationListItem(
        ConversationKey conversation,
        string title,
        string? detail = null,
        int unreadCount = 0,
        string? avatarUrl = null,
        bool isBot = false,
        string? timestamp = null,
        DateTimeOffset? latestMessageTimestamp = null,
        bool isSelected = false,
        bool isMuted = false,
        bool isPinned = false,
        IReadOnlyList<ConversationAvatarTile>? avatarTiles = null,
        long? searchTargetMessageId = null,
        UserPresenceStatus? presenceStatus = null,
        UserStatusContent? userStatus = null)
    {
        _conversation = conversation ?? throw new ArgumentNullException(nameof(conversation));
        _title = title;
        _detail = detail;
        _unreadCount = unreadCount;
        _avatarUrl = avatarUrl;
        _isBot = isBot;
        _timestamp = timestamp;
        _latestMessageTimestamp = latestMessageTimestamp;
        _isSelected = isSelected;
        _isMuted = isMuted;
        _isPinned = isPinned;
        _avatarTiles = avatarTiles ?? [];
        _searchTargetMessageId = searchTargetMessageId;
        _presenceStatus = presenceStatus;
        _userStatus = userStatus;
    }

    public ConversationKey Conversation => _conversation;
    public string Title => _title;
    public string? Detail => _detail;
    public int UnreadCount => _unreadCount;
    public string? AvatarUrl => _avatarUrl;
    public bool IsBot => _isBot;
    public string? Timestamp => _timestamp;
    public DateTimeOffset? LatestMessageTimestamp => _latestMessageTimestamp;
    public bool IsMuted => _isMuted;
    public bool IsPinned => _isPinned;
    public long? SearchTargetMessageId => _searchTargetMessageId;
    public string ProjectionKey => SearchTargetMessageId is { } messageId
        ? $"{Conversation.CanonicalKey}|message:{messageId}"
        : Conversation.CanonicalKey;
    public bool IsPrivateGroup => Conversation is ChannelTopic;
    public IReadOnlyList<ConversationAvatarTile> AvatarTiles => _avatarTiles;
    public bool HasAvatarTiles => IsPrivateGroup && AvatarTiles.Count > 0;
    public bool ShowSingleAvatar => !IsPrivateGroup;
    public bool ShowGroupFallback => IsPrivateGroup && !HasAvatarTiles;
    public UserPresenceStatus? PresenceStatus => _presenceStatus;
    public bool HasPresence => PresenceStatus is not null;
    public string PresenceLabel => PresenceStatus switch
    {
        UserPresenceStatus.Active => "在线",
        UserPresenceStatus.Idle => "忙碌",
        UserPresenceStatus.Offline => "离线",
        _ => string.Empty
    };
    public Brush PresenceBrush => new SolidColorBrush(Color.FromArgb(PresenceStatus switch
    {
        UserPresenceStatus.Active => "#22C55E",
        UserPresenceStatus.Idle => "#F59E0B",
        _ => "#9CA3AF"
    }));
    public UserStatusContent? UserStatus => _userStatus;
    public string UserStatusGlyph => UserStatus?.Emoji is { ReactionType: "unicode_emoji" } emoji
        ? EmojiCatalog.GetDisplayValue(emoji.EmojiCode)
        : UserStatus?.Emoji is { } fallback ? $":{fallback.EmojiName}:" : string.Empty;
    public bool HasUserStatusGlyph => UserStatusGlyph.Length > 0;
    public string UserStatusDescription => UserStatus is null
        ? string.Empty
        : UserStatus.StatusText.Length > 0 ? UserStatus.StatusText : UserStatusGlyph;

    public bool IsSelected
    {
        get => _isSelected;
        internal set => SetProperty(ref _isSelected, value);
    }

    public bool HasUnread => UnreadCount > 0;
    public string UnreadLabel => UnreadCount > 99 ? "99+" : UnreadCount.ToString();
    public bool HasTimestamp => !string.IsNullOrWhiteSpace(Timestamp);
    public double ItemOpacity => IsMuted ? 0.62d : 1d;
    public Brush ToneBrush => new SolidColorBrush(Color.FromArgb(TonePalette[StableToneIndex(Conversation.CanonicalKey)]));
    public string Initial => AvatarInitials.Create(Title, IsBot);

    internal void ApplyFrom(ConversationListItem candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (!string.Equals(ProjectionKey, candidate.ProjectionKey, StringComparison.Ordinal))
            throw new InvalidOperationException("Conversation search items with different keys cannot be merged.");

        _conversation = candidate.Conversation;
        if (SetProperty(ref _title, candidate.Title, nameof(Title))) OnPropertyChanged(nameof(Initial));
        SetProperty(ref _detail, candidate.Detail, nameof(Detail));
        if (SetProperty(ref _unreadCount, candidate.UnreadCount, nameof(UnreadCount)))
        {
            OnPropertyChanged(nameof(HasUnread));
            OnPropertyChanged(nameof(UnreadLabel));
        }
        SetProperty(ref _avatarUrl, candidate.AvatarUrl, nameof(AvatarUrl));
        if (SetProperty(ref _isBot, candidate.IsBot, nameof(IsBot))) OnPropertyChanged(nameof(Initial));
        if (SetProperty(ref _timestamp, candidate.Timestamp, nameof(Timestamp))) OnPropertyChanged(nameof(HasTimestamp));
        SetProperty(ref _latestMessageTimestamp, candidate.LatestMessageTimestamp, nameof(LatestMessageTimestamp));
        if (SetProperty(ref _isMuted, candidate.IsMuted, nameof(IsMuted))) OnPropertyChanged(nameof(ItemOpacity));
        SetProperty(ref _isPinned, candidate.IsPinned, nameof(IsPinned));
        if (SetProperty(ref _searchTargetMessageId, candidate.SearchTargetMessageId, nameof(SearchTargetMessageId)))
        {
            OnPropertyChanged(nameof(ProjectionKey));
        }
        if (SetProperty(ref _avatarTiles, candidate.AvatarTiles, nameof(AvatarTiles)))
        {
            OnPropertyChanged(nameof(HasAvatarTiles));
            OnPropertyChanged(nameof(ShowGroupFallback));
        }
        if (SetProperty(ref _presenceStatus, candidate.PresenceStatus, nameof(PresenceStatus)))
        {
            OnPropertyChanged(nameof(HasPresence));
            OnPropertyChanged(nameof(PresenceLabel));
            OnPropertyChanged(nameof(PresenceBrush));
        }
        if (SetProperty(ref _userStatus, candidate.UserStatus, nameof(UserStatus)))
        {
            OnPropertyChanged(nameof(UserStatusGlyph));
            OnPropertyChanged(nameof(HasUserStatusGlyph));
            OnPropertyChanged(nameof(UserStatusDescription));
        }
        IsSelected = candidate.IsSelected;
    }

    internal ConversationListItem WithSearchMatch(long messageId, string detail, DateTimeOffset timestamp) =>
        new(
            Conversation,
            Title,
            detail,
            UnreadCount,
            AvatarUrl,
            IsBot,
            ShellViewModel.FormatConversationTimestamp(timestamp.LocalDateTime),
            timestamp,
            IsSelected,
            IsMuted,
            IsPinned,
            AvatarTiles,
            messageId,
            PresenceStatus,
            UserStatus);

    private static readonly string[] TonePalette =
    [
        "#2F9BFF", "#8A63D2", "#2B9A78", "#E28A39", "#D65B78", "#367FC4"
    ];

    private static int StableToneIndex(string value)
    {
        var hash = 17;
        foreach (var character in value) hash = unchecked((hash * 31) + character);
        return (hash & int.MaxValue) % TonePalette.Length;
    }
}
