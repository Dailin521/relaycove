namespace RelayCove.Client.Notifications;

internal sealed class FallbackClientNotificationPlatform : IClientNotificationPlatform
{
    private readonly IClientNotificationPlatform primary;
    private readonly IClientNotificationPlatform fallback;

    public FallbackClientNotificationPlatform(
        IClientNotificationPlatform primary,
        IClientNotificationPlatform fallback)
    {
        this.primary = primary ?? throw new ArgumentNullException(nameof(primary));
        this.fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
    }

    public async Task<ClientNotificationPlatformResult> SubmitAsync(
        ClientNotificationRequest request,
        CancellationToken cancellationToken)
    {
        var result = await primary.SubmitAsync(request, cancellationToken).ConfigureAwait(false);
        return result.Status == ClientNotificationPlatformStatus.Accepted
            ? result
            : await fallback.SubmitAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ClientNotificationPlatformResult> ClearConversationAsync(
        string accountScopeId,
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        var result = await primary
            .ClearConversationAsync(accountScopeId, conversationId, cancellationToken)
            .ConfigureAwait(false);
        return result.Status == ClientNotificationPlatformStatus.Accepted
            ? result
            : await fallback
                .ClearConversationAsync(accountScopeId, conversationId, cancellationToken)
                .ConfigureAwait(false);
    }

    public async Task<ClientNotificationPlatformResult> ClearSummaryAsync(
        string accountScopeId,
        CancellationToken cancellationToken)
    {
        var result = await primary.ClearSummaryAsync(accountScopeId, cancellationToken)
            .ConfigureAwait(false);
        return result.Status == ClientNotificationPlatformStatus.Accepted
            ? result
            : await fallback.ClearSummaryAsync(accountScopeId, cancellationToken)
                .ConfigureAwait(false);
    }
}
