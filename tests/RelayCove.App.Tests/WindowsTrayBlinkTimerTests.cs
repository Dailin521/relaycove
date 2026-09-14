using RelayCove.App.Platforms.Windows;

namespace RelayCove.App.Tests;

public sealed class WindowsTrayBlinkTimerTests
{
    [Fact]
    public void Start_WhenOnlyTrayMessageWindowExists_DeliversTicksWithoutUiDispatcher()
    {
        var ticks = 0;
        var timer = new WindowsTrayBlinkTimer(
            () => ticks++,
            (window, id, interval) =>
            {
                Assert.Equal((nint)42, window);
                Assert.Equal(500u, interval);
                return id;
            },
            (_, _) => { });

        timer.Start(42);

        Assert.True(timer.IsRunning);
        Assert.True(timer.TryHandleMessage(42, 0x0113, 1));
        Assert.True(timer.TryHandleMessage(42, 0x0113, 1));
        Assert.Equal(2, ticks);
    }

    [Fact]
    public void Start_WhenRepeatedBeforeFirstTick_DoesNotPostponeBlinkDeadline()
    {
        uint now = 0;
        uint deadline = 0;
        var starts = 0;
        var ticks = 0;
        var timer = new WindowsTrayBlinkTimer(
            () => ticks++,
            (_, id, interval) =>
            {
                starts++;
                deadline = now + interval;
                return id;
            },
            (_, _) => { });

        timer.Start(42);
        for (now = 100; now < 500; now += 100) timer.Start(42);

        Assert.Equal(1, starts);
        Assert.Equal(now, deadline);
        Assert.True(timer.TryHandleMessage(42, 0x0113, 1));
        Assert.Equal(1, ticks);
    }

    [Theory]
    [InlineData(42, 0x0200, 1)]
    [InlineData(42, 0x0113, 2)]
    [InlineData(43, 0x0113, 1)]
    public void TryHandleMessage_WhenMessageDoesNotBelongToTimer_DoesNotBlink(
        int window, uint message, int timerId)
    {
        var ticks = 0;
        var timer = new WindowsTrayBlinkTimer(() => ticks++, (_, id, _) => id, (_, _) => { });
        timer.Start(42);

        Assert.False(timer.TryHandleMessage(window, message, timerId));
        Assert.Equal(0, ticks);
    }

    [Fact]
    public void Stop_WhenUnreadIsAcknowledged_IgnoresQueuedTicksUntilResumed()
    {
        var ticks = 0;
        var starts = 0;
        var stops = 0;
        var timer = new WindowsTrayBlinkTimer(
            () => ticks++,
            (_, id, _) => { starts++; return id; },
            (window, id) =>
            {
                Assert.Equal((nint)42, window);
                Assert.Equal((nuint)1, id);
                stops++;
            });
        timer.Start(42);

        timer.Stop();
        timer.Stop();

        Assert.False(timer.IsRunning);
        Assert.False(timer.TryHandleMessage(42, 0x0113, 1));
        Assert.Equal(0, ticks);
        Assert.Equal(1, stops);

        timer.Start(42);
        Assert.Equal(2, starts);
        Assert.True(timer.TryHandleMessage(42, 0x0113, 1));
        Assert.Equal(1, ticks);
    }

    [Fact]
    public void Start_WhenNativeTimerCreationFails_AllowsLaterAttempt()
    {
        var starts = 0;
        var timer = new WindowsTrayBlinkTimer(
            () => { },
            (_, id, _) => ++starts == 1 ? 0 : id,
            (_, _) => { });

        timer.Start(42);
        Assert.False(timer.IsRunning);
        timer.Start(42);
        Assert.True(timer.IsRunning);
        Assert.Equal(2, starts);
    }

    [Fact]
    public void Start_WhenMessageWindowDoesNotExist_DoesNotCreateThreadTimer()
    {
        var timer = new WindowsTrayBlinkTimer(
            () => { },
            (_, _, _) => throw new InvalidOperationException("Unexpected native timer."),
            (_, _) => { });

        timer.Start(0);

        Assert.False(timer.IsRunning);
    }
}
