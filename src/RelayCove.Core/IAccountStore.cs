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
    Task<bool> IsCacheUnlockedAsync(
        AccountId accountId,
        CancellationToken cancellationToken = default);
    Task SetCacheUnlockedAsync(
        AccountId accountId,
        bool isUnlocked,
        CancellationToken cancellationToken = default);
    Task ClearAsync(AccountId accountId, CancellationToken cancellationToken = default);
}
