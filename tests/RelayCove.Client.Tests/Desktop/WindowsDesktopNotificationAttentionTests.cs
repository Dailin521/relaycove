using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using RelayCove.Client.Desktop;

namespace RelayCove.Client.Tests.Desktop;

public sealed class WindowsDesktopNotificationAttentionTests
{
    [Fact]
    public void SignalAcceptedToast_WhenWindowIsBackground_PlaysSoundAndStartsFlash()
    {
        var windowState = new WindowsMainWindowState();
        windowState.Update((nint)42, isForeground: false);
        var native = new RecordingNative();
        var attention = CreateAttention(windowState, native);

        attention.SignalAcceptedToast();

        Assert.Equal(1, native.SoundCount);
        Assert.Equal([(nint)42], native.StartHandles);
        Assert.Empty(native.StopHandles);
    }

    [Fact]
    public void SignalAcceptedToast_WhenWindowIsForeground_PlaysSoundWithoutStartingFlash()
    {
        var windowState = new WindowsMainWindowState();
        windowState.Update((nint)42, isForeground: true);
        var native = new RecordingNative();
        var attention = CreateAttention(windowState, native);

        attention.SignalAcceptedToast();

        Assert.Equal(1, native.SoundCount);
        Assert.Empty(native.StartHandles);
        Assert.Empty(native.StopHandles);
    }

    [Fact]
    public void SignalAcceptedToast_WhenSoundThrows_StillStartsFlashAndLogsTypeOnly()
    {
        var windowState = new WindowsMainWindowState();
        windowState.Update((nint)42, isForeground: false);
        var native = new RecordingNative
        {
            SoundException = new InvalidOperationException("sensitive sound"),
        };
        var logger = new RecordingLogger<WindowsDesktopNotificationAttention>();
        var attention = new WindowsDesktopNotificationAttention(windowState, logger, native);

        attention.SignalAcceptedToast();

        Assert.Equal([(nint)42], native.StartHandles);
        Assert.Contains(
            logger.Entries,
            entry => entry.Contains(nameof(InvalidOperationException), StringComparison.Ordinal));
        Assert.DoesNotContain(
            logger.Entries,
            entry => entry.Contains("sensitive sound", StringComparison.Ordinal));
    }

    [Fact]
    public void SignalAcceptedToast_WhenFlashStartThrows_LogsTypeOnlyAndDoesNotArmStop()
    {
        var windowState = new WindowsMainWindowState();
        windowState.Update((nint)42, isForeground: false);
        var native = new RecordingNative
        {
            StartException = new InvalidOperationException("sensitive start"),
        };
        var logger = new RecordingLogger<WindowsDesktopNotificationAttention>();
        var attention = new WindowsDesktopNotificationAttention(windowState, logger, native);

        attention.SignalAcceptedToast();
        attention.StopFlashing();

        Assert.Equal([(nint)42], native.StartHandles);
        Assert.Empty(native.StopHandles);
        Assert.Contains(
            logger.Entries,
            entry => entry.Contains(nameof(InvalidOperationException), StringComparison.Ordinal));
        Assert.DoesNotContain(
            logger.Entries,
            entry => entry.Contains("sensitive start", StringComparison.Ordinal));
    }

    [Fact]
    public void StopFlashing_WhenStarted_StopsOriginalHandleOnlyOnce()
    {
        var windowState = new WindowsMainWindowState();
        windowState.Update((nint)42, isForeground: false);
        var native = new RecordingNative();
        var attention = CreateAttention(windowState, native);
        attention.SignalAcceptedToast();

        windowState.Update((nint)84, isForeground: true);
        attention.StopFlashing();
        attention.StopFlashing();

        Assert.Equal([(nint)42], native.StopHandles);
    }

    [Fact]
    public void StopFlashing_WhenNativeThrows_ClearsDutyWithoutRetryingOrLoggingPayload()
    {
        var windowState = new WindowsMainWindowState();
        windowState.Update((nint)42, isForeground: false);
        var native = new RecordingNative();
        var logger = new RecordingLogger<WindowsDesktopNotificationAttention>();
        var attention = new WindowsDesktopNotificationAttention(windowState, logger, native);
        attention.SignalAcceptedToast();
        native.StopException = new InvalidOperationException("sensitive stop");

        attention.StopFlashing();
        attention.StopFlashing();

        Assert.Equal([(nint)42], native.StopHandles);
        Assert.Contains(
            logger.Entries,
            entry => entry.Contains(nameof(InvalidOperationException), StringComparison.Ordinal));
        Assert.DoesNotContain(
            logger.Entries,
            entry => entry.Contains("sensitive stop", StringComparison.Ordinal));
    }

    [Fact]
    public void SignalAcceptedToast_WhenWindowBecomesForeground_StopsPreviousFlash()
    {
        var windowState = new WindowsMainWindowState();
        windowState.Update((nint)42, isForeground: false);
        var native = new RecordingNative();
        var attention = CreateAttention(windowState, native);
        attention.SignalAcceptedToast();

        windowState.Update((nint)42, isForeground: true);
        attention.SignalAcceptedToast();

        Assert.Equal(2, native.SoundCount);
        Assert.Equal([(nint)42], native.StartHandles);
        Assert.Equal([(nint)42], native.StopHandles);
    }

    [Fact]
    public void Update_WhenForegroundHandleIsMissing_RejectsInvalidState()
    {
        var windowState = new WindowsMainWindowState();

        Assert.Throws<ArgumentException>(() =>
            windowState.Update(nint.Zero, isForeground: true));
    }

    private static WindowsDesktopNotificationAttention CreateAttention(
        WindowsMainWindowState windowState,
        IWindowsDesktopAttentionNative native) =>
        new(
            windowState,
            new RecordingLogger<WindowsDesktopNotificationAttention>(),
            native);

    private sealed class RecordingNative : IWindowsDesktopAttentionNative
    {
        private int soundCount;

        public Exception? SoundException { get; init; }

        public Exception? StartException { get; init; }

        public Exception? StopException { get; set; }

        public bool SoundResult { get; init; } = true;

        public int SoundCount => Volatile.Read(ref soundCount);

        public List<nint> StartHandles { get; } = [];

        public List<nint> StopHandles { get; } = [];

        public bool PlayNotificationSound()
        {
            Interlocked.Increment(ref soundCount);
            if (SoundException is not null)
            {
                throw SoundException;
            }

            return SoundResult;
        }

        public void StartTaskbarFlash(nint windowHandle)
        {
            StartHandles.Add(windowHandle);
            if (StartException is not null)
            {
                throw StartException;
            }
        }

        public void StopTaskbarFlash(nint windowHandle)
        {
            StopHandles.Add(windowHandle);
            if (StopException is not null)
            {
                throw StopException;
            }
        }
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
