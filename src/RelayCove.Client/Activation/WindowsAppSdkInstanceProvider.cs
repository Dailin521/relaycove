using Microsoft.Extensions.Logging;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;

namespace RelayCove.Client.Activation;

internal sealed class WindowsAppSdkInstanceProvider : IWindowsAppInstanceProvider
{
    private readonly object stateGate = new();
    private readonly ILogger<WindowsAppSdkInstanceProvider> logger;
    private AppActivationArguments? currentActivation;

    public WindowsAppSdkInstanceProvider(ILogger<WindowsAppSdkInstanceProvider> logger)
    {
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public IWindowsAppInstanceRegistration FindOrRegister(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        AppActivationArguments activation;
        lock (stateGate)
        {
            currentActivation ??= AppInstance.GetCurrent().GetActivatedEventArgs();
            activation = currentActivation;
        }

        var keyInstance = AppInstance.FindOrRegisterForKey(key);
        return new WindowsAppSdkInstanceRegistration(
            keyInstance,
            activation,
            logger);
    }

    private sealed class WindowsAppSdkInstanceRegistration :
        IWindowsAppInstanceRegistration
    {
        private const int MaximumBufferedActivations = 64;
        private readonly object stateGate = new();
        private readonly AppInstance keyInstance;
        private readonly AppActivationArguments currentActivation;
        private readonly ILogger logger;
        private readonly bool ownsKey;
        private readonly uint processId;
        private readonly Queue<WindowsProcessActivation> bufferedActivations = new();
        private Action<WindowsProcessActivation>? activated;
        private bool disposed;

        public WindowsAppSdkInstanceRegistration(
            AppInstance keyInstance,
            AppActivationArguments currentActivation,
            ILogger logger)
        {
            this.keyInstance = keyInstance ?? throw new ArgumentNullException(nameof(keyInstance));
            this.currentActivation = currentActivation ??
                throw new ArgumentNullException(nameof(currentActivation));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            ownsKey = keyInstance.IsCurrent;
            processId = keyInstance.ProcessId;
            if (ownsKey)
            {
                keyInstance.Activated += OnActivated;
            }
        }

        public event Action<WindowsProcessActivation>? Activated
        {
            add
            {
                ArgumentNullException.ThrowIfNull(value);
                WindowsProcessActivation[] pending;
                lock (stateGate)
                {
                    ObjectDisposedException.ThrowIf(disposed, this);
                    activated += value;
                    pending = bufferedActivations.ToArray();
                    bufferedActivations.Clear();
                }

                foreach (var activation in pending)
                {
                    Dispatch(value, activation);
                }
            }
            remove
            {
                lock (stateGate)
                {
                    activated -= value;
                }
            }
        }

        public bool IsCurrent => ownsKey;

        public uint ProcessId => processId;

        public WindowsProcessActivation GetCurrentActivation()
        {
            lock (stateGate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
            }

            return Translate(currentActivation);
        }

        public Task RedirectCurrentActivationAsync()
        {
            lock (stateGate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
            }

            return keyInstance.RedirectActivationToAsync(currentActivation).AsTask();
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
                activated = null;
                bufferedActivations.Clear();
            }

            if (ownsKey)
            {
                try
                {
                    keyInstance.Activated -= OnActivated;
                }
                catch (Exception exception)
                {
                    logger.LogWarning(
                        "Unsubscribing the primary Windows app instance failed; " +
                        "errorType={ErrorType}.",
                        exception.GetType().Name);
                }

                try
                {
                    keyInstance.UnregisterKey();
                }
                catch (Exception exception)
                {
                    logger.LogWarning(
                        "Releasing the primary Windows app instance key failed; " +
                        "errorType={ErrorType}.",
                        exception.GetType().Name);
                }
            }
        }

        private void OnActivated(object? sender, AppActivationArguments args)
        {
            _ = sender;
            WindowsProcessActivation activation;
            try
            {
                activation = Translate(args);
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    "Reading a redirected Windows activation failed; " +
                    "errorType={ErrorType}.",
                    exception.GetType().Name);
                return;
            }

            Action<WindowsProcessActivation>? sink;
            lock (stateGate)
            {
                if (disposed)
                {
                    return;
                }

                sink = activated;
                if (sink is null)
                {
                    if (bufferedActivations.Count == MaximumBufferedActivations)
                    {
                        bufferedActivations.Dequeue();
                        logger.LogWarning(
                            "A buffered Windows activation was dropped at the bounded limit.");
                    }

                    bufferedActivations.Enqueue(activation);
                    return;
                }
            }

            Dispatch(sink, activation);
        }

        private void Dispatch(
            Action<WindowsProcessActivation> sink,
            WindowsProcessActivation activation)
        {
            try
            {
                sink(activation);
            }
            catch (Exception exception)
            {
                logger.LogError(
                    "Dispatching a redirected Windows activation failed; " +
                    "errorType={ErrorType}.",
                    exception.GetType().Name);
            }
        }

        private static WindowsProcessActivation Translate(AppActivationArguments activation)
        {
            if (activation.Kind == ExtendedActivationKind.Launch)
            {
                return WindowsProcessActivation.Launch();
            }

            if (activation.Kind == ExtendedActivationKind.AppNotification &&
                activation.Data is AppNotificationActivatedEventArgs notification)
            {
                return WindowsProcessActivation.AppNotification(notification.Argument);
            }

            return WindowsProcessActivation.Unsupported();
        }
    }
}
