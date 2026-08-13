namespace RelayCove.App.ViewModels;

public sealed record TopicItem(
    long ChannelId,
    string Topic,
    long? MaxMessageId,
    int UnreadCount = 0)
{
    public string CanonicalKey => new RelayCove.Core.ChannelTopic(ChannelId, Topic).CanonicalKey;
    public string DisplayName => string.IsNullOrEmpty(Topic) ? "（无主题）" : Topic;
    public bool HasUnread => UnreadCount > 0;
    public string UnreadLabel => UnreadCount > 99 ? "99+" : UnreadCount.ToString();
}
