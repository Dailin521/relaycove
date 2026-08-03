namespace RelayCove.Client.Desktop;

internal sealed class WindowsMainWindowState
{
    private readonly object stateGate = new();
    private Snapshot snapshot = Snapshot.Empty;

    public Snapshot Current
    {
        get
        {
            lock (stateGate)
            {
                return snapshot;
            }
        }
    }

    public void Update(nint windowHandle, bool isForeground)
    {
        if (isForeground && windowHandle == nint.Zero)
        {
            throw new ArgumentException(
                "A foreground window must have a native handle.",
                nameof(windowHandle));
        }

        lock (stateGate)
        {
            snapshot = new Snapshot(windowHandle, isForeground);
        }
    }

    internal readonly record struct Snapshot(nint WindowHandle, bool IsForeground)
    {
        public static Snapshot Empty { get; } = new(nint.Zero, IsForeground: false);
    }
}
