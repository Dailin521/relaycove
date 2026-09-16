using RelayCove.App.Platforms.Windows;

namespace RelayCove.App.Tests;

public sealed class WindowsUserActivitySourceTests
{
    [Theory]
    [InlineData(0u, 299999u, false)]
    [InlineData(0u, 300000u, true)]
    [InlineData(0u, 360000u, true)]
    [InlineData(360000u, 360001u, false)]
    [InlineData(uint.MaxValue - 999u, 1000u, false)]
    [InlineData(uint.MaxValue - 299999u, 0u, true)]
    public void ResolveIdle_WhenSystemInputAgeChanges_UsesFiveMinutesAcrossTickWrap(uint lastInput, uint now, bool expected)
    {
        Assert.Equal(expected, WindowsUserActivitySource.ResolveIdle(lastInput, now));
    }

    [Fact]
    public void ResolveIdle_WhenWindowsCannotReadLastInput_ReturnsUnknown()
    {
        Assert.Null(WindowsUserActivitySource.ResolveIdle(null, 300000));
    }
}
