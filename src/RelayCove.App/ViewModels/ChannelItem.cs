namespace RelayCove.App.ViewModels;

public sealed record ChannelItem(
    long ChannelId,
    string Name,
    int UnreadCount = 0,
    string? RecentTopic = null,
    string? RecentPreview = null,
    string? Timestamp = null)
{
    public bool HasUnread => UnreadCount > 0;
    public string UnreadLabel => UnreadCount > 99 ? "99+" : UnreadCount.ToString();
    public string DisplayTitle => string.IsNullOrWhiteSpace(RecentTopic) ? Name : RecentTopic;
    public string Detail => string.IsNullOrWhiteSpace(RecentPreview)
        ? $"# {Name}"
        : $"# {Name} · {RecentPreview}";
    public bool HasTimestamp => !string.IsNullOrWhiteSpace(Timestamp);
    public Brush ToneBrush => new SolidColorBrush(
        Color.FromArgb(TonePalette[(int)(Math.Abs(ChannelId % TonePalette.Length))]));

    private static readonly string[] TonePalette =
    [
        "#2F9BFF", "#8A63D2", "#2B9A78", "#E28A39", "#D65B78", "#367FC4"
    ];
}
