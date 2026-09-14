namespace RelayCove.Core;

public sealed record MessageActionPolicyInvalidatedEvent(
    long? EventId = null,
    DomainEventSource Source = DomainEventSource.Realtime) : DomainEvent(EventId, Source);
