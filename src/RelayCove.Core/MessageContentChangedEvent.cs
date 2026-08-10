namespace RelayCove.Core;

public sealed record MessageContentChangedEvent(
    long MessageId,
    string Content,
    long? EventId = null,
    DomainEventSource Source = DomainEventSource.Realtime) : DomainEvent(EventId, Source);
