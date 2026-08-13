using Microsoft.UI.Windowing;
using RelayCove.App.Services;
using WinRT.Interop;

namespace RelayCove.App.Platforms.Windows;

public sealed class WindowsWindowShellAdapter : IWindowShellAdapter
{
    private Window? _window;
    private AppWindow? _appWindow;
    private bool _isPinned;

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
        _window.MinimumWidth = 720;
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

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnWindowDestroying(object? sender, EventArgs eventArgs)
    {
        if (_window is not null)
        {
            _window.HandlerChanged -= OnHandlerChanged;
            _window.Destroying -= OnWindowDestroying;
        }

        _appWindow = null;
        _window = null;
    }
}
