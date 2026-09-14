using System.Runtime.InteropServices;

namespace RelayCove.App.Platforms.Windows;

internal sealed class WindowsTrayBlinkTimer
{
    private const uint TimerMessage = 0x0113;
    private const nuint TimerId = 1;
    private readonly Action _tick;
    private readonly Func<nint, nuint, uint, nuint> _startTimer;
    private readonly Action<nint, nuint> _stopTimer;
    private nint _windowHandle;
    private nuint _activeTimerId;

    internal WindowsTrayBlinkTimer(Action tick)
        : this(tick, (window, id, interval) => SetTimer(window, id, interval, 0),
            (window, id) => { _ = KillTimer(window, id); })
    {
    }

    internal WindowsTrayBlinkTimer(
        Action tick,
        Func<nint, nuint, uint, nuint> startTimer,
        Action<nint, nuint> stopTimer)
    {
        _tick = tick;
        _startTimer = startTimer;
        _stopTimer = stopTimer;
    }

    internal bool IsRunning => _activeTimerId != 0;

    internal void Start(nint windowHandle)
    {
        // Keep the existing cadence when another message or state update arrives.
        if (windowHandle == 0 || IsRunning) return;
        _windowHandle = windowHandle;
        _activeTimerId = _startTimer(windowHandle, TimerId, 500);
    }

    internal void Stop()
    {
        if (!IsRunning) return;
        var timerId = _activeTimerId;
        _activeTimerId = 0;
        _stopTimer(_windowHandle, timerId);
        _windowHandle = 0;
    }

    internal bool TryHandleMessage(nint windowHandle, uint message, nint wordParameter)
    {
        if (!IsRunning || windowHandle != _windowHandle || message != TimerMessage ||
            unchecked((nuint)wordParameter) != _activeTimerId) return false;
        _tick();
        return true;
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nuint SetTimer(nint windowHandle, nuint timerId, uint interval, nint callback);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool KillTimer(nint windowHandle, nuint timerId);
}
