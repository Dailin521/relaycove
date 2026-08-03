using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RelayCove.Client.Accounts;
using RelayCove.Client.Sync;
using RelayCove.Shared.Messages;

namespace RelayCove.Client.Tests.Accounts;

public sealed class ClientAutomaticSyncSchedulerTests
{
    [Fact]
    public async Task UpdateActivity_WhenForegroundRises_RequestsExactlyOneWindowSyncPerEdge()
    {
        var sync = new FakeSyncCoordinator();
        await using var scheduler = CreateScheduler(sync);

        scheduler.UpdateActivity(mainWindowForeground: true);
        scheduler.Start(initialMainWindowForeground: true);
        scheduler.UpdateActivity(mainWindowForeground: true);
        scheduler.UpdateActivity(mainWindowForeground: false);
        scheduler.UpdateActivity(mainWindowForeground: false);
        scheduler.UpdateActivity(mainWindowForeground: true);
        scheduler.UpdateActivity(mainWindowForeground: true);
        scheduler.UpdateActivity(mainWindowForeground: false);
        scheduler.UpdateActivity(mainWindowForeground: true);

        Assert.Equal(
            [SyncReason.WindowActivated, SyncReason.WindowActivated],
            sync.Reasons);
    }

    [Fact]
    public async Task PeriodicLoop_WhenSyncFails_RequestsSequentiallyAndContinues()
    {
        var firstRequest = new TaskCompletionSource<ClientSyncRunOutcome>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var requestCount = 0;
        var sync = new FakeSyncCoordinator
        {
            TriggerAction = (reason, _) => Interlocked.Increment(ref requestCount) switch
            {
                1 => firstRequest.Task,
                2 => Task.FromException<ClientSyncRunOutcome>(
                    new IOException("classified clock-independent detail")),
                _ => Task.FromResult(Completed(reason)),
            },
        };
        var clock = new ManualDelay();
        var logger = new RecordingLogger<ClientAutomaticSyncScheduler>();
        await using var scheduler = new ClientAutomaticSyncScheduler(
            sync,
            logger,
            delayAsync: clock.DelayAsync);
        scheduler.Start(initialMainWindowForeground: false);

        await clock.ReleaseNextAsync();
        await sync.WaitForRequestCountAsync(1);
        Assert.Equal(1, clock.CallCount);

        firstRequest.TrySetResult(new ClientSyncRunOutcome(
            ClientSyncRunStatus.TransientFailure,
            SyncReason.Periodic,
            RoundsExecuted: 1));
        await clock.ReleaseNextAsync();
        await sync.WaitForRequestCountAsync(2);
        await clock.ReleaseNextAsync();
        await sync.WaitForRequestCountAsync(3);

        Assert.Equal(
            [SyncReason.Periodic, SyncReason.Periodic, SyncReason.Periodic],
            sync.Reasons);
        Assert.All(
            clock.RequestedDelays,
            delay => Assert.Equal(
                ClientAutomaticSyncScheduler.DefaultPeriodicInterval,
                delay));
        Assert.Contains(
            logger.Entries,
            entry => entry.Contains(
                nameof(ClientSyncRunStatus.TransientFailure),
                StringComparison.Ordinal));
        Assert.Contains(
            logger.Entries,
            entry => entry.Contains(nameof(IOException), StringComparison.Ordinal));
        Assert.DoesNotContain(
            logger.Entries,
            entry => entry.Contains("classified", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DisposeAsync_WhenPeriodicRequestIsPending_CancelsWaitAndStopsScheduler()
    {
        var pendingRequest = new TaskCompletionSource<ClientSyncRunOutcome>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var sync = new FakeSyncCoordinator
        {
            TriggerAction = (_, _) => pendingRequest.Task,
        };
        var clock = new ManualDelay();
        var scheduler = new ClientAutomaticSyncScheduler(
            sync,
            NullLogger<ClientAutomaticSyncScheduler>.Instance,
            ClientAutomaticSyncScheduler.DefaultPeriodicInterval,
            clock.DelayAsync);
        scheduler.Start(initialMainWindowForeground: false);
        await clock.ReleaseNextAsync();
        await sync.WaitForRequestCountAsync(1);

        await scheduler.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal([SyncReason.Periodic], sync.Reasons);
        Assert.Equal(0, sync.DisposeCount);
        Assert.Throws<ObjectDisposedException>(() =>
            scheduler.UpdateActivity(mainWindowForeground: true));
        pendingRequest.TrySetResult(Completed(SyncReason.Periodic));
    }

    [Fact]
    public async Task PeriodicLoop_WhenClockFails_LogsTypeWithoutDetailAndStops()
    {
        var sync = new FakeSyncCoordinator();
        var logger = new RecordingLogger<ClientAutomaticSyncScheduler>();
        await using var scheduler = new ClientAutomaticSyncScheduler(
            sync,
            logger,
            delayAsync: (_, _) => throw new IOException("classified clock detail"));

        scheduler.Start(initialMainWindowForeground: false);
        await WaitUntilAsync(() => logger.Entries.Count != 0);

        Assert.Empty(sync.Reasons);
        Assert.Contains(nameof(IOException), logger.Entries[0], StringComparison.Ordinal);
        Assert.DoesNotContain("classified", logger.Entries[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Start_WhenRepeatedOrDisposed_RejectsInvalidLifecycle()
    {
        var scheduler = CreateScheduler(new FakeSyncCoordinator());
        scheduler.Start(initialMainWindowForeground: false);

        Assert.Throws<InvalidOperationException>(() =>
            scheduler.Start(initialMainWindowForeground: false));
        await scheduler.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() =>
            scheduler.Start(initialMainWindowForeground: false));
    }

    private static ClientAutomaticSyncScheduler CreateScheduler(
        IClientAccountSyncCoordinator syncCoordinator) =>
        new(
            syncCoordinator,
            NullLogger<ClientAutomaticSyncScheduler>.Instance,
            delayAsync: static (_, cancellationToken) =>
                Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));

    private static ClientSyncRunOutcome Completed(SyncReason reason) =>
        new(ClientSyncRunStatus.Completed, reason, RoundsExecuted: 1);

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
        }
    }

    private sealed class ManualDelay
    {
        private readonly ConcurrentQueue<TaskCompletionSource> waits = [];
        private readonly ConcurrentQueue<TimeSpan> requestedDelays = [];
        private readonly SemaphoreSlim available = new(0);
        private int callCount;

        public int CallCount => Volatile.Read(ref callCount);

        public IReadOnlyList<TimeSpan> RequestedDelays => requestedDelays.ToArray();

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var registration = cancellationToken.Register(
                static state =>
                {
                    var pair = ((TaskCompletionSource Completion, CancellationToken Token))state!;
                    pair.Completion.TrySetCanceled(pair.Token);
                },
                (completion, cancellationToken));
            _ = completion.Task.ContinueWith(
                static (_, state) => ((CancellationTokenRegistration)state!).Dispose(),
                registration,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            requestedDelays.Enqueue(delay);
            waits.Enqueue(completion);
            Interlocked.Increment(ref callCount);
            available.Release();
            return completion.Task;
        }

        public async Task ReleaseNextAsync()
        {
            Assert.True(await available.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(waits.TryDequeue(out var completion));
            completion.TrySetResult();
        }
    }

    private sealed class FakeSyncCoordinator : IClientAccountSyncCoordinator
    {
        private readonly ConcurrentQueue<SyncReason> reasons = [];
        private readonly SemaphoreSlim requestAvailable = new(0);
        private int disposeCount;

        public Func<SyncReason, CancellationToken, Task<ClientSyncRunOutcome>>? TriggerAction
        {
            get;
            init;
        }

        public IReadOnlyList<SyncReason> Reasons => reasons.ToArray();

        public int DisposeCount => Volatile.Read(ref disposeCount);

        public Task<ClientSyncRunOutcome> TriggerAsync(
            SyncReason reason,
            CancellationToken cancellationToken = default)
        {
            reasons.Enqueue(reason);
            requestAvailable.Release();
            var request = TriggerAction?.Invoke(reason, cancellationToken) ??
                Task.FromResult(Completed(reason));
            return cancellationToken.CanBeCanceled
                ? request.WaitAsync(cancellationToken)
                : request;
        }

        public async Task WaitForRequestCountAsync(int expected)
        {
            while (Reasons.Count < expected)
            {
                Assert.True(await requestAvailable.WaitAsync(TimeSpan.FromSeconds(5)));
            }
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref disposeCount);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        private readonly ConcurrentQueue<string> entries = [];

        public IReadOnlyList<string> Entries => entries.ToArray();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            entries.Enqueue(formatter(state, exception));
    }
}
