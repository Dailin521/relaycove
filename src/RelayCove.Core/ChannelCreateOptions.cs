namespace RelayCove.Core;

public sealed record ChannelCreateOptions(
    string Name,
    string? Description,
    bool IsPrivate,
    bool IsWebPublic,
    bool HistoryPublicToSubscribers,
    bool IsDefaultStream);
