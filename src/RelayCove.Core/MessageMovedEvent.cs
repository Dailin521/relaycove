namespace RelayCove.Core;

public sealed record MessageMovedEvent(IReadOnlyCollection<long> MessageIds, ConversationKey Destination, long? EventId = null, DomainEventSource Source = DomainEventSource.Realtime) : DomainEvent(EventId, Source);
