namespace RelayCove.Client.Notifications;

internal interface IClientNotificationPlatform
{
    Task<ClientNotificationPlatformResult> SubmitAsync(
        ClientNotificationRequest request,
        CancellationToken cancellationToken);

    Task<ClientNotificationPlatformResult> ClearConversationAsync(
        Guid conversationId,
        CancellationToken cancellationToken);
}
