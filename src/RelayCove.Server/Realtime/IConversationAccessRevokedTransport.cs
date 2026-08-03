namespace RelayCove.Server.Realtime;

internal interface IConversationAccessRevokedTransport
{
    Task SendAsync(
        string recipientUserId,
        Guid conversationId,
        CancellationToken cancellationToken);
}
