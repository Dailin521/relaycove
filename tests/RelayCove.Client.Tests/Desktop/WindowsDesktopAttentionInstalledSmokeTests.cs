using System.Globalization;
using System.Runtime.InteropServices;
using RelayCove.Client.Desktop;

namespace RelayCove.Client.Tests.Desktop;

public sealed class WindowsDesktopAttentionInstalledSmokeTests
{
    private const string WindowHandleEnvironmentVariable =
        "RELAYCOVE_DESKTOP_ATTENTION_SMOKE_HANDLE";

    [Fact]
    public async Task NativeAdapter_WhenExplicitlyEnabled_PlaysSoundAndStartsThenStopsFlash()
    {
        var rawHandle = Environment.GetEnvironmentVariable(
            WindowHandleEnvironmentVariable);
        if (!long.TryParse(
                rawHandle,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var handleValue))
        {
            return;
        }

        var windowHandle = (nint)handleValue;
        Assert.NotEqual(nint.Zero, windowHandle);
        Assert.True(IsWindow(windowHandle));
        var native = new WindowsDesktopAttentionNative();

        Assert.True(native.PlayNotificationSound());
        native.StartTaskbarFlash(windowHandle);
        await Task.Delay(500);
        native.StopTaskbarFlash(windowHandle);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint windowHandle);
}
