using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using RelayCove.App.Services;
using Windows.Graphics;
using WinRT.Interop;

namespace RelayCove.App.Platforms.Windows;

public sealed class WindowsWindowShellAdapter : IWindowShellAdapter
{
    private const uint WindowClose = 0x0010;
    private static readonly TimeSpan ForcedExitDelay = TimeSpan.FromSeconds(3);
    private const string WindowXKey = "relaycove.window.x";
    private const string WindowYKey = "relaycove.window.y";
    private const string WindowWidthKey = "relaycove.window.width";
    private const string WindowHeightKey = "relaycove.window.height";
    private const int MinimumWindowWidth = 480;
    private const int MinimumWindowHeight = 560;
    private Window? _window;
    private AppWindow? _appWindow;
    private DispatcherQueueTimer? _placementSaveTimer;
    private RectInt32? _lastRestoredBounds;
    private nint _windowHandle;
    private bool _isPinned;
    private bool _exitRequested;
    private bool _isRestoringPlacement;
    private bool _placementRestored;
    private readonly Action<int> _terminateProcess;
    private int _forcedExitScheduled;
#if DEBUG
    private bool _previewPlacementApplied;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _previewPlacementTimer;
#endif

    public event EventHandler? StateChanged;

    public WindowsWindowShellAdapter()
        : this(Environment.Exit)
    {
    }

    internal WindowsWindowShellAdapter(Action<int> terminateProcess)
    {
        _terminateProcess = terminateProcess ?? throw new ArgumentNullException(nameof(terminateProcess));
    }

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
        if (_exitRequested) return;
        _exitRequested = true;
        if (Interlocked.Exchange(ref _forcedExitScheduled, 1) == 0)
        {
            _ = ForceExitAfterDelayAsync(ForcedExitDelay, _terminateProcess);
        }
        if (_windowHandle == 0)
        {
            _terminateProcess(0);
            return;
        }
        if (!PostMessage(_windowHandle, WindowClose, 0, 0))
        {
            _terminateProcess(0);
        }
    }

    internal static async Task ForceExitAfterDelayAsync(TimeSpan delay, Action<int> terminateProcess)
    {
        ArgumentNullException.ThrowIfNull(terminateProcess);
        await Task.Delay(delay).ConfigureAwait(false);
        terminateProcess(0);
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
            _appWindow.Changed -= OnAppWindowChanged;
        }
        _appWindow = AppWindow.GetFromWindowId(windowId);
        _appWindow.Closing += OnAppWindowClosing;
        _appWindow.Changed += OnAppWindowChanged;
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
        else if (!NativeShellPreviewSession.IsRequested && !_placementRestored)
        {
            RestoreWindowPlacement();
            InitializePlacementSaveTimer(nativeWindow.DispatcherQueue);
            _placementRestored = true;
        }
