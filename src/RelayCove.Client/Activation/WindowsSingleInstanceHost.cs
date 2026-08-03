using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace RelayCove.Client.Activation;

internal sealed class WindowsSingleInstanceHost : IDisposable
{
    internal const string InstanceKey = "RelayCove.Client.Primary";
    private const int MaximumRedirectAttempts = 3;

    private readonly object stateGate = new();
    private readonly IWindowsAppInstanceProvider provider;
    private readonly Action<WindowsProcessActivation> activationSink;
    private readonly ILogger<WindowsSingleInstanceHost> logger;
    private readonly TimeSpan redirectTimeout;
    private readonly TimeSpan handoffObservationDelay;
    private readonly Func<uint, CancellationToken, Task> processExitWaiter;
    private readonly Func<TimeSpan, Task> handoffDelay;
    private IWindowsAppInstanceRegistration? registration;
    private Task<WindowsSingleInstanceStartStatus>? startTask;
    private bool disposed;

    public WindowsSingleInstanceHost(
        IWindowsAppInstanceProvider provider,
        Action<WindowsProcessActivation> activationSink,
        ILogger<WindowsSingleInstanceHost> logger,
        TimeSpan? redirectTimeout = null,
        TimeSpan? handoffObservationDelay = null,
        Func<uint, CancellationToken, Task>? processExitWaiter = null,
        Func<TimeSpan, Task>? handoffDelay = null)
    {
        this.provider = provider ?? throw new ArgumentNullException(nameof(provider));
        this.activationSink = activationSink ??
            throw new ArgumentNullException(nameof(activationSink));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.redirectTimeout = redirectTimeout ?? TimeSpan.FromSeconds(10);
        this.handoffObservationDelay = handoffObservationDelay ?? TimeSpan.FromSeconds(1);
        this.processExitWaiter = processExitWaiter ?? WaitForProcessExitAsync;
        this.handoffDelay = handoffDelay ?? Task.Delay;
        if (this.redirectTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(redirectTimeout));
        }

