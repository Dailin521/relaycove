namespace RelayCove.Core;

public sealed record HeartbeatEvent(long? EventId = null, DomainEventSource Source = DomainEventSource.Realtime) : DomainEvent(EventId, Source);
