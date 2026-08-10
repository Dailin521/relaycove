namespace RelayCove.Core;

public sealed record SubscriptionChangedEvent(Subscription Subscription, bool IsRemoved, long? EventId = null, DomainEventSource Source = DomainEventSource.Realtime) : DomainEvent(EventId, Source);
