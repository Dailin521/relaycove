using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using RelayCove.Client.Notifications;

namespace RelayCove.Client.Tests.Notifications;

public sealed class WindowsClientNotificationHostTests
{
    private const string AccountScopeId =
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [Fact]
    public void TryStart_WhenSupported_SubscribesBeforeRegisterAndIsIdempotent()
    {
        var manager = new FakeWindowsAppNotificationManager();
        manager.RegisterAction = () => Assert.Equal(1, manager.SubscriberCount);
        using var host = CreateHost(manager, _ => { });

        Assert.True(host.TryStart());
        Assert.True(host.TryStart());

        Assert.Equal(1, manager.RegisterCount);
        Assert.Equal(1, manager.SubscriberCount);
    }

    [Fact]
    public void Invocation_WhenTargetIsValid_DeliversParsedTarget()
    {
        var manager = new FakeWindowsAppNotificationManager();
        ClientNotificationActivationTarget? received = null;
        using var host = CreateHost(manager, target => received = target);
        Assert.True(host.TryStart());
        var argument = WindowsNotificationActivationCodec.EncodeToArgument(
            ClientNotificationActivationTarget.UnreadOverview(AccountScopeId));

        manager.Raise(argument);

        Assert.NotNull(received);
        Assert.Equal(ClientNotificationActivationKind.UnreadOverview, received.Kind);
        Assert.Equal(AccountScopeId, received.AccountScopeId);
    }

    [Fact]
    public void TryStart_WhenCurrentActivationIsNotification_DeliversAfterRegistration()
    {
        var target = ClientNotificationActivationTarget.UnreadOverview(AccountScopeId);
        var manager = new FakeWindowsAppNotificationManager
        {
            CurrentActivationArgument =
                WindowsNotificationActivationCodec.EncodeToArgument(target),
        };
        ClientNotificationActivationTarget? received = null;
        using var host = CreateHost(manager, activation => received = activation);

        Assert.True(host.TryStart());

        Assert.Equal(target, received);
        Assert.Equal(1, manager.GetCurrentActivationCount);
    }

    [Fact]
    public void TryStart_WhenCurrentActivationReadFails_KeepsRegisteredAndLogsSafely()
    {
        var manager = new FakeWindowsAppNotificationManager
        {
            CurrentActivationException = new COMException("sensitive activation"),
        };
        var logger = new RecordingLogger<WindowsClientNotificationHost>();
        using var host = CreateHost(manager, _ => { }, logger);

        Assert.True(host.TryStart());

        Assert.Equal(1, manager.SubscriberCount);
        Assert.DoesNotContain(
            logger.Entries,
            entry => entry.Contains("sensitive activation", StringComparison.Ordinal));
    }

    [Fact]
    public void Invocation_WhenTargetIsInvalid_FailsClosedWithoutCallingSink()
    {
        var manager = new FakeWindowsAppNotificationManager();
        var logger = new RecordingLogger<WindowsClientNotificationHost>();
        var sinkCalls = 0;
        using var host = CreateHost(
            manager,
            _ => Interlocked.Increment(ref sinkCalls),
            logger);
        Assert.True(host.TryStart());

        manager.Raise("target=message&account=attacker");

        Assert.Equal(0, sinkCalls);
        Assert.Contains(
            logger.Entries,
            entry => entry.Contains("rejected", StringComparison.Ordinal));
        Assert.DoesNotContain(
            logger.Entries,
            entry => entry.Contains("attacker", StringComparison.Ordinal));
    }

    [Fact]
    public void TryStart_WhenRegisterFails_UnsubscribesAndReportsUnavailable()
    {
        var manager = new FakeWindowsAppNotificationManager
        {
            RegisterException = new COMException("registration failed"),
        };
        using var host = CreateHost(manager, _ => { });

        Assert.False(host.TryStart());

        Assert.Equal(1, manager.RegisterCount);
        Assert.Equal(0, manager.SubscriberCount);
        Assert.Equal(0, manager.UnregisterCount);
    }

    [Fact]
    public async Task TryStart_WhenRegisterDoesNotFinishInTime_ReturnsAndCleansLateRegistration()
    {
        var manager = new FakeWindowsAppNotificationManager
        {
            RegisterAction = () => Thread.Sleep(100),
        };
        using var host = CreateHost(
            manager,
            _ => { },
            nativeOperationTimeout: TimeSpan.FromMilliseconds(20));

        Assert.False(host.TryStart());
        await WaitUntilAsync(() => manager.UnregisterCount == 1);

        Assert.Equal(0, manager.SubscriberCount);
    }

