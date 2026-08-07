namespace RelayCove.Server.Realtime;

internal interface IConversationAccessGrantedTransport
{
    Task SendAsync(
        IReadOnlyList<NewMessageRecipient> recipients,
        Guid conversationId,
        CancellationToken cancellationToken);
}
