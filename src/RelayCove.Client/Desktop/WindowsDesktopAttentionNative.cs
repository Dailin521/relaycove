using System.Runtime.InteropServices;

namespace RelayCove.Client.Desktop;

internal sealed class WindowsDesktopAttentionNative : IWindowsDesktopAttentionNative
{
    private const uint FlashWindowStop = 0;
    private const uint FlashWindowTray = 0x00000002;
    private const uint FlashWindowTimerNoForeground = 0x0000000C;
    private const uint MessageBeepAsterisk = 0x00000040;

    public bool PlayNotificationSound() => MessageBeep(MessageBeepAsterisk);

    public void StartTaskbarFlash(nint windowHandle)
    {
        var information = CreateInformation(
            windowHandle,
            FlashWindowTray | FlashWindowTimerNoForeground,
            uint.MaxValue);
        _ = FlashWindowEx(ref information);
    }

    public void StopTaskbarFlash(nint windowHandle)
    {
        var information = CreateInformation(windowHandle, FlashWindowStop, flashCount: 0);
        _ = FlashWindowEx(ref information);
    }

    private static FlashWindowInformation CreateInformation(
        nint windowHandle,
        uint flags,
        uint flashCount) =>
        new()
        {
            Size = (uint)Marshal.SizeOf<FlashWindowInformation>(),
            WindowHandle = windowHandle,
            Flags = flags,
            FlashCount = flashCount,
            TimeoutMilliseconds = 0,
        };

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MessageBeep(uint type);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FlashWindowInformation information);

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashWindowInformation
    {
        public uint Size;
        public nint WindowHandle;
        public uint Flags;
        public uint FlashCount;
        public uint TimeoutMilliseconds;
    }
}
