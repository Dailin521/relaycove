namespace RelayCove.App.ViewModels;

public sealed record AvailableChannelItem(
    long ChannelId,
    string Name,
    string? Description,
    int? SubscriberCount)
{
    public string DescriptionLabel => string.IsNullOrWhiteSpace(Description) ? "暂无频道说明" : Description;
    public string SubscriberCountLabel => SubscriberCount is { } count ? $"{count} 位订阅者" : "订阅人数不可用";
}
