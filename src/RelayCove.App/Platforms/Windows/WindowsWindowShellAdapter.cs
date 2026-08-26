using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using RelayCove.App.Services;
using WinRT.Interop;

namespace RelayCove.App.Platforms.Windows;

public sealed class WindowsWindowShellAdapter : IWindowShellAdapter
{
    private const uint WindowClose = 0x0010;
    private Window? _window;
    private AppWindow? _appWindow;
    private nint _windowHandle;
    private bool _isPinned;
    private bool _exitRequested;
#if DEBUG
    private bool _previewPlacementApplied;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _previewPlacementTimer;
#endif

    public event EventHandler? StateChanged;

    public bool IsPinned => _isPinned;
    public bool IsForeground => IsForegroundWindow(
        _windowHandle,
        GetForegroundWindow(),
        _windowHandle != 0 && IsIconic(_windowHandle));

    public void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (ReferenceEquals(_window, window)) return;

        if (_window is not null)
        {
            _window.HandlerChanged -= OnHandlerChanged;
            _window.Destroying -= OnWindowDestroying;
        }

        _window = window;
#if DEBUG
        _window.Width = NativeShellPreviewSession.IsRequested ? NativeShellPreviewSession.RequestedWidth : 1440;
        _window.Height = NativeShellPreviewSession.IsRequested ? NativeShellPreviewSession.RequestedHeight : 900;
#else
        _window.Width = 1440;
        _window.Height = 900;
#endif
        // The first HWND can be created on a 200% monitor before moving to a
        // 150% monitor. This track minimum keeps the intended 640-DIP narrow
        // shell reachable across that per-monitor DPI transition.
        _window.MinimumWidth = 480;
        _window.MinimumHeight = 560;
        _window.HandlerChanged += OnHandlerChanged;
        _window.Destroying += OnWindowDestroying;
        TryInitializeNativeWindow();
    }

    public void TogglePinned()
    {
        if (_appWindow?.Presenter is not OverlappedPresenter presenter) return;
        presenter.IsAlwaysOnTop = !presenter.IsAlwaysOnTop;
        _isPinned = presenter.IsAlwaysOnTop;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RequestExit()
    {
        if (_windowHandle == 0) return;
        _exitRequested = true;
        if (!PostMessage(_windowHandle, WindowClose, 0, 0))
        {
            _exitRequested = false;
        }
    }

    private void OnHandlerChanged(object? sender, EventArgs eventArgs) => TryInitializeNativeWindow();

    private void TryInitializeNativeWindow()
    {
        if (_window?.Handler?.PlatformView is not Microsoft.UI.Xaml.Window nativeWindow) return;
        var windowHandle = WindowNative.GetWindowHandle(nativeWindow);
        _windowHandle = windowHandle;
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(windowHandle);
        if (_appWindow is not null)
        {
            _appWindow.Closing -= OnAppWindowClosing;
        }
        _appWindow = AppWindow.GetFromWindowId(windowId);
        _appWindow.Closing += OnAppWindowClosing;
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            _isPinned = presenter.IsAlwaysOnTop;
        }

#if DEBUG
        if (NativeShellPreviewSession.IsRequested && !_previewPlacementApplied)
        {
            _previewPlacementApplied = TryPlacePreviewOnSecondaryDisplay(
                windowHandle,
                nativeWindow.DispatcherQueue);
        }
#endif

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

#if DEBUG
    private bool TryPlacePreviewOnSecondaryDisplay(
        nint windowHandle,
        Microsoft.UI.Dispatching.DispatcherQueue dispatcherQueue)
    {
        try
        {
            nint secondaryMonitor = 0;
            NativeMonitorInfo secondaryInfo = default;
            MonitorEnumProcedure callback = (monitor, _, _, _) =>
            {
                var info = new NativeMonitorInfo
                {
                    Size = (uint)Marshal.SizeOf<NativeMonitorInfo>()
                };
                if (GetMonitorInfo(monitor, ref info) &&
                    (info.Flags & MonitorInfoPrimary) == 0)
                {
                    secondaryMonitor = monitor;
                    secondaryInfo = info;
                    return false;
                }

                return true;
            };
            _ = EnumDisplayMonitors(0, 0, callback, 0);
            GC.KeepAlive(callback);
            if (secondaryMonitor == 0) return false;

            var workArea = secondaryInfo.WorkArea;
            if (!SetWindowPos(
                    windowHandle,
                    0,
                    workArea.Left,
                    workArea.Top,
                    0,
                    0,
                    SetWindowPosNoSize | SetWindowPosNoZOrder | SetWindowPosNoActivate))
            {
                return false;
            }

            if (GetScaleFactorForMonitor(secondaryMonitor, out var scaleFactor) != 0 ||
                scaleFactor <= 0)
            {
                return true;
            }

            var targetDpi = checked((uint)Math.Round(
                96d * scaleFactor / 100d,
                MidpointRounding.AwayFromZero));
            _previewPlacementTimer?.Stop();
            var timer = dispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(1);
            timer.IsRepeating = false;
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (ReferenceEquals(_previewPlacementTimer, timer))
                {
                    _previewPlacementTimer = null;
                }

                TryResizePreviewWindow(
                    windowHandle,
                    workArea,
                    targetDpi,
                    NativeShellPreviewSession.RequestedWidth,
                    NativeShellPreviewSession.RequestedHeight);
            };
            _previewPlacementTimer = timer;
            timer.Start();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void TryResizePreviewWindow(
        nint windowHandle,
        NativeRectangle workArea,
        uint dpi,
        int widthDip,
        int heightDip)
    {
        try
        {
            var width = ScaleDipToPixels(widthDip, dpi);
            var height = ScaleDipToPixels(heightDip, dpi);
            var availableWidth = workArea.Right - workArea.Left;
            var availableHeight = workArea.Bottom - workArea.Top;
            var x = workArea.Left + Math.Max(0, (availableWidth - width) / 2);
            var y = workArea.Top + Math.Max(0, (availableHeight - height) / 2);
            _ = SetWindowPos(
                windowHandle,
                0,
                x,
                y,
                width,
                height,
                SetWindowPosNoZOrder | SetWindowPosNoActivate);
        }
        catch (Exception)
        {
            // Preview placement is best effort; production window startup must continue.
        }
    }

    private const uint MonitorInfoPrimary = 0x00000001;
    private const uint SetWindowPosNoSize = 0x0001;
    private const uint SetWindowPosNoZOrder = 0x0004;
    private const uint SetWindowPosNoActivate = 0x0010;

    private delegate bool MonitorEnumProcedure(
        nint monitor,
        nint monitorDeviceContext,
        nint monitorRectangle,
        nint data);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMonitorInfo
    {
        public uint Size;
        public NativeRectangle MonitorArea;
        public NativeRectangle WorkArea;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(
        nint deviceContext,
        nint clipRectangle,
        MonitorEnumProcedure callback,
        nint data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref NativeMonitorInfo monitorInfo);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint windowHandle,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetScaleFactorForMonitor(nint monitor, out int scaleFactor);
#endif

    internal static int ScaleDipToPixels(int dip, uint dpi) =>
        checked((int)Math.Round(dip * dpi / 96d, MidpointRounding.AwayFromZero));

    internal static bool IsForegroundWindow(nint windowHandle, nint foregroundWindow, bool isMinimized) =>
        windowHandle != 0 && windowHandle == foregroundWindow && !isMinimized;

    internal static bool ShouldCloseToTray(bool isNativePreviewRequested) => !isNativePreviewRequested;

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs eventArgs)
    {
        if (_exitRequested) return;
#if DEBUG
        if (!ShouldCloseToTray(NativeShellPreviewSession.IsRequested)) return;
#endif
        eventArgs.Cancel = true;
        sender.Hide();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnWindowDestroying(object? sender, EventArgs eventArgs)
    {
        if (_window is not null)
        {
            _window.HandlerChanged -= OnHandlerChanged;
            _window.Destroying -= OnWindowDestroying;
        }

        if (_appWindow is not null)
        {
            _appWindow.Closing -= OnAppWindowClosing;
            _appWindow = null;
        }
        _windowHandle = 0;
        _window = null;
        _exitRequested = false;
#if DEBUG
        _previewPlacementApplied = false;
        _previewPlacementTimer?.Stop();
        _previewPlacementTimer = null;
#endif
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint windowHandle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(
        nint windowHandle,
        uint message,
        nint wordParameter,
        nint longParameter);
}