        if (this.handoffObservationDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(handoffObservationDelay));
        }
    }

    public Task<WindowsSingleInstanceStartStatus> StartAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task<WindowsSingleInstanceStartStatus> sharedStart;
        lock (stateGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            startTask ??= CompleteStartAsync();
            sharedStart = startTask;
        }

        return cancellationToken.CanBeCanceled
            ? sharedStart.WaitAsync(cancellationToken)
            : sharedStart;
    }

    public void Dispose()
    {
        IWindowsAppInstanceRegistration? ownedRegistration;
        lock (stateGate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            ownedRegistration = registration;
            registration = null;
        }

        if (ownedRegistration is not null)
        {
            ownedRegistration.Activated -= OnActivated;
            ownedRegistration.Dispose();
        }
    }

    private async Task<WindowsSingleInstanceStartStatus> CompleteStartAsync()
    {
        IWindowsAppInstanceRegistration? candidate = null;
        try
        {
            candidate = provider.FindOrRegister(InstanceKey);
            for (var redirectAttempt = 1;
                 !candidate.IsCurrent;
                 redirectAttempt++)
            {
                var redirectedProcessId = candidate.ProcessId;
                var redirectResult = await TryRedirectAsync(candidate).ConfigureAwait(false);
                candidate.Dispose();
                candidate = null;

                if (handoffObservationDelay > TimeSpan.Zero)
                {
                    await handoffDelay(handoffObservationDelay).ConfigureAwait(false);
                }

                candidate = provider.FindOrRegister(InstanceKey);
                if (candidate.IsCurrent)
                {
                    logger.LogInformation(
                        "The redirected Windows activation was reclaimed after primary shutdown.");
                    break;
                }

                if (candidate.ProcessId == redirectedProcessId)
                {
                    candidate.Dispose();
                    candidate = null;
                    return redirectResult == RedirectResult.Succeeded
                        ? WindowsSingleInstanceStartStatus.Redirected
                        : WindowsSingleInstanceStartStatus.RedirectFailed;
                }

                if (redirectAttempt >= MaximumRedirectAttempts)
                {
                    candidate.Dispose();
                    candidate = null;
                    logger.LogError(
                        "The primary Windows app instance changed too often during activation handoff.");
                    return WindowsSingleInstanceStartStatus.RedirectFailed;
                }

                logger.LogInformation(
                    "The primary Windows app instance changed during activation handoff; " +
                    "redirecting to its successor.");
            }

            candidate.Activated += OnActivated;
            lock (stateGate)
            {
                if (disposed)
                {
                    candidate.Activated -= OnActivated;
                    candidate.Dispose();
                    return WindowsSingleInstanceStartStatus.RedirectFailed;
                }

                registration = candidate;
            }

            try
            {
                Dispatch(candidate.GetCurrentActivation());
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    "Reading the current Windows activation failed; errorType={ErrorType}.",
                    exception.GetType().Name);
            }

            logger.LogInformation("The primary Windows app instance is ready.");
            return WindowsSingleInstanceStartStatus.Primary;
        }
        catch (Exception exception)
        {
            candidate?.Dispose();
            logger.LogError(
                "Registering the primary Windows app instance failed; " +
                "errorType={ErrorType}.",
                exception.GetType().Name);
            return WindowsSingleInstanceStartStatus.RedirectFailed;
        }
    }

    private void OnActivated(WindowsProcessActivation activation) => Dispatch(activation);

    private async Task<RedirectResult> TryRedirectAsync(
        IWindowsAppInstanceRegistration candidate)
    {
        Task redirect;
        try
        {
            redirect = candidate.RedirectCurrentActivationAsync();
        }
        catch (Exception exception)
        {
            LogRedirectFailure(exception);
            return RedirectResult.Failed;
        }

        using var processExitCancellation = new CancellationTokenSource();
        var processExit = ObserveProcessExitAsync(
            candidate.ProcessId,
            processExitCancellation.Token);
        var boundedRedirect = redirect.WaitAsync(redirectTimeout);
        try
        {
            _ = await Task.WhenAny(boundedRedirect, processExit).ConfigureAwait(false);
            if (boundedRedirect.IsCompleted)
            {
                await boundedRedirect.ConfigureAwait(false);
                logger.LogInformation(
                    "The Windows activation was redirected to the primary instance.");
                return RedirectResult.Succeeded;
            }

            await processExit.ConfigureAwait(false);
            ObserveFault(boundedRedirect);
            logger.LogInformation(
                "The redirect target exited before Windows confirmed activation handoff.");
            return RedirectResult.TargetExited;
        }
        catch (Exception exception)
        {
            LogRedirectFailure(exception);
            return RedirectResult.Failed;
        }
        finally
        {
            processExitCancellation.Cancel();
        }
    }

    private async Task ObserveProcessExitAsync(
        uint processId,
        CancellationToken cancellationToken)
    {
        try
        {
            await processExitWaiter(processId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Observing the primary Windows app process failed; errorType={ErrorType}.",
                exception.GetType().Name);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }
    }

    private void LogRedirectFailure(Exception exception) =>
        logger.LogError(
            "Redirecting a Windows activation failed; errorType={ErrorType}.",
            exception.GetType().Name);

    private static async Task WaitForProcessExitAsync(
        uint processId,
        CancellationToken cancellationToken)
    {
        if (processId > int.MaxValue)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        Process process;
        try
        {
            process = Process.GetProcessById((int)processId);
        }
        catch (ArgumentException)
        {
            return;
        }
        catch (InvalidOperationException)
        {
            return;
        }
        catch (Win32Exception)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        using (process)
        {
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // A process that exits between lookup and handle observation is terminal.
            }
        }
    }

    private static void ObserveFault(Task task) =>
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously |
                TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);

    private void Dispatch(WindowsProcessActivation activation)
    {
        lock (stateGate)
        {
            if (disposed)
            {
                return;
            }
        }

        try
        {
            activationSink(activation);
        }
        catch (Exception exception)
        {
            logger.LogError(
                "Dispatching a Windows activation failed; errorType={ErrorType}.",
                exception.GetType().Name);
        }
    }

    private enum RedirectResult
    {
        Succeeded,
        Failed,
        TargetExited,
    }
}
