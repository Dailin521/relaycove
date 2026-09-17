using RelayCove.App.Platforms.Windows;
using System.Drawing;

namespace RelayCove.App.Tests;

public sealed class GifTransparencyTests
{
    [Fact]
    public void HasTransparentPixels_WhenGraphicControlExtensionHasTransparencyFlag_ReturnsTrue()
    {
        var content = CreateGif(graphicControlPackedFields: 0x01);

        Assert.True(GifTransparency.HasTransparentPixels(content));
    }

    [Fact]
    public void HasTransparentPixels_WhenGraphicControlExtensionHasNoTransparencyFlag_ReturnsFalse()
    {
        var content = CreateGif(graphicControlPackedFields: 0x00);

        Assert.False(GifTransparency.HasTransparentPixels(content));
    }

    [Fact]
    public void HasTransparentPixels_WhenContentIsNotGif_ReturnsFalse()
    {
        Assert.False(GifTransparency.HasTransparentPixels("not a gif"u8));
    }

    [Fact]
    public void RenderFrame_WhenGifHasTransparentPaletteIndex_PreservesAlpha()
    {
        var content = Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7");

        Assert.True(TransparentGifFrameRenderer.TryCreate(content, out var renderer));
        using (renderer)
        {
            using var stream = new MemoryStream(renderer!.RenderFramePng(0));
            using var bitmap = new Bitmap(stream);

            Assert.Equal(0, bitmap.GetPixel(0, 0).A);
        }
    }

    private static byte[] CreateGif(byte graphicControlPackedFields) =>
    [.. "GIF89a"u8, 0x01, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00,
        0x21, 0xf9, 0x04, graphicControlPackedFields, 0x00, 0x00, 0x00, 0x00, 0x3b];
}
