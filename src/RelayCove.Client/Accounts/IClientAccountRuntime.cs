using RelayCove.Client.Auth;
using RelayCove.Client.Notifications;
using RelayCove.Client.Storage;
using RelayCove.Client.Sync;
using RelayCove.Shared.Realtime;

namespace RelayCove.Client.Accounts;

internal interface IClientAccountRuntime : IAsyncDisposable
{
    AccountScopeIdentity Identity { get; }

    ConnectionState ConnectionState { get; }

    bool TryAuthorizeNotificationTarget(ClientNotificationActivationTarget target);

    void UpdateActivity(ClientActivitySnapshot snapshot);

    Task<ClientAccountRuntimeStartOutcome> StartAsync(
        CancellationToken cancellationToken = default);

    Task<ClientSyncRunOutcome> RetryRealtimeAsync(
        CancellationToken cancellationToken = default);

    Task<ClientLogoutStatus> LogoutAsync(
        CancellationToken cancellationToken = default);
}
