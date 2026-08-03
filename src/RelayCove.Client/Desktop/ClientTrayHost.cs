using Microsoft.Extensions.Logging;

namespace RelayCove.Client.Desktop;

internal sealed class ClientTrayHost : IDisposable
{
    private readonly object stateGate = new();
    private readonly IClientTrayIcon trayIcon;
    private readonly Action<Action> dispatchToUi;
    private readonly Action openMainWindow;
    private readonly Action exitApplication;
    private readonly ILogger<ClientTrayHost> logger;
    private ClientTrayStatus status;
    private bool started;
    private bool disposed;
    private int exitRequested;

    public ClientTrayHost(
        IClientTrayIcon trayIcon,
        Action<Action> dispatchToUi,
        Action openMainWindow,
        Action exitApplication,
        ClientTrayStatus initialStatus,
        ILogger<ClientTrayHost> logger)
    {
        this.trayIcon = trayIcon ?? throw new ArgumentNullException(nameof(trayIcon));
        this.dispatchToUi = dispatchToUi ?? throw new ArgumentNullException(nameof(dispatchToUi));
        this.openMainWindow = openMainWindow ?? throw new ArgumentNullException(nameof(openMainWindow));
        this.exitApplication = exitApplication ?? throw new ArgumentNullException(nameof(exitApplication));
        status = initialStatus ?? throw new ArgumentNullException(nameof(initialStatus));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        trayIcon.OpenRequested += OnOpenRequested;
        trayIcon.ExitRequested += OnExitRequested;
    }

    public bool IsAvailable
    {
        get
        {
            lock (stateGate)
            {
                return started && !disposed;
            }
        }
    }

    public bool TryStart()
    {
        ClientTrayStatus current;
        lock (stateGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (started)
            {
                return true;
            }

            current = status;
        }

        try
        {
            trayIcon.Show(ClientTrayStatusFormatter.Format(current));
            lock (stateGate)
            {
                if (disposed)
                {
                    return false;
                }

                started = true;
            }

            return true;
        }
        catch (Exception exception)
        {
            logger.LogError(
                "Starting the Windows tray icon failed; errorType={ErrorType}.",
                exception.GetType().Name);
            return false;
        }
    }

    public void UpdateStatus(ClientTrayStatus value)
    {
        ArgumentNullException.ThrowIfNull(value);
        lock (stateGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            status = value;
            if (!started)
            {
                return;
            }
        }

        TryDispatch(UpdateStatusOnUi);
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
            started = false;
        }

        trayIcon.OpenRequested -= OnOpenRequested;
        trayIcon.ExitRequested -= OnExitRequested;
        try
        {
            trayIcon.Dispose();
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Disposing the Windows tray icon failed; errorType={ErrorType}.",
                exception.GetType().Name);
        }
    }

    private void OnOpenRequested() => TryDispatch(() =>
    {
        lock (stateGate)
        {
            if (disposed || !started)
            {
                return;
            }
        }

        openMainWindow();
    });

    private void OnExitRequested()
    {
        if (Interlocked.Exchange(ref exitRequested, 1) != 0)
        {
            return;
        }

        if (!TryDispatch(() =>
            {
                lock (stateGate)
                {
                    if (disposed || !started)
                    {
                        return;
                    }
                }

                exitApplication();
            }))
        {
            Interlocked.Exchange(ref exitRequested, 0);
        }
    }

    private void UpdateStatusOnUi()
    {
        ClientTrayStatus current;
        lock (stateGate)
        {
            if (disposed || !started)
            {
                return;
            }

            current = status;
        }

        try
        {
            trayIcon.Update(ClientTrayStatusFormatter.Format(current));
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Updating the Windows tray icon failed; errorType={ErrorType}.",
                exception.GetType().Name);
        }
    }

    private bool TryDispatch(Action action)
    {
        try
        {
            dispatchToUi(action);
            return true;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Dispatching a Windows tray action failed; errorType={ErrorType}.",
                exception.GetType().Name);
            return false;
        }
    }
}
