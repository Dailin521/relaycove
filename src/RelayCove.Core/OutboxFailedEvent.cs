namespace RelayCove.Core;

public sealed record OutboxFailedEvent(string LocalId, OutboxFailureKind Failure)
    : DomainEvent(null, DomainEventSource.Local);
