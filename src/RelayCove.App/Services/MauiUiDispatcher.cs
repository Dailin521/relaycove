using Microsoft.UI.Dispatching;

namespace RelayCove.App.Services;

public sealed class MauiUiDispatcher : IUiDispatcher
{
    public Task YieldToRenderAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var queue = DispatcherQueue.GetForCurrentThread();
        if (queue is null) return Task.CompletedTask;

        // A normal-priority Task.Yield continuation can run before WinUI paints.
        // Let pending layout, rendering and input run ahead of content loading.
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!queue.TryEnqueue(DispatcherQueuePriority.Low, () => ready.TrySetResult()))
            throw new InvalidOperationException("The UI dispatcher is unavailable.");
        return ready.Task.WaitAsync(cancellationToken);
    }

    public void Dispatch(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (Application.Current?.Dispatcher is { } dispatcher)
        {
            dispatcher.Dispatch(action);
            return;
        }

        action();
    }
}
