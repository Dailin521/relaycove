namespace RelayCove.Core;

public sealed record UnknownDomainEvent(string Kind, long? EventId = null, DomainEventSource Source = DomainEventSource.Realtime) : DomainEvent(EventId, Source);
