namespace RelayCove.App.ViewModels;

public sealed record ChannelItem(
    long ChannelId,
    string Name,
    int UnreadCount = 0)
{
    public bool HasUnread => UnreadCount > 0;
    public string UnreadLabel => UnreadCount > 99 ? "99+" : UnreadCount.ToString();
}
