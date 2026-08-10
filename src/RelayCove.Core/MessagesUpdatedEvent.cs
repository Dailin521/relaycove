namespace RelayCove.Core;

public sealed record MessagesUpdatedEvent(IReadOnlyList<ChatMessage> Messages, long? EventId = null, DomainEventSource Source = DomainEventSource.Realtime) : DomainEvent(EventId, Source);
