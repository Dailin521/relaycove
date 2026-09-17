namespace RelayCove.App.Platforms.Windows;

internal static class WindowsApplicationLifetime
{
    private static int _exitRequested;

    internal static bool IsExitRequested => Volatile.Read(ref _exitRequested) != 0;

    internal static void MarkExitRequested() => Interlocked.Exchange(ref _exitRequested, 1);
}
