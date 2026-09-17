using RelayCove.App.Services;

namespace RelayCove.App.Tests;

public sealed class ApplicationShutdownCoordinatorTests
{
    [Fact]
    public async Task RequestShutdownAsync_WhenCalledTwice_ReleasesAndCleansUpOnlyOnce()
    {
        var released = 0;
        var cleaned = 0;
        var terminated = 0;
        var coordinator = new ApplicationShutdownCoordinator(
            _ =>
            {
                cleaned++;
                return Task.CompletedTask;
            },
            () => released++,
            _ => terminated++,
            forcedExitDelay: TimeSpan.FromSeconds(1));

        var first = coordinator.RequestShutdownAsync(ApplicationShutdownEntryPoint.TrayExit);
        var second = coordinator.RequestShutdownAsync(ApplicationShutdownEntryPoint.WindowDestroyed);
        await Task.WhenAll(first, second);

        Assert.True(coordinator.IsShutdownRequested);
        Assert.Equal(1, released);
        Assert.Equal(1, cleaned);
        Assert.Equal(1, terminated);
    }

    [Fact]
    public async Task RequestShutdownAsync_WhenCleanupThrows_StillTerminatesProcess()
    {
        var terminated = 0;
        var coordinator = new ApplicationShutdownCoordinator(
            _ => Task.FromException(new InvalidOperationException("simulated cleanup failure")),
            () => { },
            _ => terminated++,
            forcedExitDelay: TimeSpan.FromSeconds(1));

        await coordinator.RequestShutdownAsync(ApplicationShutdownEntryPoint.WindowDestroyed);

        Assert.Equal(1, terminated);
    }

    [Fact]
    public async Task RequestShutdownAsync_WhenCleanupNeverCompletes_UsesIndependentForcedExit()
    {
        var terminated = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new ApplicationShutdownCoordinator(
            _ => never.Task,
            () => { },
            code => terminated.TrySetResult(code),
            forcedExitDelay: TimeSpan.Zero);

        var shutdown = coordinator.RequestShutdownAsync(ApplicationShutdownEntryPoint.WindowDestroyed);

        Assert.Equal(0, await terminated.Task.WaitAsync(TimeSpan.FromSeconds(1)));
        never.TrySetResult();
        await shutdown;
    }
}
