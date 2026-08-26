namespace RelayCove.Core;

public sealed record OwnPresenceEnabledChangedEvent(
    bool? IsEnabled,
    long? EventId = null,
    DomainEventSource Source = DomainEventSource.Realtime) : DomainEvent(EventId, Source);
