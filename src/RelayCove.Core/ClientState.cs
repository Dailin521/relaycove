namespace RelayCove.Core;

public sealed record ClientState
{
    public ClientState(
        IReadOnlyDictionary<long, ChatMessage>? messages = null,
        IReadOnlyDictionary<long, Subscription>? subscriptions = null,
        IReadOnlyDictionary<long, UserProfile>? users = null,
        IReadOnlyDictionary<string, TopicSummary>? topics = null,
        IReadOnlyDictionary<string, OutboxEntry>? outbox = null,
        UnreadState? unread = null,
        ConnectionState? connection = null,
        long? lastEventId = null)
    {
        Messages = new Dictionary<long, ChatMessage>(messages ?? new Dictionary<long, ChatMessage>());
        Subscriptions = new Dictionary<long, Subscription>(subscriptions ?? new Dictionary<long, Subscription>());
        Users = new Dictionary<long, UserProfile>(users ?? new Dictionary<long, UserProfile>());
        Topics = new Dictionary<string, TopicSummary>(topics ?? new Dictionary<string, TopicSummary>());
        Outbox = new Dictionary<string, OutboxEntry>(outbox ?? new Dictionary<string, OutboxEntry>());
        Unread = unread ?? new UnreadState();
        Connection = connection ?? ConnectionState.SignedOut;
        LastEventId = lastEventId;
    }

    public static ClientState Empty { get; } = new();
    public IReadOnlyDictionary<long, ChatMessage> Messages { get; init; }
    public IReadOnlyDictionary<long, Subscription> Subscriptions { get; init; }
    public IReadOnlyDictionary<long, UserProfile> Users { get; init; }
    public IReadOnlyDictionary<string, TopicSummary> Topics { get; init; }
    public IReadOnlyDictionary<string, OutboxEntry> Outbox { get; init; }
    public UnreadState Unread { get; init; }
    public ConnectionState Connection { get; init; }
    public long? LastEventId { get; init; }
}
