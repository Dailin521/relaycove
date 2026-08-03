using RelayCove.Client.Auth;
using RelayCove.Client.Notifications;
using RelayCove.Client.Storage;
using RelayCove.Client.Sync;
using RelayCove.Shared.Realtime;

namespace RelayCove.Client.Accounts;

internal interface IClientAccountRuntime : IAsyncDisposable
{
    event Action<ConnectionState> ConnectionStateChanged;

    event Action<long> ConversationStateChanged;

    AccountScopeIdentity Identity { get; }

    ConnectionState ConnectionState { get; }

    bool TryAuthorizeNotificationTarget(ClientNotificationActivationTarget target);

    void UpdateActivity(ClientActivitySnapshot snapshot);

    Task<LocalConversationListReadOutcome> ReadConversationListAsync(
        CancellationToken cancellationToken = default);

    Task<LocalMessagePageReadOutcome> ReadMessagePageAsync(
        Guid conversationId,
        long? beforeMessageId,
        int limit,
        CancellationToken cancellationToken = default);

    Task<ClientMessageHistoryPageOutcome> LoadMessageHistoryAsync(
        Guid conversationId,
        long? beforeMessageId,
        int limit,
        CancellationToken cancellationToken = default);

    Task<ClientMessageAroundOutcome> LoadMessageAroundAsync(
        Guid conversationId,
        long messageId,
        int before,
        int after,
        CancellationToken cancellationToken = default);

    Task<ClientMessageSendOutcome> SendTextMessageAsync(
        Guid conversationId,
        string? content,
        long? replyToMessageId = null,
        CancellationToken cancellationToken = default);

    Task<ClientMessageSendOutcome> RetryPendingMessageAsync(
        Guid conversationId,
        Guid clientMessageId,
        CancellationToken cancellationToken = default);

    Task<LocalCacheOperationStatus> MarkConversationRenderedThroughAsync(
        Guid conversationId,
        long messageId,
        CancellationToken cancellationToken = default);

    Task<ClientAccountRuntimeStartOutcome> StartAsync(
        CancellationToken cancellationToken = default);

    Task<ClientSyncRunOutcome> RetryRealtimeAsync(
        CancellationToken cancellationToken = default);

    Task<ClientLogoutStatus> LogoutAsync(
        CancellationToken cancellationToken = default);
}
