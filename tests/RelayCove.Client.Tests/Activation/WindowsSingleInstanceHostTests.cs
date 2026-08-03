using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using RelayCove.Client.Activation;

namespace RelayCove.Client.Tests.Activation;

public sealed class WindowsSingleInstanceHostTests
{
    [Fact]
    public async Task StartAsync_WhenCurrent_DispatchesCurrentAndRedirectedActivations()
    {
        var registration = new FakeRegistration(
            isCurrent: true,
            WindowsProcessActivation.Launch());
        var provider = new FakeProvider(registration);
        var received = new ConcurrentQueue<WindowsProcessActivation>();
        using var host = CreateHost(provider, received.Enqueue);

        var status = await host.StartAsync();
        registration.Raise(WindowsProcessActivation.AppNotification("safe"));

        Assert.Equal(WindowsSingleInstanceStartStatus.Primary, status);
        Assert.Equal(
            [WindowsProcessActivationKind.Launch, WindowsProcessActivationKind.AppNotification],
            received.Select(item => item.Kind));
        Assert.Equal(1, registration.SubscriberCount);
        Assert.Equal(0, registration.RedirectCount);
        Assert.Equal(WindowsSingleInstanceHost.InstanceKey, provider.Key);
    }

    [Fact]
    public async Task StartAsync_WhenSecondary_RedirectsBeforeReturningWithoutLocalDispatch()
    {
        var redirect = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = new FakeRegistration(
            isCurrent: false,
            WindowsProcessActivation.AppNotification("sensitive"))
        {
            RedirectAction = () => redirect.Task,
        };
        var provider = new FakeProvider(registration);
        var sinkCalls = 0;
        using var host = CreateHost(
            provider,
            _ => Interlocked.Increment(ref sinkCalls));

        var start = host.StartAsync();
        Assert.False(start.IsCompleted);
        Assert.Equal(1, registration.RedirectCount);
        redirect.SetResult();

        Assert.Equal(WindowsSingleInstanceStartStatus.Redirected, await start);
        Assert.Equal(0, sinkCalls);
        Assert.Equal(0, registration.SubscriberCount);
        Assert.True(registration.IsDisposed);
        Assert.Equal(2, provider.FindCount);
    }

    [Fact]
    public async Task StartAsync_WhenRedirectTimesOut_FailsClosedAndDoesNotDispatchLocally()
    {
        var never = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = new FakeRegistration(
            isCurrent: false,
            WindowsProcessActivation.Launch())
        {
            RedirectAction = () => never.Task,
        };
        var sinkCalls = 0;
        using var host = CreateHost(
            new FakeProvider(registration),
            _ => Interlocked.Increment(ref sinkCalls),
            redirectTimeout: TimeSpan.FromMilliseconds(20));

        var status = await host.StartAsync();

        Assert.Equal(WindowsSingleInstanceStartStatus.RedirectFailed, status);
        Assert.Equal(0, sinkCalls);
        Assert.True(registration.IsDisposed);
    }

    [Fact]
    public async Task StartAsync_WhenRedirectTargetExits_ReclaimsWithoutWaitingForRedirectTimeout()
    {
        var never = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var redirectedRegistration = new FakeRegistration(
            isCurrent: false,
            WindowsProcessActivation.AppNotification("safe"),
            processId: 10)
        {
            RedirectAction = () => never.Task,
        };
        var reclaimedRegistration = new FakeRegistration(
            isCurrent: true,
            WindowsProcessActivation.AppNotification("safe"),
            processId: 20);
        var received = new List<WindowsProcessActivation>();
        using var host = CreateHost(
            new FakeProvider(redirectedRegistration, reclaimedRegistration),
            received.Add,
            redirectTimeout: TimeSpan.FromSeconds(10),
            processExitWaiter: (_, _) => Task.CompletedTask);

        var status = await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(WindowsSingleInstanceStartStatus.Primary, status);
        Assert.Equal([WindowsProcessActivationKind.AppNotification], received.Select(x => x.Kind));
        Assert.True(redirectedRegistration.IsDisposed);
    }

    [Fact]
    public async Task StartAsync_WhenCalledConcurrently_SharesOneRegistrationAndRedirect()
    {
        var redirect = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = new FakeRegistration(
            isCurrent: false,
            WindowsProcessActivation.Launch())
        {
            RedirectAction = () => redirect.Task,
        };
        var provider = new FakeProvider(registration);
        using var host = CreateHost(provider, _ => { });

        var starts = Enumerable.Range(0, 20)
            .Select(_ => host.StartAsync())
            .ToArray();
        redirect.SetResult();
        var results = await Task.WhenAll(starts);

        Assert.All(
            results,
            result => Assert.Equal(WindowsSingleInstanceStartStatus.Redirected, result));
        Assert.Equal(2, provider.FindCount);
        Assert.Equal(1, registration.RedirectCount);
    }

