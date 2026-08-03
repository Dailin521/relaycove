using Microsoft.Extensions.Logging;
using RelayCove.Client.Sync;
using RelayCove.Shared.Messages;

namespace RelayCove.Client.Accounts;

internal sealed class ClientAccountSyncRequestor
{
    private readonly IClientAccountSyncCoordinator coordinator;
    private readonly ILogger<ClientAccountSyncRequestor> logger;

    public ClientAccountSyncRequestor(
        IClientAccountSyncCoordinator coordinator,
        ILogger<ClientAccountSyncRequestor> logger)
    {
        this.coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void Request(SyncReason reason)
    {
        try
        {
            _ = ObserveAsync(coordinator.TriggerAsync(reason, CancellationToken.None), reason);
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception exception)
        {
            LogFailure(reason, exception);
        }
    }

    private async Task ObserveAsync(Task<ClientSyncRunOutcome> request, SyncReason reason)
    {
        try
        {
            var outcome = await request.ConfigureAwait(false);
            if (outcome.Status is not ClientSyncRunStatus.Completed and
                not ClientSyncRunStatus.Canceled)
            {
                logger.LogWarning(
                    "Requested account sync did not complete; reason={Reason}; status={Status}.",
                    reason,
                    outcome.Status);
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception exception)
        {
            LogFailure(reason, exception);
        }
    }

    private void LogFailure(SyncReason reason, Exception exception)
    {
        logger.LogWarning(
            "Account sync request failed; reason={Reason}; errorType={ErrorType}.",
            reason,
            exception.GetType().Name);
    }
}
