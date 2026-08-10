namespace RelayCove.Core;

public sealed record MessageDeletedEvent(IReadOnlyCollection<long> MessageIds, long? EventId = null, DomainEventSource Source = DomainEventSource.Realtime) : DomainEvent(EventId, Source);
