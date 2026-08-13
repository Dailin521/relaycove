#if DEBUG
using System.Runtime.InteropServices;
#endif
using Microsoft.UI.Windowing;
using RelayCove.App.Services;
using WinRT.Interop;

namespace RelayCove.App.Platforms.Windows;

public sealed class WindowsWindowShellAdapter : IWindowShellAdapter
{
    private Window? _window;
    private AppWindow? _appWindow;
    private bool _isPinned;
#if DEBUG
    private bool _previewPlacementApplied;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _previewPlacementTimer;
#endif

    public event EventHandler? StateChanged;

    public bool IsPinned => _isPinned;

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
        _window.Width = 1440;
        _window.Height = 900;
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

    private void OnHandlerChanged(object? sender, EventArgs eventArgs) => TryInitializeNativeWindow();

    private void TryInitializeNativeWindow()
    {
        if (_window?.Handler?.PlatformView is not Microsoft.UI.Xaml.Window nativeWindow) return;
        var windowHandle = WindowNative.GetWindowHandle(nativeWindow);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(windowHandle);
        _appWindow = AppWindow.GetFromWindowId(windowId);
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

                TryResizePreviewWindow(windowHandle, workArea, targetDpi);
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
        uint dpi)
    {
        try
        {
            var width = ScaleDipToPixels(1440, dpi);
            var height = ScaleDipToPixels(900, dpi);
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

    private void OnWindowDestroying(object? sender, EventArgs eventArgs)
    {
        if (_window is not null)
        {
            _window.HandlerChanged -= OnHandlerChanged;
            _window.Destroying -= OnWindowDestroying;
        }

        _appWindow = null;
        _window = null;
#if DEBUG
        _previewPlacementApplied = false;
        _previewPlacementTimer?.Stop();
        _previewPlacementTimer = null;
#endif
    }
}
