using System.Runtime.InteropServices;
using RelayCove.App.Platforms.Windows;

namespace RelayCove.App.Tests;

public sealed class WindowsTrayIconRenderingTests
{
    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(32)]
    [InlineData(48)]
    public void CreateUnreadIcon_WhenUnreadIsShown_AddsRedDotAndPreservesArtworkAcrossBlinking(int size)
    {
        using var commonControls = new CommonControlsContext();
        var artwork = CreateColorArtwork(size, transparentBorder: true);
        var unread = WindowsTrayUnreadIconRenderer.Create(artwork);
        var transparent = WindowsTrayIconController.CreateTransparentIcon(0);
        var imageList = Native.ImageList_Create(size, size, 0x0021, 1, 0);
        try
        {
            Assert.NotEqual(nint.Zero, unread);
            Assert.NotEqual(nint.Zero, imageList);
            Assert.Equal(0, Native.ImageList_ReplaceIcon(imageList, -1, artwork));
            var originalPixels = DrawImageList(imageList, size);
            Assert.Equal(0, Native.ImageList_ReplaceIcon(imageList, 0, unread));
            var unreadPixels = DrawImageList(imageList, size);
            Assert.Contains(unreadPixels, pixel => pixel == 0x00FF4D5A);
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    if (x < size / 2 || y >= size / 2)
                        Assert.Equal(originalPixels[y * size + x], unreadPixels[y * size + x]);
                }
            }

            Assert.Equal(0, Native.ImageList_ReplaceIcon(imageList, 0, transparent));
            Assert.All(DrawImageList(imageList, size), pixel => Assert.Equal(Background, pixel));
            Assert.Equal(0, Native.ImageList_ReplaceIcon(imageList, 0, unread));
            Assert.Equal(unreadPixels, DrawImageList(imageList, size));
            Assert.Equal(0, Native.ImageList_ReplaceIcon(imageList, 0, artwork));
            Assert.Equal(originalPixels, DrawImageList(imageList, size));
        }
        finally
        {
            if (imageList != 0) _ = Native.ImageList_Destroy(imageList);
            if (transparent != 0) _ = Native.DestroyIcon(transparent);
            if (unread != 0) _ = Native.DestroyIcon(unread);
            if (artwork != 0) _ = Native.DestroyIcon(artwork);
        }
    }

    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(32)]
    [InlineData(48)]
    public void CreateTransparentIcon_WhenReplacingColorArtwork_ClearsAndRestoresImageListSlot(int size)
    {
        // Use the shell's v6 image list: v5 does not reproduce the monochrome-icon defect.
        // All drawing is into memory; no HWND, tray registration, screenshot or account is used.
        using var commonControls = new CommonControlsContext();
        var version = new CommonControlsVersion { Size = (uint)Marshal.SizeOf<CommonControlsVersion>() };
        Assert.Equal(0, Native.DllGetVersion(ref version));
        Assert.True(version.Major >= 6, "The regression must exercise Common Controls v6.");
        var artwork = CreateColorArtwork();
        var transparent = WindowsTrayIconController.CreateTransparentIcon(0);
        var imageList = Native.ImageList_Create(size, size, 0x0021, 1, 0); // ILC_COLOR32 | ILC_MASK
        try
        {
            Assert.NotEqual(nint.Zero, artwork);
            Assert.NotEqual(nint.Zero, transparent);
            Assert.NotEqual(nint.Zero, imageList);
            Assert.Equal(0, Native.ImageList_ReplaceIcon(imageList, -1, artwork));
            var visiblePixels = DrawImageList(imageList, size);
            Assert.Contains(visiblePixels, pixel => pixel != Background);

            for (var cycle = 0; cycle < 3; cycle++)
            {
                Assert.Equal(0, Native.ImageList_ReplaceIcon(imageList, 0, transparent));
                Assert.Equal(0, DrawImageList(imageList, size).Count(pixel => pixel != Background));
                Assert.Equal(0, Native.ImageList_ReplaceIcon(imageList, 0, artwork));
                Assert.Equal(visiblePixels, DrawImageList(imageList, size));
                Assert.Equal(1, Native.ImageList_GetImageCount(imageList));
            }
        }
        finally
        {
            if (imageList != 0) _ = Native.ImageList_Destroy(imageList);
            if (transparent != 0) _ = Native.DestroyIcon(transparent);
            if (artwork != 0) _ = Native.DestroyIcon(artwork);
        }
    }

    private const int Background = 0x00345678;

    private static nint CreateColorArtwork(int size = 48, bool transparentBorder = false)
    {
        var pixels = new byte[size * size * 4];
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            var x = offset / 4 % size;
            var y = offset / 4 / size;
            if (transparentBorder && (x == 0 || y == 0 || x == size - 1 || y == size - 1)) continue;
            pixels[offset] = (byte)(0x40 + (transparentBorder ? y : 0));
            pixels[offset + 1] = 0x80;
            pixels[offset + 2] = 0xC0;
            pixels[offset + 3] = 0xFF;
        }
        return Native.CreateIcon(0, size, size, 1, 32, new byte[((size + 15) / 16) * 2 * size], pixels);
    }

    private static int[] DrawImageList(nint imageList, int size)
    {
        var header = new BitmapInfoHeader
        {
            Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
            Width = size,
            Height = -size,
            Planes = 1,
            BitCount = 32
        };
        var bitmap = Native.CreateDIBSection(0, ref header, 0, out var bits, 0, 0);
        var dc = Native.CreateCompatibleDC(0);
        var previous = nint.Zero;
        try
        {
            Assert.NotEqual(nint.Zero, bitmap);
            Assert.NotEqual(nint.Zero, dc);
            previous = Native.SelectObject(dc, bitmap);
            Assert.NotEqual(nint.Zero, previous);
            var pixels = Enumerable.Repeat(Background, size * size).ToArray();
            Marshal.Copy(pixels, 0, bits, pixels.Length);
            Assert.True(Native.ImageList_Draw(imageList, 0, dc, 0, 0, 1)); // ILD_TRANSPARENT
            Assert.True(Native.GdiFlush());
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            // RGB is visible output; the destination DIB's alpha is not displayed by GDI.
            return pixels.Select(pixel => pixel & 0x00FFFFFF).ToArray();
        }
        finally
        {
            if (previous != 0) _ = Native.SelectObject(dc, previous);
            if (bitmap != 0) _ = Native.DeleteObject(bitmap);
            if (dc != 0) _ = Native.DeleteDC(dc);
        }
    }

    private sealed class CommonControlsContext : IDisposable
    {
        private readonly nint _context;
        private readonly nuint _cookie;

        internal CommonControlsContext()
        {
            var data = new ActivationContext
            {
                Size = (uint)Marshal.SizeOf<ActivationContext>(),
                Source = Path.Combine(AppContext.BaseDirectory, "Fixtures", "CommonControls.manifest")
            };
            _context = Native.CreateActCtx(ref data);
            Assert.NotEqual((nint)(-1), _context);
            if (!Native.ActivateActCtx(_context, out _cookie))
            {
                Native.ReleaseActCtx(_context);
                Assert.Fail("Cannot activate Common Controls v6 for the rendering regression.");
            }
        }

        public void Dispose()
        {
            _ = Native.DeactivateActCtx(0, _cookie);
            Native.ReleaseActCtx(_context);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ActivationContext
    {
        public uint Size;
        public uint Flags;
        [MarshalAs(UnmanagedType.LPWStr)] public string Source;
        public ushort ProcessorArchitecture;
        public ushort LanguageId;
        public nint AssemblyDirectory;
        public nint ResourceName;
        public nint ApplicationName;
        public nint Module;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CommonControlsVersion
    {
        public uint Size;
        public uint Major;
        public uint Minor;
        public uint Build;
        public uint Platform;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ColorsUsed;
        public uint ColorsImportant;
    }

    private static class Native
    {
        [DllImport("kernel32.dll", EntryPoint = "CreateActCtxW", ExactSpelling = true)]
        internal static extern nint CreateActCtx(ref ActivationContext context);
        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ActivateActCtx(nint context, out nuint cookie);
        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeactivateActCtx(uint flags, nuint cookie);
        [DllImport("kernel32.dll")]
        internal static extern void ReleaseActCtx(nint context);
        [DllImport("user32.dll")]
        internal static extern nint CreateIcon(nint instance, int width, int height, byte planes, byte bitsPerPixel, byte[] andMask, byte[] xorMask);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyIcon(nint icon);
        [DllImport("gdi32.dll")]
        internal static extern nint CreateDIBSection(nint dc, ref BitmapInfoHeader header, uint usage, out nint bits, nint section, uint offset);
        [DllImport("gdi32.dll")]
        internal static extern nint CreateCompatibleDC(nint dc);
        [DllImport("gdi32.dll")]
        internal static extern nint SelectObject(nint dc, nint obj);
        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeleteObject(nint obj);
        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeleteDC(nint dc);
        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GdiFlush();
        [DllImport("comctl32.dll")]
        internal static extern int DllGetVersion(ref CommonControlsVersion version);
        [DllImport("comctl32.dll")]
        internal static extern nint ImageList_Create(int width, int height, uint flags, int initial, int grow);
        [DllImport("comctl32.dll")]
        internal static extern int ImageList_ReplaceIcon(nint imageList, int index, nint icon);
        [DllImport("comctl32.dll")]
        internal static extern int ImageList_GetImageCount(nint imageList);
        [DllImport("comctl32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ImageList_Draw(nint imageList, int index, nint dc, int x, int y, uint style);
        [DllImport("comctl32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ImageList_Destroy(nint imageList);
    }
}
