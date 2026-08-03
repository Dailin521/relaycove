namespace RelayCove.Client.Activation;

internal static class ClientApplicationShutdown
{
    public static Task RunBlockingOperationAsync(Action operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return Task.Factory.StartNew(
            operation,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    public static async Task StopPrimaryAsync(
        Action stopActivationDispatch,
        Action stopNotificationRouting,
        Func<Task> stopNativeNotifications,
        Action releaseInstanceKey)
    {
        ArgumentNullException.ThrowIfNull(stopActivationDispatch);
        ArgumentNullException.ThrowIfNull(stopNotificationRouting);
        ArgumentNullException.ThrowIfNull(stopNativeNotifications);
        ArgumentNullException.ThrowIfNull(releaseInstanceKey);

        stopActivationDispatch();
        stopNotificationRouting();
        try
        {
            await stopNativeNotifications();
        }
        finally
        {
            releaseInstanceKey();
        }
    }
}
