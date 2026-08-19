namespace RelayCove.Core;

public sealed record AnonymousChannelGroupSetting(
    IReadOnlyList<long> DirectMembers,
    IReadOnlyList<long> DirectSubgroups) : ChannelGroupSetting;
