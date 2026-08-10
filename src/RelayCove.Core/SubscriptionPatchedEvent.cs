namespace RelayCove.Core;

public sealed record SubscriptionPatchedEvent(
    long ChannelId,
    string? Name,
    bool? IsActive,
    long? EventId = null,
    DomainEventSource Source = DomainEventSource.Realtime) : DomainEvent(EventId, Source);
