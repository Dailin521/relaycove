using RelayCove.Core;

namespace RelayCove.App.ViewModels;

public sealed class ChannelPermissionItem(ChannelGroupSettingName name, ChannelGroupSetting? value, string label)
{
    public ChannelGroupSettingName Name { get; } = name;
    public ChannelGroupSetting? Value { get; } = value;
    public string Label { get; } = label;
    public bool IsAnonymous => Value is AnonymousChannelGroupSetting;
    public string ValueLabel => Value switch
    {
        NamedChannelGroupSetting named => $"用户组 {named.GroupId}",
        AnonymousChannelGroupSetting => "自定义权限组（仅只读）",
        _ => "未设置"
    };
}
