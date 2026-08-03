using Microsoft.Extensions.Logging;
using RelayCove.Client.Notifications;

namespace RelayCove.Client.Desktop;

internal sealed class WindowsDesktopNotificationAttention : IClientNotificationAttention
{
    private readonly object stateGate = new();
    private readonly WindowsMainWindowState windowState;
    private readonly IWindowsDesktopAttentionNative native;
    private readonly ILogger<WindowsDesktopNotificationAttention> logger;
    private nint flashingWindowHandle;

    public WindowsDesktopNotificationAttention(
        WindowsMainWindowState windowState,
        ILogger<WindowsDesktopNotificationAttention> logger,
        IWindowsDesktopAttentionNative? native = null)
    {
        this.windowState = windowState ?? throw new ArgumentNullException(nameof(windowState));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.native = native ?? new WindowsDesktopAttentionNative();
    }

    public void SignalAcceptedToast()
    {
        TryPlaySound();

        lock (stateGate)
        {
            var window = windowState.Current;
            if (window.IsForeground || window.WindowHandle == nint.Zero)
            {
                StopFlashingCore();
                return;
            }

            if (flashingWindowHandle != nint.Zero &&
                flashingWindowHandle != window.WindowHandle)
            {
                StopFlashingCore();
            }

            try
            {
                native.StartTaskbarFlash(window.WindowHandle);
                // FlashWindowEx reports the window's previous active state, not
                // operation success. A completed call owns a matching STOP duty.
                flashingWindowHandle = window.WindowHandle;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    "Starting taskbar notification flashing failed; errorType={ErrorType}.",
                    exception.GetType().Name);
            }
        }
    }

    public void StopFlashing()
    {
        lock (stateGate)
        {
            StopFlashingCore();
        }
    }

    private void TryPlaySound()
    {
        try
        {
            if (!native.PlayNotificationSound())
            {
                logger.LogWarning("Playing the Windows notification sound failed.");
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Playing the Windows notification sound failed; errorType={ErrorType}.",
                exception.GetType().Name);
        }
    }

    private void StopFlashingCore()
    {
        var windowHandle = flashingWindowHandle;
        if (windowHandle == nint.Zero)
        {
            return;
        }

        flashingWindowHandle = nint.Zero;
        try
        {
            native.StopTaskbarFlash(windowHandle);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Stopping taskbar notification flashing failed; errorType={ErrorType}.",
                exception.GetType().Name);
        }
    }
}
