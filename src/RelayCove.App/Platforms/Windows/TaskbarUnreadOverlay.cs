using System.Runtime.InteropServices;

namespace RelayCove.App.Platforms.Windows;

internal sealed class TaskbarUnreadOverlay : IDisposable
{
    private ITaskbarList3? _taskbar;

    public bool TryApply(nint windowHandle, int count, bool isTruncated)
    {
        if (windowHandle == 0) return false;
        if (count <= 0 && !isTruncated) return TryClear(windowHandle);
        if (!TryGetTaskbar(out var taskbar)) return false;

        var bits = TaskbarUnreadIconRenderer.Render(count, isTruncated);
        var icon = CreateIcon(
            0,
            TaskbarUnreadIconRenderer.IconSize,
            TaskbarUnreadIconRenderer.IconSize,
            1,
            32,
            bits.AndMask,
            bits.XorBits);
        if (icon == 0) return false;
        try
        {
            return taskbar.SetOverlayIcon(windowHandle, icon, bits.Description) >= 0;
        }
        finally
        {
            _ = DestroyIcon(icon);
        }
    }

    public bool TryClear(nint windowHandle)
    {
        if (windowHandle == 0 || !TryGetTaskbar(out var taskbar)) return false;
        return taskbar.SetOverlayIcon(windowHandle, 0, string.Empty) >= 0;
    }

    private bool TryGetTaskbar(out ITaskbarList3 taskbar)
    {
        if (_taskbar is not null)
        {
            taskbar = _taskbar;
            return true;
        }

        try
        {
            var taskbarType = Type.GetTypeFromCLSID(new Guid("56FDF344-FD6D-11d0-958A-006097C9A090"));
            if (taskbarType is null)
            {
                taskbar = null!;
                return false;
            }
            var candidate = (ITaskbarList3)(Activator.CreateInstance(taskbarType) ??
                                            throw new InvalidOperationException("Taskbar COM activation failed."));
            if (candidate.HrInit() < 0)
            {
                if (Marshal.IsComObject(candidate)) Marshal.FinalReleaseComObject(candidate);
                taskbar = null!;
                return false;
            }
            _taskbar = candidate;
            taskbar = candidate;
            return true;
        }
        catch (Exception)
        {
            taskbar = null!;
            return false;
        }
    }

    public void Dispose()
    {
        if (_taskbar is null) return;
        if (Marshal.IsComObject(_taskbar)) Marshal.FinalReleaseComObject(_taskbar);
        _taskbar = null;
    }

    [DllImport("user32.dll")]
    private static extern nint CreateIcon(
        nint instance,
        int width,
        int height,
        byte planes,
        byte bitsPerPixel,
        byte[] andBits,
        byte[] xorBits);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);

    [ComImport]
    [Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEA84")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        [PreserveSig] int HrInit();
        [PreserveSig] int AddTab(nint windowHandle);
        [PreserveSig] int DeleteTab(nint windowHandle);
        [PreserveSig] int ActivateTab(nint windowHandle);
        [PreserveSig] int SetActiveAlt(nint windowHandle);
        [PreserveSig] int MarkFullscreenWindow(nint windowHandle, [MarshalAs(UnmanagedType.Bool)] bool isFullscreen);
        [PreserveSig] int SetProgressValue(nint windowHandle, ulong completed, ulong total);
        [PreserveSig] int SetProgressState(nint windowHandle, uint flags);
        [PreserveSig] int RegisterTab(nint tabHandle, nint ownerHandle);
        [PreserveSig] int UnregisterTab(nint tabHandle);
        [PreserveSig] int SetTabOrder(nint tabHandle, nint insertBeforeHandle);
        [PreserveSig] int SetTabActive(nint tabHandle, nint ownerHandle, uint reserved);
        [PreserveSig] int ThumbBarAddButtons(nint windowHandle, uint buttonCount, nint buttons);
        [PreserveSig] int ThumbBarUpdateButtons(nint windowHandle, uint buttonCount, nint buttons);
        [PreserveSig] int ThumbBarSetImageList(nint windowHandle, nint imageList);
        [PreserveSig] int SetOverlayIcon(
            nint windowHandle,
            nint icon,
            [MarshalAs(UnmanagedType.LPWStr)] string description);
        [PreserveSig] int SetThumbnailTooltip(nint windowHandle, [MarshalAs(UnmanagedType.LPWStr)] string tooltip);
        [PreserveSig] int SetThumbnailClip(nint windowHandle, nint clipRectangle);
    }
}

