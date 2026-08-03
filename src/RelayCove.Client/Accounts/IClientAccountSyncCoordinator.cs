using RelayCove.Client.Sync;
using RelayCove.Shared.Messages;

namespace RelayCove.Client.Accounts;

internal interface IClientAccountSyncCoordinator : IAsyncDisposable
{
    Task<ClientSyncRunOutcome> TriggerAsync(
        SyncReason reason,
        CancellationToken cancellationToken = default);
}
