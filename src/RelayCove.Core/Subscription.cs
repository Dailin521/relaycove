namespace RelayCove.Core;

public sealed record Subscription
{
    public Subscription(
        long channelId,
        string name,
        bool isActive = true,
        bool isMuted = false,
        bool isPinned = false,
        string? color = null,
        bool? isPrivate = null,
        ChannelTopicsPolicy? topicsPolicy = null,
        bool? isWebPublic = null)
    {
        if (channelId <= 0) throw new ArgumentOutOfRangeException(nameof(channelId));
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ChannelId = channelId;
        Name = name;
        IsActive = isActive;
        IsMuted = isMuted;
        IsPinned = isPinned;
        Color = color;
        IsPrivate = isPrivate;
        TopicsPolicy = topicsPolicy;
        IsWebPublic = isWebPublic;
    }

    public long ChannelId { get; init; }
    public string Name { get; init; }
    public bool IsActive { get; init; }
    public bool IsMuted { get; init; }
    public bool IsPinned { get; init; }
    public string? Color { get; init; }
    public bool? IsPrivate { get; init; }
    public ChannelTopicsPolicy? TopicsPolicy { get; init; }
    public bool? IsWebPublic { get; init; }
}
