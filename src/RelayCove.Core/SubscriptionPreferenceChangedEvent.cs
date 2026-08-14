namespace RelayCove.Core;

public sealed record SubscriptionPreferenceChangedEvent(
    long ChannelId,
    SubscriptionPreference Preference,
    bool Value,
    long? EventId = null,
    DomainEventSource Source = DomainEventSource.Realtime) : DomainEvent(EventId, Source);
