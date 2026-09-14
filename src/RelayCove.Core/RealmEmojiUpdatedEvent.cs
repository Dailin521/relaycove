namespace RelayCove.Core;

public sealed record RealmEmojiUpdatedEvent(
    IReadOnlyList<RealmEmoji> Emojis,
    long? EventId = null,
    DomainEventSource Source = DomainEventSource.Realtime) : DomainEvent(EventId, Source);
