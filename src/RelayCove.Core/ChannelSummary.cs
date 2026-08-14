namespace RelayCove.Core;

public sealed record ChannelSummary(
    long ChannelId,
    string Name,
    string? Description,
    bool IsArchived,
    int? SubscriberCount,
    bool IsPrivate = false,
    bool IsSubscribed = false,
    string? Color = null);
