using RelayCove.Server.Services;

namespace RelayCove.Server.Hosting;

public sealed class AttachmentStorageMaintenanceHostedService(
    AttachmentStorageRecoveryHostedService recovery,
    TimeProvider timeProvider,
    ILogger<AttachmentStorageMaintenanceHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(CleanupInterval, timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await recovery.CleanupExpiredUnboundAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    "Periodic unbound attachment cleanup failed and will retry later. Error type: {ErrorType}.",
                    exception.GetType().Name);
            }
        }
    }
}
