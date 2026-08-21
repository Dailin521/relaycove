namespace RelayCove.Core;

public sealed record SubscriptionPatchedEvent(
    long ChannelId,
    string? Name,
    bool? IsActive,
    long? EventId = null,
    DomainEventSource Source = DomainEventSource.Realtime,
    bool? IsPrivate = null,
    bool? IsWebPublic = null,
    ChannelTopicsPolicy? TopicsPolicy = null,
    bool ClearEligibility = false) : DomainEvent(EventId, Source);
