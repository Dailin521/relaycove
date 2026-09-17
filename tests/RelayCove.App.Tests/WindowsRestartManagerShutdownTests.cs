using RelayCove.App.Platforms.Windows;

namespace RelayCove.App.Tests;

public sealed class WindowsRestartManagerShutdownTests
{
    [Theory]
    [InlineData(0x11, 0, 1, true, 1, 0)]
    [InlineData(0x16, 1, 1, true, 0, 1)]
    [InlineData(0x16, 0, 1, true, 0, 0)]
    [InlineData(0x10, 0, 0, false, 0, 0)]
    [InlineData(0x11, 0, 0, false, 0, 0)]
    [InlineData(0x16, 1, 0, false, 0, 0)]
    public void SessionMessage_WhenReceived_ExitsOnlyForConfirmedInstallerShutdown(
        uint message, int word, int flags, bool handled, int result, int exits)
    {
        var count = 0;
        Assert.Equal(handled, WindowsRestartManagerShutdown.TryHandleMessage(message, word, flags,
            () => count++, out var actual));
        Assert.Equal((nint)result, actual);
        Assert.Equal(exits, count);
    }
}
