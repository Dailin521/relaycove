using CommunityToolkit.Mvvm.ComponentModel;
using RelayCove.Core;

namespace RelayCove.App.ViewModels;

public sealed partial class ChannelSettingsChannelItem : ObservableObject
{
    public ChannelSettingsChannelItem(ChannelSummary channel)
    {
        ChannelId = channel.ChannelId;
        Name = channel.Name;
        Description = channel.Description;
        IsArchived = channel.IsArchived;
        IsPrivate = channel.IsPrivate;
        IsSubscribed = channel.IsSubscribed;
        SubscriberCount = channel.SubscriberCount;
        WeeklyTraffic = channel.WeeklyTraffic;
        Color = channel.Color;
    }

    public long ChannelId { get; }
    public string Name { get; }
    public string? Description { get; }
    public bool IsArchived { get; }
    public bool IsPrivate { get; }
    public bool IsSubscribed { get; }
    public int? SubscriberCount { get; }
    public int? WeeklyTraffic { get; }
    public string? Color { get; }
    [ObservableProperty] public partial bool IsSelected { get; set; }
    public string DescriptionLabel => string.IsNullOrWhiteSpace(Description) ? "暂无频道说明" : Description;
    public string SubscriberLabel => SubscriberCount is { } count ? $"{count} 位订阅者" : "订阅人数不可用";
    public string TrafficLabel => WeeklyTraffic is { } traffic ? $"每周约 {traffic} 条" : "活跃度不可用";
    public string PrivacyGlyph => IsPrivate ? "🔒" : "#";
    public string SubscriptionGlyph => IsSubscribed ? "✓" : "+";
}
