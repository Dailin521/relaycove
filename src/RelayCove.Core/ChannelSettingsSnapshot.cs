namespace RelayCove.Core;

public sealed record ChannelSettingsSnapshot(
    IReadOnlyList<ChannelSummary> Channels,
    IReadOnlyList<ChannelFolder> Folders,
    IReadOnlyList<ChannelUserGroup> UserGroups,
    long CurrentUserId,
    bool IsOrganizationAdministrator,
    bool IsGuest,
    ChannelSettingsLimits Limits);
