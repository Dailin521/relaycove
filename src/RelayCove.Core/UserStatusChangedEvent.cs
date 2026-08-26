namespace RelayCove.Core;

public sealed record UserStatusChangedEvent(
    long UserId,
    UserStatusContent? Status,
    long? EventId = null,
    DomainEventSource Source = DomainEventSource.Realtime) : DomainEvent(EventId, Source);
