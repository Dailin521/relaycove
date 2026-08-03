using Microsoft.Extensions.Logging;
using RelayCove.Client.Sync;
using RelayCove.Shared.Messages;

namespace RelayCove.Client.Accounts;

internal sealed class ClientAutomaticSyncScheduler : IAsyncDisposable
{
    internal static readonly TimeSpan DefaultPeriodicInterval = TimeSpan.FromMinutes(5);

    private readonly object stateGate = new();
    private readonly IClientAccountSyncCoordinator syncCoordinator;
    private readonly ILogger<ClientAutomaticSyncScheduler> logger;
    private readonly TimeSpan periodicInterval;
    private readonly Func<TimeSpan, CancellationToken, Task> delayAsync;
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private readonly HashSet<Task> observers = [];
    private Task? periodicLoop;
    private Task? disposeTask;
    private bool isMainWindowForeground;
    private bool started;
    private bool disposed;

    internal ClientAutomaticSyncScheduler(
        IClientAccountSyncCoordinator syncCoordinator,
        ILogger<ClientAutomaticSyncScheduler> logger,
        TimeSpan? periodicInterval = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        this.syncCoordinator = syncCoordinator ??
            throw new ArgumentNullException(nameof(syncCoordinator));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.periodicInterval = periodicInterval ?? DefaultPeriodicInterval;
        if (this.periodicInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(periodicInterval),
                "The periodic sync interval must be positive.");
        }

        this.delayAsync = delayAsync ?? Task.Delay;
    }

    public void Start(bool initialMainWindowForeground)
    {
        lock (stateGate)
        {
            ThrowIfDisposed();
            if (started)
            {
                throw new InvalidOperationException(
                    "The automatic sync scheduler has already started.");
            }

            started = true;
            isMainWindowForeground = initialMainWindowForeground;
            periodicLoop = RunPeriodicLoopAsync(lifetimeCancellation.Token);
        }
    }

    public void UpdateActivity(bool mainWindowForeground)
    {
        var requestWindowActivated = false;
        lock (stateGate)
        {
            ThrowIfDisposed();
            requestWindowActivated = started &&
                !isMainWindowForeground &&
                mainWindowForeground;
            isMainWindowForeground = mainWindowForeground;
        }

        if (requestWindowActivated)
        {
            RequestSync(SyncReason.WindowActivated);
        }
    }

    public ValueTask DisposeAsync()
    {
        Task sharedDispose;
        var completeDispose = false;
        TaskCompletionSource? completion = null;
        lock (stateGate)
        {
            if (disposeTask is null)
            {
                disposed = true;
                completion = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                disposeTask = completion.Task;
                completeDispose = true;
            }

            sharedDispose = disposeTask;
        }

        if (completeDispose)
        {
            _ = CompleteDisposeAsync(completion!);
        }

        return new ValueTask(sharedDispose);
    }

    private async Task RunPeriodicLoopAsync(CancellationToken cancellationToken)
    {
        await Task.Yield();
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await delayAsync(periodicInterval, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    "The periodic sync clock failed; errorType={ErrorType}.",
                    exception.GetType().Name);
                return;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var observer = RequestSync(SyncReason.Periodic);
            if (observer is not null)
            {
                await observer.ConfigureAwait(false);
            }
        }
    }

    private Task? RequestSync(SyncReason reason)
    {
        lock (stateGate)
        {
            if (disposed)
            {
                return null;
            }

            Task<ClientSyncRunOutcome> request;
            try
            {
                request = syncCoordinator.TriggerAsync(
                    reason,
                    lifetimeCancellation.Token);
            }
            catch (ObjectDisposedException)
            {
                return null;
            }
            catch (Exception exception)
            {
                LogFailure(reason, exception);
                return null;
            }

            var observer = ObserveAsync(request, reason, lifetimeCancellation.Token);
            observers.Add(observer);
            _ = observer.ContinueWith(
                static (completed, state) =>
                    ((ClientAutomaticSyncScheduler)state!).RemoveObserver(completed),
                this,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return observer;
        }
    }

    private async Task ObserveAsync(
        Task<ClientSyncRunOutcome> request,
        SyncReason reason,
        CancellationToken cancellationToken)
    {
        try
        {
            var outcome = await request.ConfigureAwait(false);
            if (outcome.Status is not ClientSyncRunStatus.Completed and
                not ClientSyncRunStatus.Canceled)
            {
                logger.LogWarning(
                    "Automatic account sync did not complete; " +
                    "reason={Reason}; status={Status}.",
                    reason,
                    outcome.Status);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception exception)
        {
            LogFailure(reason, exception);
        }
    }

    private async Task CompleteDisposeAsync(TaskCompletionSource completion)
    {
        try
        {
            lifetimeCancellation.Cancel();
            Task? loop;
            Task[] activeObservers;
            lock (stateGate)
            {
                loop = periodicLoop;
                activeObservers = [.. observers];
            }

            if (loop is not null)
            {
                await loop.ConfigureAwait(false);
            }

            if (activeObservers.Length != 0)
            {
                await Task.WhenAll(activeObservers).ConfigureAwait(false);
            }

            lifetimeCancellation.Dispose();
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private void RemoveObserver(Task observer)
    {
        lock (stateGate)
        {
            observers.Remove(observer);
        }
    }

    private void LogFailure(SyncReason reason, Exception exception)
    {
        logger.LogWarning(
            "Automatic account sync failed; reason={Reason}; errorType={ErrorType}.",
            reason,
            exception.GetType().Name);
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(disposed, this);
}
