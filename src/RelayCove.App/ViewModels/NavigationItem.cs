using RelayCove.Core;

namespace RelayCove.App.ViewModels;

public sealed record NavigationItem(
    ConversationKey Conversation,
    string Title,
    string? Detail = null,
    int UnreadCount = 0,
    string? AvatarUrl = null,
    bool IsBot = false,
    string? Timestamp = null)
{
    public bool HasUnread => UnreadCount > 0;
    public string UnreadLabel => UnreadCount > 99 ? "99+" : UnreadCount.ToString();
    public bool HasAvatar => !string.IsNullOrWhiteSpace(AvatarUrl);
    public bool ShowFallback => !HasAvatar;
    public bool HasTimestamp => !string.IsNullOrWhiteSpace(Timestamp);
    public Brush ToneBrush => new SolidColorBrush(
        Color.FromArgb(TonePalette[StableToneIndex(Conversation.CanonicalKey)]));
    public string Initial => IsBot
        ? "BOT"
        : string.IsNullOrWhiteSpace(Title)
            ? "?"
            : Title.Trim()[0].ToString().ToUpperInvariant();

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
