namespace RelayCove.Core;

public sealed record Subscription
{
    public Subscription(long channelId, string name, bool isActive = true)
    {
        if (channelId <= 0) throw new ArgumentOutOfRangeException(nameof(channelId));
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ChannelId = channelId;
        Name = name;
        IsActive = isActive;
    }

    public long ChannelId { get; init; }
    public string Name { get; init; }
    public bool IsActive { get; init; }
}
