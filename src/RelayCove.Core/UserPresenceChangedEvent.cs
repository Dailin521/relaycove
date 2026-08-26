namespace RelayCove.Core;

public sealed record UserPresenceChangedEvent(
    UserPresence Presence,
    long? EventId = null,
    DomainEventSource Source = DomainEventSource.Realtime) : DomainEvent(EventId, Source);
