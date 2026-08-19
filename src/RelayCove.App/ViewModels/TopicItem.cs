using CommunityToolkit.Mvvm.ComponentModel;
using RelayCove.Core;

namespace RelayCove.App.ViewModels;

/// <summary>Stable navigation-row state for one channel topic.</summary>
public sealed class TopicItem : ObservableObject
{
    private long _channelId;
    private string _topic;
    private long? _maxMessageId;
    private int _unreadCount;
    private bool _isSelected;
    private bool _isPointerOver;
    private bool _isActionMenuOpen;
    private TopicVisibilityPolicy _visibilityPolicy;
    private bool _isResolved;

    public TopicItem(long channelId, string topic, long? maxMessageId, int unreadCount = 0, bool isSelected = false, TopicVisibilityPolicy visibilityPolicy = TopicVisibilityPolicy.None, bool isResolved = false)
    {
        _channelId = channelId;
        _topic = topic;
        _maxMessageId = maxMessageId;
        _unreadCount = unreadCount;
        _isSelected = isSelected;
        _visibilityPolicy = visibilityPolicy;
        _isResolved = isResolved;
    }

    public long ChannelId => _channelId;
    public string Topic => _topic;
    public long? MaxMessageId => _maxMessageId;
    public int UnreadCount => _unreadCount;
    public bool IsSelected { get => _isSelected; internal set { if (SetProperty(ref _isSelected, value)) OnPropertyChanged(nameof(ShowActions)); } }
    public bool IsPointerOver { get => _isPointerOver; set { if (SetProperty(ref _isPointerOver, value)) OnPropertyChanged(nameof(ShowActions)); } }
    public bool IsActionMenuOpen { get => _isActionMenuOpen; internal set { if (SetProperty(ref _isActionMenuOpen, value)) OnPropertyChanged(nameof(ShowActions)); } }
    public TopicVisibilityPolicy VisibilityPolicy
    {
        get => _visibilityPolicy;
        internal set
        {
            if (!SetProperty(ref _visibilityPolicy, value)) return;
            OnPropertyChanged(nameof(VisibilityLabel));
            OnPropertyChanged(nameof(VisibilityGlyph));
        }
    }
    public bool IsResolved { get => _isResolved; internal set { if (SetProperty(ref _isResolved, value)) OnPropertyChanged(nameof(ResolutionLabel)); } }
    public string CanonicalKey => new RelayCove.Core.ChannelTopic(ChannelId, Topic).CanonicalKey;
    public string DisplayName => string.IsNullOrEmpty(Topic) ? "（无主题）" : Topic;
    public bool HasUnread => UnreadCount > 0;
    public string UnreadLabel => UnreadCount > 99 ? "99+" : UnreadCount.ToString();
    public bool ShowActions => IsSelected || IsPointerOver || IsActionMenuOpen;
    public string VisibilityLabel => VisibilityPolicy switch { TopicVisibilityPolicy.Muted => "静音", TopicVisibilityPolicy.Unmuted => "取消静音", TopicVisibilityPolicy.Followed => "关注", _ => "继承频道设置" };
    public string VisibilityGlyph => VisibilityPolicy switch { TopicVisibilityPolicy.Muted => "🔕", TopicVisibilityPolicy.Unmuted => "🔔", TopicVisibilityPolicy.Followed => "★", _ => "○" };
    public string ResolutionLabel => IsResolved ? "取消解决" : "解决话题";

    internal void ApplyFrom(TopicItem candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (!string.Equals(CanonicalKey, candidate.CanonicalKey, StringComparison.Ordinal)) throw new InvalidOperationException("Topics with different keys cannot be merged.");
        _channelId = candidate.ChannelId;
        if (SetProperty(ref _topic, candidate.Topic, nameof(Topic))) { OnPropertyChanged(nameof(CanonicalKey)); OnPropertyChanged(nameof(DisplayName)); }
        SetProperty(ref _maxMessageId, candidate.MaxMessageId, nameof(MaxMessageId));
        if (SetProperty(ref _unreadCount, candidate.UnreadCount, nameof(UnreadCount))) { OnPropertyChanged(nameof(HasUnread)); OnPropertyChanged(nameof(UnreadLabel)); }
        IsSelected = candidate.IsSelected;
        VisibilityPolicy = candidate.VisibilityPolicy;
        IsResolved = candidate.IsResolved;
    }
}
