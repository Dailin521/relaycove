namespace RelayCove.Core;

public sealed record SendConfirmedEvent(string LocalId, ChatMessage Message, long? EventId = null) : DomainEvent(EventId, DomainEventSource.Send);
