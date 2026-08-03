namespace RelayCove.Client.Notifications;

internal interface IClientNotificationPlatform
{
    Task<ClientNotificationPlatformResult> SubmitAsync(
        ClientNotificationRequest request,
        CancellationToken cancellationToken);

    Task ClearConversationAsync(
        Guid conversationId,
        CancellationToken cancellationToken);
}
