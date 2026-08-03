using Microsoft.Extensions.Logging;
using RelayCove.Client.Sync;

namespace RelayCove.Client.Accounts;

internal sealed class ClientReadThroughRequestor
{
    private readonly IClientAccountReadThroughCoordinator coordinator;
    private readonly ILogger<ClientReadThroughRequestor> logger;

    public ClientReadThroughRequestor(
        IClientAccountReadThroughCoordinator coordinator,
        ILogger<ClientReadThroughRequestor> logger)
    {
        this.coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void Request()
    {
        try
        {
            _ = ObserveAsync(coordinator.TriggerAsync(CancellationToken.None));
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception exception)
        {
            LogFailure(exception);
        }
    }

    private async Task ObserveAsync(Task<ClientReadThroughRunOutcome> request)
    {
        try
        {
            var outcome = await request.ConfigureAwait(false);
            if (outcome.Status is not ClientReadThroughRunStatus.Completed and
                not ClientReadThroughRunStatus.Canceled)
            {
                logger.LogWarning(
                    "Requested read-through upload did not complete; status={Status}; " +
                    "requests={RequestCount}; receipts={ReceiptCount}.",
                    outcome.Status,
                    outcome.RequestCount,
                    outcome.ReceiptCount);
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception exception)
        {
            LogFailure(exception);
        }
    }

    private void LogFailure(Exception exception)
    {
        logger.LogWarning(
            "Read-through upload request failed; errorType={ErrorType}.",
            exception.GetType().Name);
    }
}
