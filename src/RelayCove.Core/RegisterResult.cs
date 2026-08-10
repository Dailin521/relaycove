namespace RelayCove.Core;

public sealed record RegisterResult(
    string QueueId,
    long LastEventId,
    TimeSpan EventQueueLongPollTimeout,
    int MaxMessageLength,
    int MaxTopicLength,
    IReadOnlyList<Subscription> Subscriptions,
    IReadOnlyList<UserProfile> Users,
    IReadOnlyList<ConversationKey> RecentDirectMessages,
    UnreadState Unread,
    IReadOnlyList<DomainEvent> Events);
