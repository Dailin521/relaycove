using Microsoft.Extensions.Logging;
using RelayCove.Client.Notifications;

namespace RelayCove.Client.Activation;

internal sealed class ClientActivationDispatcher : IDisposable
{
    private readonly object dispatchGate = new();
    private readonly ClientNotificationActivationRouter notificationRouter;
    private readonly Action launchSink;
    private readonly ILogger<ClientActivationDispatcher> logger;
    private bool disposed;

    public ClientActivationDispatcher(
        ClientNotificationActivationRouter notificationRouter,
        Action launchSink,
        ILogger<ClientActivationDispatcher> logger)
    {
        this.notificationRouter = notificationRouter ??
            throw new ArgumentNullException(nameof(notificationRouter));
        this.launchSink = launchSink ?? throw new ArgumentNullException(nameof(launchSink));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void Dispatch(WindowsProcessActivation activation)
    {
        ArgumentNullException.ThrowIfNull(activation);
        lock (dispatchGate)
        {
            if (disposed)
            {
                return;
            }

            switch (activation.Kind)
            {
                case WindowsProcessActivationKind.Launch:
                    DispatchLaunch();
                    break;
                case WindowsProcessActivationKind.AppNotification:
                    DispatchNotificationArgument(activation.NotificationArgument);
                    break;
                default:
                    logger.LogInformation("An unsupported Windows activation was ignored.");
                    break;
            }
        }
    }

    public ClientNotificationActivationRouteStatus Dispatch(
        ClientNotificationActivationTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        lock (dispatchGate)
        {
            return disposed
                ? ClientNotificationActivationRouteStatus.Stopping
                : notificationRouter.TryRoute(target);
        }
    }

    public void Dispose()
    {
        lock (dispatchGate)
        {
            disposed = true;
        }
    }

    private void DispatchLaunch()
    {
        try
        {
            launchSink();
        }
        catch (Exception exception)
        {
            logger.LogError(
                "Dispatching a Windows launch activation failed; errorType={ErrorType}.",
                exception.GetType().Name);
        }
    }

    private void DispatchNotificationArgument(string? argument)
    {
        if (argument is null ||
            !WindowsNotificationActivationCodec.TryDecode(argument, out var target))
        {
            logger.LogWarning("A Windows notification activation target was rejected.");
            return;
        }

        _ = notificationRouter.TryRoute(target!);
    }
}
