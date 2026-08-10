namespace RelayCove.Core;

/// <summary>
/// Represents a recognized Zulip event whose property is deliberately outside the frozen MVP.
/// It still advances the event cursor, but does not mutate product state.
/// </summary>
public sealed record IgnoredDomainEvent(
    string EventKind,
    string ReasonCode,
    long? EventId = null,
    DomainEventSource Source = DomainEventSource.Realtime)
    : DomainEvent(EventId, Source);
