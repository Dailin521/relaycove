namespace RelayCove.Core;

public sealed record ChannelPersonalSettings(
    long ChannelId,
    string? Color,
    bool IsMuted,
    bool IsPinned,
    bool? DesktopNotifications,
    bool? AudibleNotifications,
    bool? PushNotifications,
    bool? EmailNotifications,
    bool? WildcardMentionsNotify);
