using Microsoft.Extensions.DependencyInjection;
using RelayCove.App.Platforms.Windows;
using RelayCove.App.ViewModels;
using RelayCove.Core;

namespace RelayCove.App.Services;

public sealed class ApplicationShutdownCoordinator : IApplicationShutdownCoordinator
{
    private static readonly TimeSpan ShutdownDeadline = TimeSpan.FromSeconds(3);
    private readonly Func<CancellationToken, Task> _cleanupAsync;
    private readonly Action _releaseNativeResources;
    private readonly Action<int> _terminateProcess;
    private readonly Action<string> _diagnose;
    private readonly TimeSpan _forcedExitDelay;
    private readonly CancellationTokenSource _forcedExitCancellation = new();
    private Task? _shutdownTask;
    private int _shutdownRequested;

    public ApplicationShutdownCoordinator(IServiceProvider services)
        : this(
            cancellationToken => StopAndDisposeServicesAsync(services, cancellationToken, WindowsLifecycleDiagnostics.Write),
            () => services.GetRequiredService<IAppNotificationService>().Dispose(),
            Environment.Exit,
            WindowsLifecycleDiagnostics.Write,
            ShutdownDeadline)
    {
    }

    internal ApplicationShutdownCoordinator(
        Func<CancellationToken, Task> cleanupAsync,
        Action releaseNativeResources,
        Action<int> terminateProcess,
        Action<string>? diagnose = null,
        TimeSpan? forcedExitDelay = null)
    {
        _cleanupAsync = cleanupAsync ?? throw new ArgumentNullException(nameof(cleanupAsync));
        _releaseNativeResources = releaseNativeResources ?? throw new ArgumentNullException(nameof(releaseNativeResources));
        _terminateProcess = terminateProcess ?? throw new ArgumentNullException(nameof(terminateProcess));
        _diagnose = diagnose ?? (_ => { });
        _forcedExitDelay = forcedExitDelay ?? ShutdownDeadline;
    }

    public bool IsShutdownRequested => Volatile.Read(ref _shutdownRequested) != 0;

    public Task RequestShutdownAsync(ApplicationShutdownEntryPoint entryPoint)
    {
        if (Interlocked.Exchange(ref _shutdownRequested, 1) != 0)
        {
            return _shutdownTask ?? Task.CompletedTask;
        }

        WindowsApplicationLifetime.MarkExitRequested();
        _diagnose($"shutdown-requested:{entryPoint}");
        _ = ForceExitAfterDelayAsync(_forcedExitDelay, _forcedExitCancellation.Token, _terminateProcess, _diagnose);
        try
        {
            _releaseNativeResources();
            _diagnose("native-resources-released");
        }
        catch (Exception exception)
        {
            _diagnose($"native-resources-failed:{exception.GetType().Name}");
        }

        _shutdownTask = Task.Run(RunShutdownAsync);
        return _shutdownTask;
    }

    private async Task RunShutdownAsync()
    {
        try
        {
            using var cleanupCancellation = new CancellationTokenSource(_forcedExitDelay);
            await _cleanupAsync(cleanupCancellation.Token).ConfigureAwait(false);
            _diagnose("cleanup-complete");
        }
        catch (OperationCanceledException)
        {
            _diagnose("cleanup-timed-out");
        }
        catch (Exception exception)
        {
            _diagnose($"cleanup-failed:{exception.GetType().Name}");
        }
        finally
        {
            _forcedExitCancellation.Cancel();
            _terminateProcess(0);
        }
    }

    private static async Task ForceExitAfterDelayAsync(
        TimeSpan delay,
        CancellationToken cancellationToken,
        Action<int> terminateProcess,
        Action<string> diagnose)
    {
        try
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            diagnose("forced-exit");
            terminateProcess(0);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static async Task StopAndDisposeServicesAsync(
        IServiceProvider services,
        CancellationToken cancellationToken,
        Action<string> diagnose)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(diagnose);
        var session = services.GetRequiredService<IClientSession>();
        diagnose("cleanup-session-stop");
        try
        {
            await session.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            diagnose($"cleanup-session-stop-failed:{exception.GetType().Name}");
        }

        if (!cancellationToken.IsCancellationRequested && session is IAsyncDisposable sessionDisposable)
        {
            diagnose("cleanup-session-dispose");
            try
            {
                await sessionDisposable.DisposeAsync().AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                diagnose($"cleanup-session-dispose-failed:{exception.GetType().Name}");
            }
        }

        diagnose("cleanup-viewmodel-dispose");
        try
        {
            services.GetService<ShellViewModel>()?.Dispose();
        }
        catch (Exception exception)
        {
            diagnose($"cleanup-viewmodel-dispose-failed:{exception.GetType().Name}");
        }

        if (!cancellationToken.IsCancellationRequested &&
            services.GetService<IAccountStore>() is IAsyncDisposable storeDisposable)
        {
            diagnose("cleanup-account-store-dispose");
            try
            {
                await storeDisposable.DisposeAsync().AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                diagnose($"cleanup-account-store-dispose-failed:{exception.GetType().Name}");
            }
        }

        if (services.GetService<IZulipGateway>() is IDisposable gatewayDisposable)
        {
            diagnose("cleanup-gateway-dispose");
            try
            {
                gatewayDisposable.Dispose();
            }
            catch (Exception exception)
            {
                diagnose($"cleanup-gateway-dispose-failed:{exception.GetType().Name}");
            }
        }
    }
}
