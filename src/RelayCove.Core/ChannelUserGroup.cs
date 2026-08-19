namespace RelayCove.Core;

public sealed record ChannelUserGroup(
    long GroupId,
    string Name,
    bool IsDeactivated,
    IReadOnlyList<long> Members,
    IReadOnlyList<long> DirectSubgroupIds);
