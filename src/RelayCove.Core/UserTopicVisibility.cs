namespace RelayCove.Core;

public sealed record UserTopicVisibility(long ChannelId, string Topic, TopicVisibilityPolicy Policy);
