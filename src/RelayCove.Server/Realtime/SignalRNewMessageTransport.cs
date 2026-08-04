using Microsoft.AspNetCore.SignalR;
using RelayCove.Server.Hubs;
using RelayCove.Shared.Messages;

namespace RelayCove.Server.Realtime;

internal sealed class SignalRNewMessageTransport(
    IHubContext<ChatHub, IChatClient> hubContext) : INewMessageTransport
{
    public async Task SendAsync(
        IReadOnlyList<NewMessageRecipient> recipients,
        MessageDto message,
        CancellationToken cancellationToken)
    {
        if (recipients.Count == 0)
        {
            return;
        }

        await hubContext.Clients
            .Groups(recipients
                .Select(recipient => AccountHubGroup.For(
                    recipient.UserId,
                    recipient.AccessTokenVersion)))
            .NewMessage(message)
            .WaitAsync(cancellationToken);
    }
}
