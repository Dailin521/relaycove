using RelayCove.App.Platforms.Windows;

namespace RelayCove.App.Tests;

public sealed class WindowsWindowShellAdapterTests
{
    [Theory]
    [InlineData(1440, 96, 1440)]
    [InlineData(1440, 144, 2160)]
    [InlineData(900, 192, 1800)]
    public void ScaleDipToPixels_WhenDisplayDpiChanges_PreservesLogicalSize(
        int dip,
        uint dpi,
        int expectedPixels)
    {
        var pixels = WindowsWindowShellAdapter.ScaleDipToPixels(dip, dpi);

        Assert.Equal(expectedPixels, pixels);
    }

    [Theory]
    [InlineData(0, 0, false, false)]
    [InlineData(10, 20, false, false)]
    [InlineData(10, 10, true, false)]
    [InlineData(10, 10, false, true)]
    public void IsForegroundWindow_WhenWindowIsHoveredOrMinimized_RequiresRealForeground(
        long windowHandle,
        long foregroundWindow,
        bool isMinimized,
        bool expected)
    {
        Assert.Equal(expected, WindowsWindowShellAdapter.IsForegroundWindow(
            (nint)windowHandle,
            (nint)foregroundWindow,
            isMinimized));
    }
}