#else
        if (!_placementRestored)
        {
            RestoreWindowPlacement();
            InitializePlacementSaveTimer(nativeWindow.DispatcherQueue);
            _placementRestored = true;
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

    internal static RectInt32 ClampWindowBounds(RectInt32 requested, RectInt32 workArea)
    {
        var minimumWidth = Math.Min(MinimumWindowWidth, workArea.Width);
        var minimumHeight = Math.Min(MinimumWindowHeight, workArea.Height);
        var width = Math.Clamp(requested.Width, minimumWidth, workArea.Width);
        var height = Math.Clamp(requested.Height, minimumHeight, workArea.Height);
        var maximumX = workArea.X + workArea.Width - width;
        var maximumY = workArea.Y + workArea.Height - height;
        var x = Math.Clamp(requested.X, workArea.X, maximumX);
        var y = Math.Clamp(requested.Y, workArea.Y, maximumY);
        return new RectInt32(x, y, width, height);
    }

    private void RestoreWindowPlacement()
    {
        if (_appWindow is null || !TryReadWindowBounds(out var savedBounds)) return;

        try
        {
            _isRestoringPlacement = true;
            var center = new PointInt32(
                savedBounds.X + savedBounds.Width / 2,
                savedBounds.Y + savedBounds.Height / 2);
            var displayArea = DisplayArea.GetFromPoint(center, DisplayAreaFallback.Primary);
            if (displayArea is null) return;

            var restoredBounds = ClampWindowBounds(savedBounds, displayArea.WorkArea);
            _appWindow.MoveAndResize(restoredBounds);
            _lastRestoredBounds = restoredBounds;
        }
        catch (Exception)
        {
            // Corrupt or stale placement must not prevent the app from opening.
        }
        finally
        {
            _isRestoringPlacement = false;
        }
    }

    private void InitializePlacementSaveTimer(DispatcherQueue dispatcherQueue)
    {
        if (_placementSaveTimer is not null) return;
        var timer = dispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(250);
        timer.IsRepeating = false;
        timer.Tick += OnPlacementSaveTimerTick;
        _placementSaveTimer = timer;
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs eventArgs)
    {
        if (_isRestoringPlacement ||
            (!eventArgs.DidPositionChange && !eventArgs.DidSizeChange))
        {
            return;
        }

#if DEBUG
        if (NativeShellPreviewSession.IsRequested) return;
#endif
        if (sender.Presenter is OverlappedPresenter presenter &&
            presenter.State != OverlappedPresenterState.Restored)
        {
            return;
        }

        _lastRestoredBounds = new RectInt32(
            sender.Position.X,
            sender.Position.Y,
            sender.Size.Width,
            sender.Size.Height);
        _placementSaveTimer?.Stop();
        _placementSaveTimer?.Start();
    }

    private void OnPlacementSaveTimerTick(DispatcherQueueTimer sender, object eventArgs)
    {
        sender.Stop();
        SaveWindowPlacement();
    }

    private void SaveWindowPlacement()
    {
#if DEBUG
        if (NativeShellPreviewSession.IsRequested) return;
#endif
        if (_appWindow is not null &&
            (_appWindow.Presenter is not OverlappedPresenter presenter ||
             presenter.State == OverlappedPresenterState.Restored))
        {
            _lastRestoredBounds = new RectInt32(
                _appWindow.Position.X,
                _appWindow.Position.Y,
                _appWindow.Size.Width,
                _appWindow.Size.Height);
        }

        if (_lastRestoredBounds is not { } bounds || bounds.Width <= 0 || bounds.Height <= 0) return;
        Preferences.Default.Set(WindowXKey, bounds.X);
        Preferences.Default.Set(WindowYKey, bounds.Y);
        Preferences.Default.Set(WindowWidthKey, bounds.Width);
        Preferences.Default.Set(WindowHeightKey, bounds.Height);
    }

    private static bool TryReadWindowBounds(out RectInt32 bounds)
    {
        var x = Preferences.Default.Get(WindowXKey, int.MinValue);
        var y = Preferences.Default.Get(WindowYKey, int.MinValue);
        var width = Preferences.Default.Get(WindowWidthKey, 0);
        var height = Preferences.Default.Get(WindowHeightKey, 0);
        if (x == int.MinValue || y == int.MinValue || width <= 0 || height <= 0)
        {
            bounds = default;
            return false;
        }

        bounds = new RectInt32(x, y, width, height);
        return true;
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs eventArgs)
    {
        SaveWindowPlacement();
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
        SaveWindowPlacement();
        if (_window is not null)
        {
            _window.HandlerChanged -= OnHandlerChanged;
            _window.Destroying -= OnWindowDestroying;
        }

        if (_appWindow is not null)
        {
            _appWindow.Closing -= OnAppWindowClosing;
            _appWindow.Changed -= OnAppWindowChanged;
            _appWindow = null;
        }
        if (_placementSaveTimer is not null)
        {
            _placementSaveTimer.Stop();
            _placementSaveTimer.Tick -= OnPlacementSaveTimerTick;
            _placementSaveTimer = null;
        }
        _lastRestoredBounds = null;
        _windowHandle = 0;
        _window = null;
        _exitRequested = false;
        _placementRestored = false;
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
