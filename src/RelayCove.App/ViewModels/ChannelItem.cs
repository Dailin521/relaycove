using CommunityToolkit.Mvvm.ComponentModel;

namespace RelayCove.App.ViewModels;

public sealed class ChannelItem : ObservableObject
{
    private long _channelId;
    private string _name;
    private int _unreadCount;
    private string? _recentTopic;
    private string? _recentPreview;
    private string? _timestamp;
    private bool _isMuted;
    private bool _isPinned;
    private string? _color;
    private bool _isSelected;
    private bool _isExpanded;
    private int _expandedTopicCount;
    private bool _isPointerOver;
    private bool _isActionMenuOpen;
    private IEnumerable<TopicItem>? _treeTopics;

    public ChannelItem(
        long channelId,
        string name,
        int unreadCount = 0,
        string? recentTopic = null,
        string? recentPreview = null,
        string? timestamp = null,
        bool isMuted = false,
        bool isPinned = false,
        bool isSelected = false,
        bool isExpanded = false,
        string? color = null)
    {
        _channelId = channelId;
        _name = name;
        _unreadCount = unreadCount;
        _recentTopic = recentTopic;
        _recentPreview = recentPreview;
        _timestamp = timestamp;
        _isMuted = isMuted;
        _isPinned = isPinned;
        _isSelected = isSelected;
        _isExpanded = isExpanded;
        _color = color;
    }

    public long ChannelId => _channelId;
    public string Name => _name;
    public int UnreadCount => _unreadCount;
    public string? RecentTopic => _recentTopic;
    public string? RecentPreview => _recentPreview;
    public string? Timestamp => _timestamp;
    public bool IsMuted => _isMuted;
    public bool IsPinned => _isPinned;
    public bool IsSelected
    {
        get => _isSelected;
        internal set
        {
            if (SetProperty(ref _isSelected, value)) OnPropertyChanged(nameof(ShowActions));
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        internal set
        {
            if (SetProperty(ref _isExpanded, value))
            {
                OnPropertyChanged(nameof(TreeRowHeight));
            }
        }
    }

    public int ExpandedTopicCount
    {
        get => _expandedTopicCount;
        internal set
        {
            var normalized = Math.Max(0, value);
            if (SetProperty(ref _expandedTopicCount, normalized))
            {
                OnPropertyChanged(nameof(TreeRowHeight));
            }
        }
    }

    public bool IsPointerOver
    {
        get => _isPointerOver;
        set
        {
            if (SetProperty(ref _isPointerOver, value)) OnPropertyChanged(nameof(ShowActions));
        }
    }

    public bool IsActionMenuOpen
    {
        get => _isActionMenuOpen;
        internal set
        {
            if (SetProperty(ref _isActionMenuOpen, value)) OnPropertyChanged(nameof(ShowActions));
        }
    }

    public IEnumerable<TopicItem>? TreeTopics
    {
        get => _treeTopics;
        internal set => SetProperty(ref _treeTopics, value);
    }

    public bool HasUnread => UnreadCount > 0;
    public string UnreadLabel => UnreadCount > 99 ? "99+" : UnreadCount.ToString();
    public string DisplayTitle => string.IsNullOrWhiteSpace(RecentTopic) ? Name : RecentTopic;
    public string Detail => string.IsNullOrWhiteSpace(RecentPreview)
        ? $"# {Name}"
        : $"# {Name} · {RecentPreview}";
    public bool HasTimestamp => !string.IsNullOrWhiteSpace(Timestamp);
    public double ItemOpacity => IsMuted ? 0.62d : 1d;
    public bool ShowActions => IsSelected || IsPointerOver || IsActionMenuOpen;
    public double TreeRowHeight => 38d + (IsExpanded ? ExpandedTopicCount * 34d : 0d);
    public Color ToneColor => TryGetColor(_color) ?? Color.FromArgb(TonePalette[(int)(Math.Abs(ChannelId % TonePalette.Length))]);

    internal void ApplyFrom(ChannelItem candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (ChannelId != candidate.ChannelId) throw new InvalidOperationException("Channel items with different IDs cannot be merged.");

        _channelId = candidate.ChannelId;
        if (SetProperty(ref _name, candidate.Name, nameof(Name)))
        {
            OnPropertyChanged(nameof(DisplayTitle));
            OnPropertyChanged(nameof(Detail));
        }
        if (SetProperty(ref _unreadCount, candidate.UnreadCount, nameof(UnreadCount)))
        {
            OnPropertyChanged(nameof(HasUnread));
            OnPropertyChanged(nameof(UnreadLabel));
        }
        if (SetProperty(ref _recentTopic, candidate.RecentTopic, nameof(RecentTopic))) OnPropertyChanged(nameof(DisplayTitle));
        if (SetProperty(ref _recentPreview, candidate.RecentPreview, nameof(RecentPreview))) OnPropertyChanged(nameof(Detail));
        if (SetProperty(ref _timestamp, candidate.Timestamp, nameof(Timestamp))) OnPropertyChanged(nameof(HasTimestamp));
        if (SetProperty(ref _isMuted, candidate.IsMuted, nameof(IsMuted))) OnPropertyChanged(nameof(ItemOpacity));
        SetProperty(ref _isPinned, candidate.IsPinned, nameof(IsPinned));
        if (SetProperty(ref _color, candidate._color, nameof(ToneColor))) OnPropertyChanged(nameof(ToneColor));
        IsSelected = candidate.IsSelected;
        IsExpanded = candidate.IsExpanded;
    }

    private static Color? TryGetColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            return Color.FromArgb(value);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static readonly string[] TonePalette =
    [
        "#2F9BFF", "#8A63D2", "#2B9A78", "#E28A39", "#D65B78", "#367FC4"
    ];
}
