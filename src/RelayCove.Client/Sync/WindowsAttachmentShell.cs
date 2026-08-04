using System.Runtime.InteropServices;
using RelayCove.Client.Storage;

namespace RelayCove.Client.Sync;

internal sealed class WindowsAttachmentShell : IWindowsAttachmentShell
{
    private const uint CoinitApartmentThreaded = 0x2;
    private const int RpcEChangedMode = unchecked((int)0x80010106);

    public WindowsAttachmentShellStatus Reveal(
        ClientAttachmentCacheStore.ValidatedFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (!OperatingSystem.IsWindows())
        {
            return WindowsAttachmentShellStatus.Unavailable;
        }

        var initializeResult = NativeMethods.CoInitializeEx(
            IntPtr.Zero,
            CoinitApartmentThreaded);
        var uninitialize = initializeResult >= 0;
        if (initializeResult < 0 && initializeResult != RpcEChangedMode)
        {
            return WindowsAttachmentShellStatus.Unavailable;
        }

        IntPtr itemIdList = IntPtr.Zero;
        try
        {
            var parseResult = NativeMethods.SHParseDisplayName(
                file.FullPath,
                IntPtr.Zero,
                out itemIdList,
                0,
                out _);
            if (parseResult < 0 || itemIdList == IntPtr.Zero)
            {
                return WindowsAttachmentShellStatus.Unavailable;
            }

            var revealResult = NativeMethods.SHOpenFolderAndSelectItems(
                itemIdList,
                0,
                IntPtr.Zero,
                0);
            return revealResult >= 0
                ? WindowsAttachmentShellStatus.Revealed
                : WindowsAttachmentShellStatus.Unavailable;
        }
        catch (Exception exception) when (
            exception is DllNotFoundException or
                EntryPointNotFoundException or
                BadImageFormatException or
                SEHException)
        {
            return WindowsAttachmentShellStatus.Unavailable;
        }
        finally
        {
            if (itemIdList != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(itemIdList);
            }

            if (uninitialize)
            {
                NativeMethods.CoUninitialize();
            }
        }
    }

    public override string ToString() => nameof(WindowsAttachmentShell);

    private static class NativeMethods
    {
        [DllImport("ole32.dll", ExactSpelling = true)]
        internal static extern int CoInitializeEx(IntPtr reserved, uint coInit);

        [DllImport("ole32.dll", ExactSpelling = true)]
        internal static extern void CoUninitialize();

        [DllImport(
            "shell32.dll",
            CharSet = CharSet.Unicode,
            ExactSpelling = true)]
        internal static extern int SHParseDisplayName(
            string name,
            IntPtr bindContext,
            out IntPtr itemIdList,
            uint attributesIn,
            out uint attributesOut);

        [DllImport("shell32.dll", ExactSpelling = true)]
        internal static extern int SHOpenFolderAndSelectItems(
            IntPtr folderItemIdList,
            uint childCount,
            IntPtr childItemIdLists,
            uint flags);
    }
}
