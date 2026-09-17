using System.Drawing;
using System.Drawing.Imaging;
using DrawingColor = System.Drawing.Color;
using DrawingImage = System.Drawing.Image;
using DrawingImageFormat = System.Drawing.Imaging.ImageFormat;

namespace RelayCove.App.Platforms.Windows;

internal sealed class TransparentGifFrameRenderer : IDisposable
{
    private const int FrameDelayPropertyId = 0x5100;
    private readonly MemoryStream _stream;
    private readonly DrawingImage _image;
    private readonly FrameDimension _frameDimension = FrameDimension.Time;
    private readonly int[] _frameDelays;

    private TransparentGifFrameRenderer(MemoryStream stream, DrawingImage image, int frameCount)
    {
        _stream = stream;
        _image = image;
        FrameCount = frameCount;
        _frameDelays = ReadFrameDelays(image, frameCount);
    }

    public int FrameCount { get; }

    public static bool TryCreate(byte[] content, out TransparentGifFrameRenderer? renderer)
    {
        renderer = null;
        if (!GifTransparency.HasTransparentPixels(content)) return false;

        var stream = new MemoryStream(content, writable: false);
        try
        {
            var image = DrawingImage.FromStream(stream, useEmbeddedColorManagement: true, validateImageData: true);
            if (!image.FrameDimensionsList.Contains(FrameDimension.Time.Guid))
            {
                image.Dispose();
                stream.Dispose();
                return false;
            }

            var frameCount = image.GetFrameCount(FrameDimension.Time);
            if (frameCount <= 0)
            {
                image.Dispose();
                stream.Dispose();
                return false;
            }

            renderer = new TransparentGifFrameRenderer(stream, image, frameCount);
            return true;
        }
        catch (ArgumentException)
        {
            stream.Dispose();
            return false;
        }
    }

    public TimeSpan GetFrameDelay(int frameIndex)
    {
        var centiseconds = _frameDelays[Math.Clamp(frameIndex, 0, _frameDelays.Length - 1)];
        return TimeSpan.FromMilliseconds(Math.Clamp(centiseconds * 10, 20, 10_000));
    }

    public byte[] RenderFramePng(int frameIndex)
    {
        _image.SelectActiveFrame(_frameDimension, Math.Clamp(frameIndex, 0, FrameCount - 1));
        using var bitmap = new Bitmap(_image.Width, _image.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(DrawingColor.Transparent);
            graphics.DrawImageUnscaled(_image, 0, 0);
        }

        using var output = new MemoryStream();
        bitmap.Save(output, DrawingImageFormat.Png);
        return output.ToArray();
    }

    public void Dispose()
    {
        _image.Dispose();
        _stream.Dispose();
    }

    private static int[] ReadFrameDelays(DrawingImage image, int frameCount)
    {
        try
        {
            var property = image.GetPropertyItem(FrameDelayPropertyId);
            if (property?.Value is not { } value || value.Length < frameCount * sizeof(int))
                return Enumerable.Repeat(10, frameCount).ToArray();
            return Enumerable.Range(0, frameCount)
                .Select(index => BitConverter.ToInt32(value, index * sizeof(int)))
                .ToArray();
        }
        catch (ArgumentException)
        {
            return Enumerable.Repeat(10, frameCount).ToArray();
        }
    }
}