    [Fact]
    public async Task TryStart_WhileLateRegistrationIsUncertain_DoesNotRaceCleanup()
    {
        using var release = new ManualResetEventSlim();
        var manager = new FakeWindowsAppNotificationManager
        {
            RegisterAction = release.Wait,
        };
        using var host = CreateHost(
            manager,
            _ => { },
            nativeOperationTimeout: TimeSpan.FromMilliseconds(20));

        Assert.False(host.TryStart());
        Assert.False(host.TryStart());
        Assert.Equal(1, manager.RegisterCount);

        release.Set();
        await WaitUntilAsync(() => manager.UnregisterCount == 1);

        Assert.True(host.TryStart());
        Assert.Equal(2, manager.RegisterCount);
    }

    [Fact]
    public void Dispose_AfterRegistration_UnsubscribesBeforeUnregisterAndIsIdempotent()
    {
        var manager = new FakeWindowsAppNotificationManager();
        var host = CreateHost(manager, _ => { });
        Assert.True(host.TryStart());
        manager.UnregisterAction = () => Assert.Equal(0, manager.SubscriberCount);

        host.Dispose();
        host.Dispose();

        Assert.Equal(1, manager.UnregisterCount);
        Assert.Equal(0, manager.SubscriberCount);
    }

    [Fact]
    public void Dispose_WhenUnregisterDoesNotFinishInTime_ReturnsWithinBound()
    {
        var manager = new FakeWindowsAppNotificationManager();
        var host = CreateHost(
            manager,
            _ => { },
            nativeOperationTimeout: TimeSpan.FromMilliseconds(20));
        Assert.True(host.TryStart());
        manager.UnregisterAction = () => Thread.Sleep(100);
        var startedAt = DateTime.UtcNow;

        host.Dispose();

        Assert.True(DateTime.UtcNow - startedAt < TimeSpan.FromSeconds(1));
        Assert.Equal(0, manager.SubscriberCount);
    }

    [Fact]
    public void Invocation_AfterDispose_IsIgnored()
    {
        var manager = new FakeWindowsAppNotificationManager();
        var sinkCalls = 0;
        var host = CreateHost(manager, _ => Interlocked.Increment(ref sinkCalls));
        Assert.True(host.TryStart());
        host.Dispose();

        manager.Raise(WindowsNotificationActivationCodec.EncodeToArgument(
            ClientNotificationActivationTarget.UnreadOverview(AccountScopeId)));

        Assert.Equal(0, sinkCalls);
    }

    private static WindowsClientNotificationHost CreateHost(
        FakeWindowsAppNotificationManager manager,
        Action<ClientNotificationActivationTarget> sink,
        RecordingLogger<WindowsClientNotificationHost>? logger = null,
        TimeSpan? nativeOperationTimeout = null) =>
        new(
            manager,
            sink,
            logger ?? new RecordingLogger<WindowsClientNotificationHost>(),
            nativeOperationTimeout);

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("The expected host state was not observed.");
            }

            await Task.Delay(10);
        }
    }

    private sealed class FakeWindowsAppNotificationManager : IWindowsAppNotificationManager
    {
        private Action<string>? notificationInvoked;

        public event Action<string>? NotificationInvoked
        {
            add
            {
                notificationInvoked += value;
                SubscriberCount++;
            }
            remove
            {
                notificationInvoked -= value;
                SubscriberCount--;
            }
        }

        public int SubscriberCount { get; private set; }

        public bool IsSupportedValue { get; init; } = true;

        public WindowsClientNotificationSetting Setting { get; init; } =
            WindowsClientNotificationSetting.Enabled;

        public Exception? RegisterException { get; init; }

        public Action? RegisterAction { get; set; }

        public Action? UnregisterAction { get; set; }

        public int RegisterCount { get; private set; }

        public int UnregisterCount { get; private set; }

        public string? CurrentActivationArgument { get; init; }

        public Exception? CurrentActivationException { get; init; }

        public int GetCurrentActivationCount { get; private set; }

        public bool IsSupported() => IsSupportedValue;

        public void Register()
        {
            RegisterCount++;
            RegisterAction?.Invoke();
            if (RegisterException is not null)
            {
                throw RegisterException;
            }
        }

        public string? GetCurrentActivationArgument()
        {
            GetCurrentActivationCount++;
            if (CurrentActivationException is not null)
            {
                throw CurrentActivationException;
            }

            return CurrentActivationArgument;
        }

        public void Unregister()
        {
            UnregisterCount++;
            UnregisterAction?.Invoke();
        }

        public Task ShowAsync(
            WindowsClientNotification notification,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RemoveByGroupAsync(
            string group,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RemoveByTagAndGroupAsync(
            string tag,
            string group,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public void Raise(string argument) => notificationInvoked?.Invoke(argument);
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
