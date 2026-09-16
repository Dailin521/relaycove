using System.Runtime.InteropServices;
using RelayCove.Core;

namespace RelayCove.App.Platforms.Windows;

public sealed class WindowsUserActivitySource : IUserActivitySource
{
    internal static readonly TimeSpan IdleThreshold = TimeSpan.FromMinutes(5);

    public bool? IsIdle
    {
        get
        {
            var input = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
            uint? lastInput = GetLastInputInfo(ref input) ? input.TickCount : null;
            return ResolveIdle(lastInput, unchecked((uint)Environment.TickCount64));
        }
    }

    internal static bool? ResolveIdle(uint? lastInput, uint now)
    {
        if (lastInput is null) return null;
        // LASTINPUTINFO uses a wrapping 32-bit system tick count, not wall-clock time.
        return unchecked(now - lastInput.Value) >= IdleThreshold.TotalMilliseconds;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint TickCount;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

}
