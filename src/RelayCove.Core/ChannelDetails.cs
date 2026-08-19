namespace RelayCove.Core;

public sealed record ChannelDetails(
    long ChannelId,
    string Name,
    string? Description,
    bool IsArchived,
    bool IsPrivate,
    bool IsWebPublic,
    int? SubscriberCount,
    int? WeeklyTraffic,
    long? FolderId,
    long? CreatorId,
    DateTimeOffset? DateCreated,
    ChannelGroupSetting? CanAdministerChannelGroup,
    ChannelGroupSetting? CanAddSubscribersGroup = null,
    ChannelGroupSetting? CanSendMessageGroup = null,
    ChannelGroupSetting? CanSubscribeGroup = null,
    ChannelGroupSetting? CanCreateTopicGroup = null);
