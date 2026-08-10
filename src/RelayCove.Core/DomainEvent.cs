namespace RelayCove.Core;

public abstract record DomainEvent(long? EventId, DomainEventSource Source);
