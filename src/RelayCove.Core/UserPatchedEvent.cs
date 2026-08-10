namespace RelayCove.Core;

public sealed record UserPatchedEvent(
    long UserId,
    string? FullName,
    string? Email,
    bool? IsActive,
    long? EventId = null,
    DomainEventSource Source = DomainEventSource.Realtime) : DomainEvent(EventId, Source);
