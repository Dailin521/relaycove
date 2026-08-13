using RelayCove.Core;

namespace RelayCove.App.ViewModels;

public sealed record NavigationItem(
    ConversationKey Conversation,
    string Title,
    string? Detail = null,
    int UnreadCount = 0,
    string? AvatarUrl = null,
    bool IsBot = false)
{
    public bool HasUnread => UnreadCount > 0;
    public string UnreadLabel => UnreadCount > 99 ? "99+" : UnreadCount.ToString();
    public bool HasAvatar => !string.IsNullOrWhiteSpace(AvatarUrl);
    public bool ShowFallback => !HasAvatar;
    public string Initial => IsBot
        ? "BOT"
        : string.IsNullOrWhiteSpace(Title)
            ? "?"
            : Title.Trim()[0].ToString().ToUpperInvariant();
}
