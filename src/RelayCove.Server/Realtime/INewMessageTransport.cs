using RelayCove.Shared.Messages;

namespace RelayCove.Server.Realtime;

internal interface INewMessageTransport
{
    Task SendAsync(
        IReadOnlyList<string> recipientUserIds,
        MessageDto message,
        CancellationToken cancellationToken);
}
