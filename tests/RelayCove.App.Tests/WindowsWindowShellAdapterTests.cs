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
}
