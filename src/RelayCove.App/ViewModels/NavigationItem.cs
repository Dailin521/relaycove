using CommunityToolkit.Mvvm.ComponentModel;
using RelayCove.Core;

namespace RelayCove.App.ViewModels;

public sealed class NavigationItem : ObservableObject
{
    private ConversationKey _conversation;
    private string _title;
    private string? _detail;
    private int _unreadCount;
    private string? _avatarUrl;
    private bool _isBot;
    private string? _timestamp;
    private bool _isSelected;

    public NavigationItem(
        ConversationKey conversation,
        string title,
        string? detail = null,
        int unreadCount = 0,
        string? avatarUrl = null,
        bool isBot = false,
        string? timestamp = null,
        bool isSelected = false)
    {
        _conversation = conversation;
        _title = title;
        _detail = detail;
        _unreadCount = unreadCount;
        _avatarUrl = avatarUrl;
        _isBot = isBot;
        _timestamp = timestamp;
        _isSelected = isSelected;
    }

    public ConversationKey Conversation => _conversation;
    public string Title => _title;
    public string? Detail => _detail;
    public int UnreadCount => _unreadCount;
    public string? AvatarUrl => _avatarUrl;
    public bool IsBot => _isBot;
    public string? Timestamp => _timestamp;

    public bool IsSelected
    {
        get => _isSelected;
        internal set => SetProperty(ref _isSelected, value);
    }

    public bool HasUnread => UnreadCount > 0;
    public string UnreadLabel => UnreadCount > 99 ? "99+" : UnreadCount.ToString();
    public bool HasAvatar => !string.IsNullOrWhiteSpace(AvatarUrl);
    public bool ShowFallback => !HasAvatar;
    public bool HasTimestamp => !string.IsNullOrWhiteSpace(Timestamp);
    public Brush ToneBrush => new SolidColorBrush(
        Color.FromArgb(TonePalette[StableToneIndex(Conversation.CanonicalKey)]));
    public string Initial => AvatarInitials.Create(Title, IsBot);

    internal void ApplyFrom(NavigationItem candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (!string.Equals(
                Conversation.CanonicalKey,
                candidate.Conversation.CanonicalKey,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Navigation items with different keys cannot be merged.");
        }

        _conversation = candidate.Conversation;
        if (SetProperty(ref _title, candidate.Title, nameof(Title)))
        {
            OnPropertyChanged(nameof(Initial));
        }
        SetProperty(ref _detail, candidate.Detail, nameof(Detail));
        if (SetProperty(ref _unreadCount, candidate.UnreadCount, nameof(UnreadCount)))
        {
            OnPropertyChanged(nameof(HasUnread));
            OnPropertyChanged(nameof(UnreadLabel));
        }
        if (SetProperty(ref _avatarUrl, candidate.AvatarUrl, nameof(AvatarUrl)))
        {
            OnPropertyChanged(nameof(HasAvatar));
            OnPropertyChanged(nameof(ShowFallback));
        }
        if (SetProperty(ref _isBot, candidate.IsBot, nameof(IsBot)))
        {
            OnPropertyChanged(nameof(Initial));
        }
        if (SetProperty(ref _timestamp, candidate.Timestamp, nameof(Timestamp)))
        {
            OnPropertyChanged(nameof(HasTimestamp));
        }
        IsSelected = candidate.IsSelected;
    }

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
