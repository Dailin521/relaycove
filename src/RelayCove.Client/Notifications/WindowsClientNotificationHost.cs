using Microsoft.Extensions.Logging;

namespace RelayCove.Client.Notifications;

internal sealed class WindowsClientNotificationHost : IDisposable
{
    private readonly object stateGate = new();
    private readonly IWindowsAppNotificationManager manager;
    private readonly Action<ClientNotificationActivationTarget> activationSink;
    private readonly ILogger<WindowsClientNotificationHost> logger;
    private readonly TimeSpan nativeOperationTimeout;
    private bool registered;
    private bool registrationUncertain;
    private bool disposed;

    public WindowsClientNotificationHost(
        IWindowsAppNotificationManager manager,
        Action<ClientNotificationActivationTarget> activationSink,
        ILogger<WindowsClientNotificationHost> logger,
        TimeSpan? nativeOperationTimeout = null)
    {
        this.manager = manager ?? throw new ArgumentNullException(nameof(manager));
        this.activationSink = activationSink ??
            throw new ArgumentNullException(nameof(activationSink));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.nativeOperationTimeout = nativeOperationTimeout ?? TimeSpan.FromSeconds(10);
        if (this.nativeOperationTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(nativeOperationTimeout));
        }
    }

    public bool TryStart()
    {
        string? currentActivationArgument;
        lock (stateGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (registered)
            {
                return true;
            }

            if (registrationUncertain)
            {
                return false;
            }

            try
            {
                manager.NotificationInvoked += OnNotificationInvoked;
                try
                {
                    var registration = Task.Factory.StartNew(
                        RegisterAndReadCurrentActivation,
                        CancellationToken.None,
                        TaskCreationOptions.LongRunning,
                        TaskScheduler.Default);
                    if (!registration.Wait(nativeOperationTimeout))
                    {
                        manager.NotificationInvoked -= OnNotificationInvoked;
                        registrationUncertain = true;
                        ScheduleLateRegistrationCleanup(registration);
                        logger.LogError(
                            "Windows app notification registration timed out.");
                        return false;
                    }

                    var result = registration.GetAwaiter().GetResult();
                    if (!result.IsSupported)
                    {
                        manager.NotificationInvoked -= OnNotificationInvoked;
                        logger.LogWarning("Windows app notifications are not supported.");
                        return false;
                    }

                    registered = true;
                    currentActivationArgument = result.ActivationArgument;
                    if (result.ActivationReadException is not null)
                    {
                        logger.LogWarning(
                            "Reading the current Windows notification activation failed; " +
                            "errorType={ErrorType}.",
                            result.ActivationReadException.GetType().Name);
                    }

                    logger.LogInformation("Windows app notification host registered.");
                }
                catch
                {
                    manager.NotificationInvoked -= OnNotificationInvoked;
                    throw;
                }
            }
            catch (Exception exception)
            {
                logger.LogError(
                    "Windows app notification registration failed; errorType={ErrorType}.",
                    exception.GetType().Name);
                return false;
            }
        }

        if (currentActivationArgument is not null)
        {
            OnNotificationInvoked(currentActivationArgument);
        }

        return true;
    }

    public void Dispose()
    {
        lock (stateGate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            if (!registered)
            {
                return;
            }

            manager.NotificationInvoked -= OnNotificationInvoked;
            try
            {
                var unregistration = Task.Factory.StartNew(
                    manager.Unregister,
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);
                if (!unregistration.Wait(nativeOperationTimeout))
                {
                    logger.LogWarning(
                        "Windows app notification unregistration timed out.");
                }
                else
                {
                    unregistration.GetAwaiter().GetResult();
                }
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    "Windows app notification unregistration failed; errorType={ErrorType}.",
                    exception.GetType().Name);
            }

            registered = false;
        }
    }

    private void OnNotificationInvoked(string argument)
    {
        lock (stateGate)
        {
            if (disposed || !registered)
            {
                return;
            }
        }

        if (!WindowsNotificationActivationCodec.TryDecode(argument, out var target))
        {
            logger.LogWarning("A Windows notification activation target was rejected.");
            return;
        }

        try
        {
            activationSink(target!);
        }
        catch (Exception exception)
        {
            logger.LogError(
                "Dispatching a Windows notification activation failed; " +
                "errorType={ErrorType}.",
                exception.GetType().Name);
        }
    }

    private RegistrationResult RegisterAndReadCurrentActivation()
    {
        if (!manager.IsSupported())
        {
            return new RegistrationResult(
                IsSupported: false,
                ActivationArgument: null,
                ActivationReadException: null);
        }

        manager.Register();
        try
        {
            return new RegistrationResult(
                IsSupported: true,
                manager.GetCurrentActivationArgument(),
                ActivationReadException: null);
        }
        catch (Exception exception)
        {
            return new RegistrationResult(
                IsSupported: true,
                ActivationArgument: null,
                exception);
        }
    }

    private void ScheduleLateRegistrationCleanup(Task<RegistrationResult> registration)
    {
        _ = registration.ContinueWith(
            static (completed, state) =>
            {
                var host = (WindowsClientNotificationHost)state!;
                try
                {
                    if (completed.Status == TaskStatus.RanToCompletion &&
                        completed.Result.IsSupported)
                    {
                        host.manager.Unregister();
                    }
                }
                catch (Exception exception)
                {
                    host.logger.LogWarning(
                        "Cleaning a late Windows notification registration failed; " +
                        "errorType={ErrorType}.",
                        exception.GetType().Name);
                }
                finally
                {
                    lock (host.stateGate)
                    {
                        host.registrationUncertain = false;
                    }
                }
            },
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private sealed record RegistrationResult(
        bool IsSupported,
        string? ActivationArgument,
        Exception? ActivationReadException);
}
