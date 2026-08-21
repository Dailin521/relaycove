namespace RelayCove.Core;

public sealed record ChannelGroupSettingUpdate(
    ChannelGroupSettingName Name,
    ChannelGroupSetting NewGroup,
    ChannelGroupSetting OldGroup);
