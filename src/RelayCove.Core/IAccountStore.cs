namespace RelayCove.Core;

public interface IAccountStore
{
    Task<IReadOnlyList<StoredAccount>> ListAsync(CancellationToken cancellationToken = default);
    Task InitializeAsync(StoredAccount account, CancellationToken cancellationToken = default);
    Task MigrateAsync(AccountId accountId, CancellationToken cancellationToken = default);
    Task<AccountSnapshot?> LoadAsync(AccountId accountId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ChatMessage>> QueryMessagesAsync(
        AccountId accountId,
        ConversationKey conversation,
        long? beforeMessageId,
        int limit,
        CancellationToken cancellationToken = default);
    Task<MessagePage> QueryMessagePageAsync(
        AccountId accountId,
        ConversationKey conversation,
        long? beforeMessageId,
        int limit,
        CancellationToken cancellationToken = default);
    Task StoreMessagePageAsync(
        AccountId accountId,
        IReadOnlyCollection<ChatMessage> messages,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ConversationSummary>> QueryConversationSummariesAsync(
        AccountId accountId,
        IReadOnlyCollection<ConversationKey>? conversations = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ConversationSummary>>([]);
    Task<IReadOnlyList<TopicSummary>> QueryTopicSummariesAsync(
        AccountId accountId,
        IReadOnlyCollection<ChannelTopic> topics,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TopicSummary>>([]);
    Task ReplaceRegisterSnapshotAsync(
        AccountId accountId,
        RegisterResult snapshot,
        CancellationToken cancellationToken = default);
    Task ApplyBatchAsync(
        AccountId accountId,
        IReadOnlyCollection<DomainEvent> events,
        CancellationToken cancellationToken = default);
    Task PurgeSubscriptionAsync(
        AccountId accountId,
        long channelId,
        CancellationToken cancellationToken = default);
    Task PurgeConversationAsync(
        AccountId accountId,
        ConversationKey conversation,
        CancellationToken cancellationToken = default) =>
        Task.FromException(new NotSupportedException("Conversation cache clearing is not available."));
    Task<bool> IsCacheUnlockedAsync(
        AccountId accountId,
        CancellationToken cancellationToken = default);
    Task SetCacheUnlockedAsync(
        AccountId accountId,
        bool isUnlocked,
        CancellationToken cancellationToken = default);
    Task ClearAsync(AccountId accountId, CancellationToken cancellationToken = default);
}
