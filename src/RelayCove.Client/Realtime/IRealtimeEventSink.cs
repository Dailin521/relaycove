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

    Task OnConversationAccessGrantedAsync(
        Guid conversationId,
        CancellationToken cancellationToken) => Task.CompletedTask;

    Task OnConversationAccessRevokedAsync(
        Guid conversationId,
        CancellationToken cancellationToken);

    Task OnAccountAccessRevokedAsync(
        AccountAccessRevokedEvent accountAccessRevoked,
        CancellationToken cancellationToken);
}
