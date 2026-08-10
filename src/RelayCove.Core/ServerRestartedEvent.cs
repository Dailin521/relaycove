namespace RelayCove.Core;

public sealed record ServerRestartedEvent(
    int FeatureLevel,
    long? EventId = null,
    DomainEventSource Source = DomainEventSource.Realtime) : DomainEvent(EventId, Source);
