namespace RelayCove.Core;

public sealed record UserUpsertEvent(
    UserProfile User,
    long? EventId = null,
    DomainEventSource Source = DomainEventSource.Realtime) : DomainEvent(EventId, Source);
