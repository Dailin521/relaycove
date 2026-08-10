namespace RelayCove.Core;

public sealed record TopicUpsertEvent(TopicSummary Topic, long? EventId = null, DomainEventSource Source = DomainEventSource.Realtime) : DomainEvent(EventId, Source);
