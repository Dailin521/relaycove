namespace RelayCove.Core;

public sealed record ChannelAdvancedSettingsChange(
    bool? IsArchived = null,
    bool? IsPrivate = null,
    bool? IsWebPublic = null,
    bool? HistoryPublicToSubscribers = null,
    bool? IsDefaultStream = null,
    ChannelTopicsPolicy? TopicsPolicy = null,
    ChannelRetentionPolicy? RetentionPolicy = null,
    ChannelGroupSettingName? GroupSetting = null,
    ChannelGroupSetting? NewGroup = null,
    ChannelGroupSetting? OldGroup = null,
    IReadOnlyList<ChannelGroupSettingUpdate>? GroupSettings = null)
{
    public override string ToString() => "ChannelAdvancedSettingsChange { Values = [redacted] }";
}
