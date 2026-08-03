using RelayCove.Client.Activation;

namespace RelayCove.Client.Tests.Activation;

public sealed class ClientApplicationShutdownTests
{
    [Fact]
    public async Task RunBlockingOperationAsync_UsesDedicatedBackgroundThread()
    {
        var callerThreadId = Environment.CurrentManagedThreadId;
        var operationThreadId = callerThreadId;

        await ClientApplicationShutdown.RunBlockingOperationAsync(() =>
        {
            operationThreadId = Environment.CurrentManagedThreadId;
        });

        Assert.NotEqual(callerThreadId, operationThreadId);
    }

    [Fact]
    public async Task StopPrimaryAsync_WhenNotificationStopIsPending_HoldsInstanceKeyUntilCompletion()
    {
        var events = new List<string>();
        var notificationStop = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var stop = ClientApplicationShutdown.StopPrimaryAsync(
            () => events.Add("dispatcher"),
            () => events.Add("router"),
            async () =>
            {
                events.Add("notification-start");
                await notificationStop.Task;
                events.Add("notification-complete");
            },
            () => events.Add("instance-key"));

        Assert.False(stop.IsCompleted);
        Assert.Equal(
            ["dispatcher", "router", "notification-start"],
            events);

        notificationStop.SetResult();
        await stop;

        Assert.Equal(
            [
                "dispatcher",
                "router",
                "notification-start",
                "notification-complete",
                "instance-key",
            ],
            events);
    }

    [Fact]
    public async Task StopPrimaryAsync_WhenNotificationStopFails_StillReleasesInstanceKeyLast()
    {
        var events = new List<string>();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ClientApplicationShutdown.StopPrimaryAsync(
                () => events.Add("dispatcher"),
                () => events.Add("router"),
                () =>
                {
                    events.Add("notification");
                    throw new InvalidOperationException("expected");
                },
                () => events.Add("instance-key")));

        Assert.Equal(
            ["dispatcher", "router", "notification", "instance-key"],
            events);
    }
}
