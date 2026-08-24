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
        long? searchTargetMessageId = null)
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
            messageId);

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
