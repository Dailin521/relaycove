using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using RelayCove.App.Platforms.Windows;
using WinRT.Interop;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace RelayCove.App.WinUI;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : MauiWinUIApplication
{
    private const int ShowWindowRestore = 9;
    private AppInstance? _mainInstance;
    private DispatcherQueue? _dispatcherQueue;
    private DispatcherQueueTimer? _activationRetryTimer;
    private int _activationRetryCount;

    /// <summary>
    /// Initializes the singleton application object.  This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
        this.InitializeComponent();
    }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            var registeredInstance = AppInstance.FindOrRegisterForKey(RichChatInstancePolicy.InstanceKey);
            if (RichChatInstancePolicy.ShouldRedirect(registeredInstance.IsCurrent))
            {
                await registeredInstance.RedirectActivationToAsync(activation);
                Environment.Exit(0);
                return;
            }

            _mainInstance = registeredInstance;
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
            _mainInstance.Activated += OnInstanceActivated;
            base.OnLaunched(args);
        }
        catch
        {
            // Single-instance enforcement fails closed so a second tray owner
            // can never start after an AppLifecycle failure.
            Environment.Exit(1);
        }
    }

    private void OnInstanceActivated(object? sender, AppActivationArguments args)
    {
        _dispatcherQueue?.TryEnqueue(BeginMainWindowActivation);
    }

    private void BeginMainWindowActivation()
    {
        if (TryActivateMainWindow()) return;
        _activationRetryCount = 0;
        _activationRetryTimer ??= CreateActivationRetryTimer();
        _activationRetryTimer.Stop();
        _activationRetryTimer.Start();
    }

    private DispatcherQueueTimer CreateActivationRetryTimer()
    {
        var timer = (_dispatcherQueue ?? DispatcherQueue.GetForCurrentThread()).CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(50);
        timer.IsRepeating = true;
        timer.Tick += OnActivationRetryTimerTick;
        return timer;
    }

    private void OnActivationRetryTimerTick(DispatcherQueueTimer sender, object args)
    {
        _activationRetryCount++;
        if (TryActivateMainWindow() || _activationRetryCount >= 20) sender.Stop();
    }

    private static bool TryActivateMainWindow()
    {
        var mauiWindow = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault();
        if (mauiWindow?.Handler?.PlatformView is not Microsoft.UI.Xaml.Window nativeWindow) return false;

        var windowHandle = WindowNative.GetWindowHandle(nativeWindow);
        if (windowHandle == 0) return false;
        _ = ShowWindow(windowHandle, ShowWindowRestore);
        nativeWindow.Activate();
        _ = SetForegroundWindow(windowHandle);
        return true;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint windowHandle, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint windowHandle);
}
