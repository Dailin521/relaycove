using Microsoft.AspNetCore.SignalR;
using RelayCove.Server.Hubs;
using RelayCove.Shared.Messages;

namespace RelayCove.Server.Realtime;

internal sealed class SignalRNewMessageTransport(
    IHubContext<ChatHub, IChatClient> hubContext) : INewMessageTransport
{
    public async Task SendAsync(
        IReadOnlyList<string> recipientUserIds,
        MessageDto message,
        CancellationToken cancellationToken)
    {
        if (recipientUserIds.Count == 0)
        {
            return;
        }

        await hubContext.Clients
            .Users(recipientUserIds)
            .NewMessage(message)
            .WaitAsync(cancellationToken);
    }
}
