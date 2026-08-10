namespace RelayCove.Core;

public sealed record TopicSummary
{
    public TopicSummary(long channelId, string topic, long? maxMessageId = null)
    {
        if (channelId <= 0) throw new ArgumentOutOfRangeException(nameof(channelId));
        ArgumentNullException.ThrowIfNull(topic);
        ChannelId = channelId;
        Topic = topic;
        MaxMessageId = maxMessageId;
    }

    public long ChannelId { get; init; }
    public string Topic { get; init; }
    public long? MaxMessageId { get; init; }
}
