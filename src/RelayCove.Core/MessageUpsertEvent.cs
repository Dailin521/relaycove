namespace RelayCove.Core;

public sealed record MessageUpsertEvent(ChatMessage Message, long? EventId = null, DomainEventSource Source = DomainEventSource.Realtime, string? LocalId = null) : DomainEvent(EventId, Source);