    [Fact]
    public async Task StartAsync_WhenOneWaiterCancels_SharedRedirectStillCompletes()
    {
        var redirect = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = new FakeRegistration(
            isCurrent: false,
            WindowsProcessActivation.Launch())
        {
            RedirectAction = () => redirect.Task,
        };
        using var host = CreateHost(new FakeProvider(registration), _ => { });
        using var cancellation = new CancellationTokenSource();

        var canceledWaiter = host.StartAsync(cancellation.Token);
        var survivingWaiter = host.StartAsync();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledWaiter);
        redirect.SetResult();

        Assert.Equal(
            WindowsSingleInstanceStartStatus.Redirected,
            await survivingWaiter);
        Assert.Equal(1, registration.RedirectCount);
    }

    [Fact]
    public async Task StartAsync_WhenPrimaryReleasesAfterRedirect_ReclaimsOriginalActivation()
    {
        var redirectedRegistration = new FakeRegistration(
            isCurrent: false,
            WindowsProcessActivation.AppNotification("safe"));
        var reclaimedRegistration = new FakeRegistration(
            isCurrent: true,
            WindowsProcessActivation.AppNotification("safe"));
        var provider = new FakeProvider(
            redirectedRegistration,
            reclaimedRegistration);
        var received = new List<WindowsProcessActivation>();
        using var host = CreateHost(provider, received.Add);

        var status = await host.StartAsync();

        Assert.Equal(WindowsSingleInstanceStartStatus.Primary, status);
        Assert.Equal([WindowsProcessActivationKind.AppNotification], received.Select(x => x.Kind));
        Assert.True(redirectedRegistration.IsDisposed);
        Assert.Equal(1, reclaimedRegistration.SubscriberCount);
        Assert.Equal(2, provider.FindCount);
    }

    [Fact]
    public async Task StartAsync_WhenPrimaryChangesAfterRedirect_RedirectsToSuccessor()
    {
        var originalPrimary = new FakeRegistration(
            isCurrent: false,
            WindowsProcessActivation.AppNotification("safe"),
            processId: 10);
        var successor = new FakeRegistration(
            isCurrent: false,
            WindowsProcessActivation.AppNotification("safe"),
            processId: 20);
        var provider = new FakeProvider(originalPrimary, successor, successor);
        var sinkCalls = 0;
        using var host = CreateHost(
            provider,
            _ => Interlocked.Increment(ref sinkCalls));

        var status = await host.StartAsync();

        Assert.Equal(WindowsSingleInstanceStartStatus.Redirected, status);
        Assert.Equal(1, originalPrimary.RedirectCount);
        Assert.Equal(1, successor.RedirectCount);
        Assert.Equal(0, sinkCalls);
        Assert.Equal(3, provider.FindCount);
    }

    [Fact]
    public async Task StartAsync_WhenUsingProductionObservationDefault_WaitsOneSecondBeforeReelection()
    {
        var originalPrimary = new FakeRegistration(
            isCurrent: false,
            WindowsProcessActivation.Launch(),
            processId: 10);
        var samePrimary = new FakeRegistration(
            isCurrent: false,
            WindowsProcessActivation.Launch(),
            processId: 10);
        TimeSpan? observedDelay = null;
        using var host = new WindowsSingleInstanceHost(
            new FakeProvider(originalPrimary, samePrimary),
            _ => { },
            new RecordingLogger<WindowsSingleInstanceHost>(),
            processExitWaiter: (_, cancellationToken) =>
                Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken),
            handoffDelay: delay =>
            {
                observedDelay = delay;
                return Task.CompletedTask;
            });

        var status = await host.StartAsync();

        Assert.Equal(WindowsSingleInstanceStartStatus.Redirected, status);
        Assert.Equal(TimeSpan.FromSeconds(1), observedDelay);
    }

    [Fact]
    public async Task StartAsync_WhenPrimaryChangesTooOften_FailsAfterBoundedRedirects()
    {
        var first = new FakeRegistration(
            isCurrent: false,
            WindowsProcessActivation.Launch(),
            processId: 10);
        var second = new FakeRegistration(
            isCurrent: false,
            WindowsProcessActivation.Launch(),
            processId: 20);
        var third = new FakeRegistration(
            isCurrent: false,
            WindowsProcessActivation.Launch(),
            processId: 30);
        var fourth = new FakeRegistration(
            isCurrent: false,
            WindowsProcessActivation.Launch(),
            processId: 40);
        var provider = new FakeProvider(first, second, third, fourth);
        using var host = CreateHost(provider, _ => { });

        var status = await host.StartAsync();

        Assert.Equal(WindowsSingleInstanceStartStatus.RedirectFailed, status);
        Assert.Equal(1, first.RedirectCount);
        Assert.Equal(1, second.RedirectCount);
        Assert.Equal(1, third.RedirectCount);
        Assert.Equal(0, fourth.RedirectCount);
        Assert.Equal(4, provider.FindCount);
        Assert.True(fourth.IsDisposed);
    }

    [Fact]
    public async Task StartAsync_WhenCurrentActivationReadFails_RemainsPrimaryAndLogsTypeOnly()
    {
        var registration = new FakeRegistration(
            isCurrent: true,
            WindowsProcessActivation.Launch())
        {
            CurrentActivationException = new InvalidOperationException("secret activation"),
        };
        var logger = new RecordingLogger<WindowsSingleInstanceHost>();
        using var host = CreateHost(new FakeProvider(registration), _ => { }, logger);

        var status = await host.StartAsync();

        Assert.Equal(WindowsSingleInstanceStartStatus.Primary, status);
        Assert.Contains(
            logger.Entries,
            entry => entry.Contains(nameof(InvalidOperationException), StringComparison.Ordinal));
        Assert.DoesNotContain(
            logger.Entries,
            entry => entry.Contains("secret activation", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Dispose_WhenPrimary_UnsubscribesAndIgnoresLaterActivation()
    {
        var registration = new FakeRegistration(
            isCurrent: true,
            WindowsProcessActivation.Launch());
        var sinkCalls = 0;
        var host = CreateHost(
            new FakeProvider(registration),
            _ => Interlocked.Increment(ref sinkCalls));
        Assert.Equal(WindowsSingleInstanceStartStatus.Primary, await host.StartAsync());

        host.Dispose();
        registration.Raise(WindowsProcessActivation.Launch());

        Assert.Equal(1, sinkCalls);
        Assert.Equal(0, registration.SubscriberCount);
        Assert.True(registration.IsDisposed);
    }

    private static WindowsSingleInstanceHost CreateHost(
        FakeProvider provider,
        Action<WindowsProcessActivation> sink,
        RecordingLogger<WindowsSingleInstanceHost>? logger = null,
        TimeSpan? redirectTimeout = null,
        TimeSpan? handoffObservationDelay = null,
        Func<uint, CancellationToken, Task>? processExitWaiter = null) =>
        new(
            provider,
            sink,
            logger ?? new RecordingLogger<WindowsSingleInstanceHost>(),
            redirectTimeout,
            handoffObservationDelay ?? TimeSpan.Zero,
            processExitWaiter ?? ((_, cancellationToken) =>
                Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)));

    private sealed class FakeProvider : IWindowsAppInstanceProvider
    {
        private readonly IReadOnlyList<FakeRegistration> registrations;

        public FakeProvider(params FakeRegistration[] registrations)
        {
            if (registrations.Length == 0)
            {
                throw new ArgumentException("At least one registration is required.");
            }

            this.registrations = registrations;
        }

        public int FindCount { get; private set; }

        public string? Key { get; private set; }

        public IWindowsAppInstanceRegistration FindOrRegister(string key)
        {
            FindCount++;
            Key = key;
            return registrations[Math.Min(FindCount - 1, registrations.Count - 1)];
        }
    }

    private sealed class FakeRegistration : IWindowsAppInstanceRegistration
    {
        private Action<WindowsProcessActivation>? activated;
        private readonly WindowsProcessActivation currentActivation;

        public FakeRegistration(
            bool isCurrent,
            WindowsProcessActivation currentActivation,
            uint processId = 1)
        {
            IsCurrent = isCurrent;
            this.currentActivation = currentActivation;
            ProcessId = processId;
        }

        public event Action<WindowsProcessActivation>? Activated
        {
            add
            {
                activated += value;
                SubscriberCount++;
            }
            remove
            {
                activated -= value;
                SubscriberCount--;
            }
        }

        public bool IsCurrent { get; }

        public uint ProcessId { get; }

        public int SubscriberCount { get; private set; }

        public int RedirectCount { get; private set; }

        public bool IsDisposed { get; private set; }

        public Exception? CurrentActivationException { get; init; }

        public Func<Task>? RedirectAction { get; init; }

        public WindowsProcessActivation GetCurrentActivation()
        {
            if (CurrentActivationException is not null)
            {
                throw CurrentActivationException;
            }

            return currentActivation;
        }

        public Task RedirectCurrentActivationAsync()
        {
            RedirectCount++;
            return RedirectAction?.Invoke() ?? Task.CompletedTask;
        }

        public void Dispose()
        {
            IsDisposed = true;
            activated = null;
        }

        public void Raise(WindowsProcessActivation activation) => activated?.Invoke(activation);
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<string> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue(formatter(state, exception));
    }
}
