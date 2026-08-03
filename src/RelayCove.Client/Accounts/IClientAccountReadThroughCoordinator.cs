using RelayCove.Client.Sync;

namespace RelayCove.Client.Accounts;

internal interface IClientAccountReadThroughCoordinator : IAsyncDisposable
{
    Task<ClientReadThroughRunOutcome> TriggerAsync(
        CancellationToken cancellationToken = default);
}
