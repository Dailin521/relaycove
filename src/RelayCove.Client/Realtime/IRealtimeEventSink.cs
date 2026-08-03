using RelayCove.Shared.Messages;
using RelayCove.Shared.Realtime;

namespace RelayCove.Client.Realtime;

public interface IRealtimeEventSink
{
    Task OnConnectionStateChangedAsync(
        ConnectionState state,
        CancellationToken cancellationToken);

    Task OnNewMessageAsync(
        MessageDto message,
        CancellationToken cancellationToken);

    Task OnConversationAccessRevokedAsync(
        Guid conversationId,
        CancellationToken cancellationToken);
}