internal static class TaskbarUnreadIconRenderer
{
    internal const int IconSize = 16;
    internal const int AndMaskStride = ((IconSize + 31) / 32) * 4;
    private static readonly byte[,] DigitRows =
    {
        { 0b111, 0b101, 0b101, 0b101, 0b111 },
        { 0b010, 0b110, 0b010, 0b010, 0b111 },
        { 0b111, 0b001, 0b111, 0b100, 0b111 },
        { 0b111, 0b001, 0b111, 0b001, 0b111 },
        { 0b101, 0b101, 0b111, 0b001, 0b001 },
        { 0b111, 0b100, 0b111, 0b001, 0b111 },
        { 0b111, 0b100, 0b111, 0b101, 0b111 },
        { 0b111, 0b001, 0b010, 0b010, 0b010 },
        { 0b111, 0b101, 0b111, 0b101, 0b111 },
        { 0b111, 0b101, 0b111, 0b001, 0b111 }
    };

    internal static TaskbarUnreadBadgeBits Render(int count, bool isTruncated)
    {
        var andMask = Enumerable.Repeat((byte)0xFF, AndMaskStride * IconSize).ToArray();
        var xorBits = new byte[IconSize * IconSize * 4];
        for (var y = 0; y < IconSize; y++)
        {
            for (var x = 0; x < IconSize; x++)
            {
                var dx = x - 7.5d;
                var dy = y - 7.5d;
                if (dx * dx + dy * dy > 56.25d) continue;
                SetOpaque(andMask, x, y);
                SetPixel(xorBits, x, y, 255, 77, 90);
            }
        }

        var description = count > 0
            ? $"{(count > 99 ? "99+" : count)} 条未读消息"
            : "有未读消息";
        if (count > 0)
        {
            var display = Math.Min(count, 99).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var width = display.Length == 1 ? 6 : 14;
            var startX = (IconSize - width) / 2;
            for (var index = 0; index < display.Length; index++)
            {
                DrawDigit(xorBits, display[index] - '0', startX + index * 8, 3);
            }
        }
        else if (isTruncated)
        {
            for (var y = 3; y <= 9; y++)
            {
                SetPixel(xorBits, 7, y, 255, 255, 255);
                SetPixel(xorBits, 8, y, 255, 255, 255);
            }
            SetPixel(xorBits, 7, 12, 255, 255, 255);
            SetPixel(xorBits, 8, 12, 255, 255, 255);
        }

        return new TaskbarUnreadBadgeBits(andMask, xorBits, description);
    }

    private static void DrawDigit(byte[] xorBits, int digit, int startX, int startY)
    {
        const int scale = 2;
        for (var row = 0; row < 5; row++)
        {
            for (var column = 0; column < 3; column++)
            {
                if ((DigitRows[digit, row] & (1 << (2 - column))) == 0) continue;
                for (var offsetY = 0; offsetY < scale; offsetY++)
                {
                    for (var offsetX = 0; offsetX < scale; offsetX++)
                    {
                        SetPixel(
                            xorBits,
                            startX + column * scale + offsetX,
                            startY + row * scale + offsetY,
                            255,
                            255,
                            255);
                    }
                }
            }
        }
    }

    private static void SetOpaque(byte[] andMask, int x, int y)
    {
        var nativeY = IconSize - 1 - y;
        var index = nativeY * AndMaskStride + x / 8;
        andMask[index] = (byte)(andMask[index] & ~(0x80 >> x % 8));
    }

    private static void SetPixel(byte[] xorBits, int x, int y, byte red, byte green, byte blue)
    {
        if (x is < 0 or >= IconSize || y is < 0 or >= IconSize) return;
        var nativeY = IconSize - 1 - y;
        var index = (nativeY * IconSize + x) * 4;
        xorBits[index] = blue;
        xorBits[index + 1] = green;
        xorBits[index + 2] = red;
        xorBits[index + 3] = 255;
    }
}

internal sealed record TaskbarUnreadBadgeBits(byte[] AndMask, byte[] XorBits, string Description);
