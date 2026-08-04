using RelayCove.Shared.Messages;

namespace RelayCove.Server.Realtime;

internal interface INewMessageTransport
{
    Task SendAsync(
        IReadOnlyList<NewMessageRecipient> recipients,
        MessageDto message,
        CancellationToken cancellationToken);
}

internal sealed record NewMessageRecipient(Guid UserId, long AccessTokenVersion);
