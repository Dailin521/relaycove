namespace RelayCove.Core;

public sealed record ClientState
{
    public ClientState(
        IReadOnlyDictionary<long, ChatMessage>? messages = null,
        IReadOnlyDictionary<long, Subscription>? subscriptions = null,
        IReadOnlyDictionary<long, UserProfile>? users = null,
        IReadOnlyDictionary<string, TopicSummary>? topics = null,
        IReadOnlyDictionary<string, ConversationSummary>? conversationSummaries = null,
        IReadOnlyDictionary<string, OutboxEntry>? outbox = null,
        UnreadState? unread = null,
        ConnectionState? connection = null,
        long? lastEventId = null,
        IReadOnlyDictionary<long, MessageMutationState>? messageMutations = null,
        PresenceState? presence = null,
        UserStatusState? userStatuses = null)
    {
        Messages = new Dictionary<long, ChatMessage>(messages ?? new Dictionary<long, ChatMessage>());
        Subscriptions = new Dictionary<long, Subscription>(subscriptions ?? new Dictionary<long, Subscription>());
        Users = new Dictionary<long, UserProfile>(users ?? new Dictionary<long, UserProfile>());
        Topics = new Dictionary<string, TopicSummary>(topics ?? new Dictionary<string, TopicSummary>());
        ConversationSummaries = new Dictionary<string, ConversationSummary>(conversationSummaries ?? new Dictionary<string, ConversationSummary>());
        Outbox = new Dictionary<string, OutboxEntry>(outbox ?? new Dictionary<string, OutboxEntry>());
        MessageMutations = new Dictionary<long, MessageMutationState>(messageMutations ?? new Dictionary<long, MessageMutationState>());
        Unread = unread ?? new UnreadState();
        Connection = connection ?? ConnectionState.SignedOut;
        LastEventId = lastEventId;
        Presence = presence ?? PresenceState.Unavailable;
        UserStatuses = userStatuses ?? UserStatusState.Unavailable;
    }

    public static ClientState Empty { get; } = new();
    public IReadOnlyDictionary<long, ChatMessage> Messages { get; init; }
    public IReadOnlyDictionary<long, Subscription> Subscriptions { get; init; }
    public IReadOnlyDictionary<long, UserProfile> Users { get; init; }
    public IReadOnlyDictionary<string, TopicSummary> Topics { get; init; }
    public IReadOnlyDictionary<string, ConversationSummary> ConversationSummaries { get; init; }
    public IReadOnlyDictionary<string, OutboxEntry> Outbox { get; init; }
    public IReadOnlyDictionary<long, MessageMutationState> MessageMutations { get; init; }
    public UnreadState Unread { get; init; }
    public ConnectionState Connection { get; init; }
    public long? LastEventId { get; init; }
    public PresenceState Presence { get; init; }
    public UserStatusState UserStatuses { get; init; }
}
