namespace RelayCove.Core;

public sealed record OutboxQueuedEvent(OutboxEntry Entry) : DomainEvent(null, DomainEventSource.Local);
