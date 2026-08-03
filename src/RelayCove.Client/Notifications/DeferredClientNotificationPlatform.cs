namespace RelayCove.Client.Notifications;

internal sealed class DeferredClientNotificationPlatform : IClientNotificationPlatform
{
    public Task<ClientNotificationPlatformResult> SubmitAsync(
        ClientNotificationRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ClientNotificationPlatformResult.TransientFailure);
    }

    public Task<ClientNotificationPlatformResult> ClearConversationAsync(
        string accountScopeId,
        Guid conversationId,
        CancellationToken cancellationToken) =>
        Task.FromResult(ClientNotificationPlatformResult.PermanentlyUnavailable);

    public Task<ClientNotificationPlatformResult> ClearSummaryAsync(
        string accountScopeId,
        CancellationToken cancellationToken) =>
        Task.FromResult(ClientNotificationPlatformResult.PermanentlyUnavailable);
}
