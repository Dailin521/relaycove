using RelayCove.Client.Realtime;
using RelayCove.Shared.Messages;
using RelayCove.Shared.Realtime;

namespace RelayCove.Client.Accounts;

internal sealed class ClientAccountRealtimeEventSink : IRealtimeEventSink
{
    private readonly IRealtimeEventSink inner;
    private readonly ClientAccountSyncRequestor syncRequestor;
    private readonly Action<ConnectionState> publishConnectionState;
    private int reconnectPending;

    public ClientAccountRealtimeEventSink(
        IRealtimeEventSink inner,
        ClientAccountSyncRequestor syncRequestor,
        Action<ConnectionState>? publishConnectionState = null)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        this.syncRequestor = syncRequestor ??
            throw new ArgumentNullException(nameof(syncRequestor));
        this.publishConnectionState = publishConnectionState ?? (static _ => { });
    }

    public async Task OnConnectionStateChangedAsync(
        ConnectionState state,
        CancellationToken cancellationToken)
    {
        try
        {
            await inner.OnConnectionStateChangedAsync(state, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            publishConnectionState(state);
        }

        if (state == ConnectionState.Reconnecting)
        {
            Volatile.Write(ref reconnectPending, 1);
            return;
        }

        if (state == ConnectionState.Connected &&
            Interlocked.Exchange(ref reconnectPending, 0) == 1)
        {
            syncRequestor.Request(SyncReason.Reconnect);
            return;
        }

        if (state is ConnectionState.Disconnected or ConnectionState.ServerUnavailable)
        {
            Volatile.Write(ref reconnectPending, 0);
        }
    }

    public Task OnNewMessageAsync(
        MessageDto message,
        CancellationToken cancellationToken) =>
        inner.OnNewMessageAsync(message, cancellationToken);

    public Task OnConversationAccessRevokedAsync(
        Guid conversationId,
        CancellationToken cancellationToken) =>
        inner.OnConversationAccessRevokedAsync(conversationId, cancellationToken);
}
