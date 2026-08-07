using RelayCove.Client.Realtime;
using RelayCove.Shared.Messages;
using RelayCove.Shared.Realtime;

namespace RelayCove.Client.Accounts;

internal sealed class ClientAccountRealtimeEventSink : IRealtimeEventSink
{
    private readonly IRealtimeEventSink inner;
    private readonly ClientAccountSyncRequestor syncRequestor;
    private readonly Action<ConnectionState> publishConnectionState;
    private readonly Func<long> accessTokenVersionProvider;
    private readonly Func<CancellationToken, Task> accountAccessRevoked;
    private int reconnectPending;

    public ClientAccountRealtimeEventSink(
        IRealtimeEventSink inner,
        ClientAccountSyncRequestor syncRequestor,
        Action<ConnectionState>? publishConnectionState = null,
        Func<CancellationToken, Task>? accountAccessRevoked = null,
        Func<long>? accessTokenVersionProvider = null)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        this.syncRequestor = syncRequestor ??
            throw new ArgumentNullException(nameof(syncRequestor));
        this.publishConnectionState = publishConnectionState ?? (static _ => { });
        this.accessTokenVersionProvider = accessTokenVersionProvider ?? (static () => 0);
        this.accountAccessRevoked = accountAccessRevoked ?? (static _ => Task.CompletedTask);
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

    public Task OnConversationAccessGrantedAsync(
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        if (conversationId != Guid.Empty)
        {
            syncRequestor.Request(SyncReason.Reconnect);
        }

        return inner.OnConversationAccessGrantedAsync(conversationId, cancellationToken);
    }

    public Task OnConversationAccessRevokedAsync(
        Guid conversationId,
        CancellationToken cancellationToken) =>
        inner.OnConversationAccessRevokedAsync(conversationId, cancellationToken);

    public Task OnAccountAccessRevokedAsync(
        AccountAccessRevokedEvent accountAccessRevoked,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(accountAccessRevoked);
        return accessTokenVersionProvider() < accountAccessRevoked.MinimumAccessTokenVersion
            ? this.accountAccessRevoked(cancellationToken)
            : Task.CompletedTask;
    }
}
