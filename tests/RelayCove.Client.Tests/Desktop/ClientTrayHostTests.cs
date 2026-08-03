using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using RelayCove.Client.Desktop;
using RelayCove.Shared.Realtime;

namespace RelayCove.Client.Tests.Desktop;

public sealed class ClientTrayHostTests
{
    [Theory]
    [InlineData(0, ConnectionState.Disconnected, "Unread: 0", "Status: Disconnected")]
    [InlineData(1, ConnectionState.Connecting, "Unread: 1", "Status: Connecting")]
    [InlineData(7, ConnectionState.Connected, "Unread: 7", "Status: Connected")]
    [InlineData(1000, ConnectionState.Reconnecting, "Unread: 999+", "Status: Reconnecting")]
    [InlineData(int.MaxValue, ConnectionState.ServerUnavailable, "Unread: 999+", "Status: Server unavailable")]
    public void Format_WhenStatusIsValid_ProducesBoundedDisplay(
        int unreadCount,
        ConnectionState connectionState,
        string expectedUnread,
        string expectedConnection)
    {
        var display = ClientTrayStatusFormatter.Format(
            new ClientTrayStatus(unreadCount, connectionState));

        Assert.Equal(expectedUnread, display.UnreadText);
        Assert.Equal(expectedConnection, display.ConnectionText);
        Assert.InRange(
            display.ToolTipText.Length,
            1,
            ClientTrayStatusFormatter.MaximumToolTipLength);
    }

    [Fact]
    public void TryStart_WhenIconShows_MakesHostAvailableWithInitialStatus()
    {
        var icon = new RecordingTrayIcon();
        using var host = CreateHost(icon);

        var started = host.TryStart();

        Assert.True(started);
        Assert.True(host.IsAvailable);
        Assert.Equal("Unread: 0", Assert.Single(icon.Shown).UnreadText);
    }

    [Fact]
    public void TryStart_WhenIconThrows_FailsWithoutMakingCloseToTrayAvailable()
    {
        var icon = new RecordingTrayIcon
        {
            ShowException = new InvalidOperationException("sensitive tray"),
        };
        var logger = new RecordingLogger<ClientTrayHost>();
        using var host = CreateHost(icon, logger: logger);

        var started = host.TryStart();

        Assert.False(started);
        Assert.False(host.IsAvailable);
        Assert.Contains(
            logger.Entries,
            entry => entry.Contains(nameof(InvalidOperationException), StringComparison.Ordinal));
        Assert.DoesNotContain(
            logger.Entries,
            entry => entry.Contains("sensitive tray", StringComparison.Ordinal));
    }

    [Fact]
    public void UpdateStatus_WhenStarted_MarshalsLatestStatusToUi()
    {
        var icon = new RecordingTrayIcon();
        var queued = new Queue<Action>();
        using var host = CreateHost(icon, dispatch: queued.Enqueue);
        Assert.True(host.TryStart());

        host.UpdateStatus(new ClientTrayStatus(23, ConnectionState.Connected));

        Assert.Empty(icon.Updated);
        Assert.Single(queued).Invoke();
        Assert.Equal("Unread: 23", Assert.Single(icon.Updated).UnreadText);
        Assert.Equal("Status: Connected", Assert.Single(icon.Updated).ConnectionText);
    }

    [Fact]
    public void OpenAndExit_WhenRequested_DispatchOpenEveryTimeAndExitOnlyOnce()
    {
        var icon = new RecordingTrayIcon();
        var openCount = 0;
        var exitCount = 0;
        using var host = CreateHost(
            icon,
            open: () => openCount++,
            exit: () => exitCount++);
        Assert.True(host.TryStart());

        icon.RaiseOpen();
        icon.RaiseOpen();
        icon.RaiseExit();
        icon.RaiseExit();

        Assert.Equal(2, openCount);
        Assert.Equal(1, exitCount);
    }

    [Fact]
    public void Dispose_WhenCallbacksArrive_IgnoresThemAndDisposesIconOnce()
    {
        var icon = new RecordingTrayIcon();
        var openCount = 0;
        var exitCount = 0;
        var host = CreateHost(
            icon,
            open: () => openCount++,
            exit: () => exitCount++);
        Assert.True(host.TryStart());

        host.Dispose();
        host.Dispose();
        icon.RaiseOpen();
        icon.RaiseExit();

        Assert.False(host.IsAvailable);
        Assert.Equal(0, openCount);
        Assert.Equal(0, exitCount);
        Assert.Equal(1, icon.DisposeCount);
    }

    private static ClientTrayHost CreateHost(
        IClientTrayIcon icon,
        Action<Action>? dispatch = null,
        Action? open = null,
        Action? exit = null,
        ILogger<ClientTrayHost>? logger = null) =>
        new(
            icon,
            dispatch ?? (static action => action()),
            open ?? (static () => { }),
            exit ?? (static () => { }),
            new ClientTrayStatus(0, ConnectionState.Disconnected),
            logger ?? new RecordingLogger<ClientTrayHost>());

    private sealed class RecordingTrayIcon : IClientTrayIcon
    {
        private int disposeCount;

        public event Action? OpenRequested;

        public event Action? ExitRequested;

        public Exception? ShowException { get; init; }

        public List<ClientTrayDisplay> Shown { get; } = [];

        public List<ClientTrayDisplay> Updated { get; } = [];

        public int DisposeCount => Volatile.Read(ref disposeCount);

        public void Show(ClientTrayDisplay display)
        {
            if (ShowException is not null)
            {
                throw ShowException;
            }

            Shown.Add(display);
        }

        public void Update(ClientTrayDisplay display) => Updated.Add(display);

        public void Dispose() => Interlocked.Increment(ref disposeCount);

        public void RaiseOpen() => OpenRequested?.Invoke();

        public void RaiseExit() => ExitRequested?.Invoke();
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
