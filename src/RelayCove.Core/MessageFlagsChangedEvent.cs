namespace RelayCove.Core;

public sealed record MessageFlagsChangedEvent(IReadOnlyCollection<long> MessageIds, bool AllMessages, MessageFlagOperation Operation, string Flag, long? EventId = null, DomainEventSource Source = DomainEventSource.Realtime) : DomainEvent(EventId, Source);
