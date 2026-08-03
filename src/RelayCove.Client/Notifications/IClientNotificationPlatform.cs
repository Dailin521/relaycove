namespace RelayCove.Client.Notifications;

internal interface IClientNotificationPlatform
{
    Task<ClientNotificationPlatformResult> SubmitAsync(
        ClientNotificationRequest request,
        CancellationToken cancellationToken);

    Task<ClientNotificationPlatformResult> ClearConversationAsync(
        string accountScopeId,
        Guid conversationId,
        CancellationToken cancellationToken);

    Task<ClientNotificationPlatformResult> ClearSummaryAsync(
        string accountScopeId,
        CancellationToken cancellationToken);
}
