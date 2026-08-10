namespace RelayCove.Core;

public sealed record EventBatch(IReadOnlyList<DomainEvent> Events, long LastEventId);
