using RelayCove.App.Platforms.Windows;
using Windows.Graphics;

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

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void ShouldCloseToTray_WhenNativePreviewIsRunning_PreservesPreviewShutdown(
        bool isNativePreviewRequested,
        bool expected)
    {
        Assert.Equal(
            expected,
            WindowsWindowShellAdapter.ShouldCloseToTray(isNativePreviewRequested));
    }

    [Fact]
    public void ClampWindowBounds_WhenSavedBoundsAreVisible_PreservesBounds()
    {
        var requested = new RectInt32(120, 80, 1280, 800);
        var workArea = new RectInt32(0, 0, 1920, 1040);

        Assert.Equal(requested, WindowsWindowShellAdapter.ClampWindowBounds(requested, workArea));
    }

    [Fact]
    public void ClampWindowBounds_WhenDisplayChanged_KeepsWindowVisible()
    {
        var requested = new RectInt32(2200, -200, 2400, 1200);
        var workArea = new RectInt32(0, 0, 1920, 1040);

        Assert.Equal(
            new RectInt32(0, 0, 1920, 1040),
            WindowsWindowShellAdapter.ClampWindowBounds(requested, workArea));
    }
}
