namespace RelayCove.Core;

public sealed record ChannelSettingsLimits(
    int? MaxChannelNameLength,
    int? MaxChannelDescriptionLength,
    int? MaxChannelFolderNameLength,
    int? MaxChannelFolderDescriptionLength);
