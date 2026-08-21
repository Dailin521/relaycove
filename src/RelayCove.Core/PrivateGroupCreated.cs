namespace RelayCove.Core;

public sealed record PrivateGroupCreated(
    long ChannelId,
    string Name,
    ChannelTopic Conversation,
    int MemberCount);
