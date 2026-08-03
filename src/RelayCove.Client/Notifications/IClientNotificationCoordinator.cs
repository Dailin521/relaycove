namespace RelayCove.Client.Notifications;

internal interface IClientNotificationCoordinator : IAsyncDisposable
{
    Task<ClientNotificationDispatchOutcome> DispatchAsync(
        IReadOnlyCollection<long> messageIds,
        ClientNotificationDispatchMode mode,
        CancellationToken cancellationToken = default,
        ClientNotificationAttentionGate? attentionGate = null);

    Task ConversationRevokedAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default);
}
