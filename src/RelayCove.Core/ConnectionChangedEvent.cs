namespace RelayCove.Core;

public sealed record ConnectionChangedEvent(ConnectionState Connection, long? EventId = null, DomainEventSource Source = DomainEventSource.Realtime) : DomainEvent(EventId, Source);
