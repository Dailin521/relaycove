namespace RelayCove.Core;

public sealed record ChannelPersonalSettingChange(ChannelPersonalSetting Setting, string? ColorValue = null, bool? BooleanValue = null)
{
    public override string ToString() => "ChannelPersonalSettingChange { Value = [redacted] }";
}
