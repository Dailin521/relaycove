namespace RelayCove.Core;

public sealed record MessageReactionChangedEvent(
    long MessageId,
    EmojiReaction Reaction,
    bool Add,
    long? EventId = null,
    DomainEventSource Source = DomainEventSource.Realtime) : DomainEvent(EventId, Source);
