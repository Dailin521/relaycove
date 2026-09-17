using System.Runtime.InteropServices;
using WinRT.Interop;

namespace RelayCove.App.Platforms.Windows;

internal static class WindowsMainWindowActivator
{
    private const int ShowWindowRestore = 9;

    internal static bool TryActivate()
    {
        if (WindowsApplicationLifetime.IsExitRequested) return false;
        try
        {
            var mauiWindow = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault();
            if (mauiWindow?.Handler?.PlatformView is not Microsoft.UI.Xaml.Window nativeWindow) return false;

            var windowHandle = WindowNative.GetWindowHandle(nativeWindow);
            if (windowHandle == 0 || !IsWindow(windowHandle)) return false;

            _ = ShowWindow(windowHandle, ShowWindowRestore);
            nativeWindow.Activate();
            _ = SetForegroundWindow(windowHandle);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint windowHandle, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint windowHandle);
}
