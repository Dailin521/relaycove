namespace RelayCove.Core;

public sealed record SubscriptionRemovedEvent(
    long ChannelId,
    long? EventId = null,
    DomainEventSource Source = DomainEventSource.Realtime) : DomainEvent(EventId, Source);
