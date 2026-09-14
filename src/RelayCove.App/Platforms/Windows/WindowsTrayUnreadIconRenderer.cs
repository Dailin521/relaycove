using System.Runtime.InteropServices;

namespace RelayCove.App.Platforms.Windows;

internal static class WindowsTrayUnreadIconRenderer
{
    internal static nint Create(nint artwork)
    {
        if (artwork == 0 || !GetIconInfo(artwork, out var source)) return 0;
        nint bitmap = 0;
        nint mask = 0;
        nint dc = 0;
        nint previous = 0;
        try
        {
            if (GetObject(source.ColorBitmap, Marshal.SizeOf<NativeBitmap>(), out var dimensions) == 0) return 0;
            var width = dimensions.Width;
            var height = dimensions.Height;
            var header = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                Width = width,
                Height = height,
                Planes = 1,
                BitCount = 32
            };
            bitmap = CreateDIBSection(0, ref header, 0, out var bits, 0, 0);
            dc = CreateCompatibleDC(0);
            if (bitmap == 0 || dc == 0) return 0;
            previous = SelectObject(dc, bitmap);
            if (previous == 0 || previous == -1) return 0;

            var pixels = new int[width * height];
            Marshal.Copy(pixels, 0, bits, pixels.Length);
            if (!DrawIconEx(dc, 0, 0, artwork, width, height, 0, 0, 3) || !GdiFlush()) return 0;
            Marshal.Copy(bits, pixels, 0, pixels.Length);

            // Paint a red dot with a white rim on the existing R artwork. Preserve
            // its size and alpha; the resulting HICON stays 32-bit during blinking.
            var radius = Math.Min(width, height) * 0.22;
            var centerX = width - radius - 0.5;
            var centerY = radius - 0.5;
            var innerRadius = Math.Min(width, height) * 0.17;
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var distance = Math.Sqrt(Math.Pow(x - centerX, 2) + Math.Pow(y - centerY, 2));
                    if (distance > radius) continue;
                    pixels[(height - 1 - y) * width + x] = distance <= innerRadius
                        ? unchecked((int)0xFFFF4D5A)
                        : unchecked((int)0xFFFFFFFF);
                }
            }

            Marshal.Copy(pixels, 0, bits, pixels.Length);
            _ = SelectObject(dc, previous);
            previous = 0;
            var maskStride = ((width + 15) / 16) * 2;
            mask = CreateBitmap(width, height, 1, 1, new byte[maskStride * height]);
            if (mask == 0) return 0;
            var iconInfo = new IconInfo { IsIcon = 1, ColorBitmap = bitmap, MaskBitmap = mask };
            return CreateIconIndirect(ref iconInfo);
        }
        finally
        {
            if (previous != 0 && previous != -1) _ = SelectObject(dc, previous);
            if (bitmap != 0) _ = DeleteObject(bitmap);
            if (mask != 0) _ = DeleteObject(mask);
            if (dc != 0) _ = DeleteDC(dc);
            if (source.ColorBitmap != 0) _ = DeleteObject(source.ColorBitmap);
            if (source.MaskBitmap != 0) _ = DeleteObject(source.MaskBitmap);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public int IsIcon;
        public uint HotspotX;
        public uint HotspotY;
        public nint MaskBitmap;
        public nint ColorBitmap;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBitmap
    {
        public int Type;
        public int Width;
        public int Height;
        public int WidthBytes;
        public ushort Planes;
        public ushort BitsPixel;
        public nint Bits;
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

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIconInfo(nint icon, out IconInfo info);
    [DllImport("gdi32.dll", EntryPoint = "GetObjectW", ExactSpelling = true)]
    private static extern int GetObject(nint obj, int size, out NativeBitmap bitmap);
    [DllImport("gdi32.dll")]
    private static extern nint CreateDIBSection(nint dc, ref BitmapInfoHeader header, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GdiFlush();
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DrawIconEx(nint dc, int x, int y, nint icon, int width, int height, uint step, nint brush, uint flags);
    [DllImport("gdi32.dll")]
    private static extern nint CreateBitmap(int width, int height, uint planes, uint bitsPerPixel, byte[] bits);
    [DllImport("user32.dll")]
    private static extern nint CreateIconIndirect(ref IconInfo info);
}
