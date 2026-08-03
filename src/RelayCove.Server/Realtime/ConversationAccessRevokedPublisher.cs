namespace RelayCove.Server.Realtime;

internal sealed class ConversationAccessRevokedPublisher(
    IConversationAccessRevokedTransport transport,
    ILogger<ConversationAccessRevokedPublisher> logger)
{
    public async Task TryPublishAsync(Guid targetUserId, Guid conversationId)
    {
        try
        {
            await transport.SendAsync(
                targetUserId.ToString("D"),
                conversationId,
                CancellationToken.None);
            logger.LogInformation(
                "Published realtime access revocation for target {TargetUserId} in {ConversationId}.",
                targetUserId,
                conversationId);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Realtime access revocation failed for target {TargetUserId} in {ConversationId}.",
                targetUserId,
                conversationId);
        }
    }
}
