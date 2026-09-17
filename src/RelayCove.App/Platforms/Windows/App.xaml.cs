using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using RelayCove.App.Platforms.Windows;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace RelayCove.App.WinUI;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : MauiWinUIApplication
{
    private static readonly TimeSpan ActivationTransferTimeout = TimeSpan.FromSeconds(5);
    private const int ActivationRetryLimit = 100;
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
        WindowsAppNotificationService.PrepareProcessNotificationIdentity();
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
                WindowsLifecycleDiagnostics.Write("activation-redirect-requested");
                if (await TryRedirectActivationAsync(registeredInstance, activation))
                {
                    WindowsLifecycleDiagnostics.Write("activation-redirected");
                    Environment.Exit(0);
                    return;
                }

                registeredInstance = AppInstance.FindOrRegisterForKey(RichChatInstancePolicy.InstanceKey);
                if (RichChatInstancePolicy.ShouldRedirect(registeredInstance.IsCurrent))
                {
                    WindowsLifecycleDiagnostics.Write("activation-redirect-failed");
                    ShowActivationFailure();
                    Environment.Exit(1);
                    return;
                }

                WindowsLifecycleDiagnostics.Write("activation-instance-reclaimed");
            }

            _mainInstance = registeredInstance;
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
            _mainInstance.Activated += OnInstanceActivated;
            MouseOnlyNavigation.Enable();
            base.OnLaunched(args);
        }
        catch (Exception exception)
        {
            // Single-instance enforcement fails closed so a second tray owner
            // can never start after an AppLifecycle failure.
            WindowsLifecycleDiagnostics.Write($"launch-failed:{exception.GetType().Name}");
            Environment.Exit(1);
        }
    }

    private static async Task<bool> TryRedirectActivationAsync(AppInstance instance, AppActivationArguments activation)
    {
        try
        {
            await instance.RedirectActivationToAsync(activation).AsTask().WaitAsync(ActivationTransferTimeout);
            return true;
        }
        catch (TimeoutException)
        {
            WindowsLifecycleDiagnostics.Write("activation-redirect-timed-out");
            return false;
        }
        catch (Exception exception)
        {
            WindowsLifecycleDiagnostics.Write($"activation-redirect-failed:{exception.GetType().Name}");
            return false;
        }
    }

    private void OnInstanceActivated(object? sender, AppActivationArguments args)
    {
        if (WindowsApplicationLifetime.IsExitRequested)
        {
            WindowsLifecycleDiagnostics.Write("activation-ignored-during-exit");
            return;
        }
        _dispatcherQueue?.TryEnqueue(BeginMainWindowActivation);
    }

    private void BeginMainWindowActivation()
    {
        if (WindowsApplicationLifetime.IsExitRequested) return;
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
        if (TryActivateMainWindow())
        {
            WindowsLifecycleDiagnostics.Write("activation-window-restored");
            sender.Stop();
            return;
        }

        if (_activationRetryCount >= ActivationRetryLimit)
        {
            WindowsLifecycleDiagnostics.Write("activation-window-unavailable");
            sender.Stop();
        }
    }

    private static bool TryActivateMainWindow() => WindowsMainWindowActivator.TryActivate();

    private static void ShowActivationFailure() => _ = MessageBox(
        0,
        "RichChat 正在退出或未响应，请稍后重试。",
        "RichChat",
        0);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int MessageBox(nint windowHandle, string text, string caption, uint type);
}
