namespace RelayCove.Core;

public sealed record TopicSummary
{
    public TopicSummary(long channelId, string topic, long? maxMessageId = null, TopicVisibilityPolicy visibilityPolicy = TopicVisibilityPolicy.None)
    {
        if (channelId <= 0) throw new ArgumentOutOfRangeException(nameof(channelId));
        ArgumentNullException.ThrowIfNull(topic);
        ChannelId = channelId;
        Topic = topic;
        MaxMessageId = maxMessageId;
        VisibilityPolicy = visibilityPolicy;
    }

    public long ChannelId { get; init; }
    public string Topic { get; init; }
    public long? MaxMessageId { get; init; }
    public TopicVisibilityPolicy VisibilityPolicy { get; init; }
}
