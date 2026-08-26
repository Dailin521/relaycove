using RelayCove.App.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using WinDataPackageView = Windows.ApplicationModel.DataTransfer.DataPackageView;

namespace RelayCove.App.Platforms.Windows;

internal static class ClipboardImageAttachmentFactory
{
    internal static async Task<SelectedAttachmentFile> CreateAsync(
        WinDataPackageView dataView,
        DateTimeOffset capturedAt)
    {
        ArgumentNullException.ThrowIfNull(dataView);
        if (!dataView.Contains(StandardDataFormats.Bitmap))
        {
            throw new InvalidOperationException("The clipboard does not contain a bitmap.");
        }

        var bitmapReference = await dataView.GetBitmapAsync();
        using var source = await bitmapReference.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(source);
        var pixels = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Straight,
            new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);

        using var output = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Straight,
            decoder.PixelWidth,
            decoder.PixelHeight,
            decoder.DpiX > 0 ? decoder.DpiX : 96d,
            decoder.DpiY > 0 ? decoder.DpiY : 96d,
            pixels.DetachPixelData());
        await encoder.FlushAsync();

        if (output.Size is 0 or > int.MaxValue)
        {
            throw new InvalidOperationException("The clipboard image could not be encoded.");
        }

        using var input = output.GetInputStreamAt(0);
        using var reader = new DataReader(input);
        var byteCount = checked((int)output.Size);
        var loaded = await reader.LoadAsync(checked((uint)byteCount));
        if (loaded != byteCount)
        {
            throw new InvalidOperationException("The clipboard image could not be read.");
        }

        var pngBytes = new byte[byteCount];
        reader.ReadBytes(pngBytes);
        return CreateFromPng(pngBytes, capturedAt);
    }

    internal static SelectedAttachmentFile CreateFromPng(
        byte[] pngBytes,
        DateTimeOffset capturedAt)
    {
        ArgumentNullException.ThrowIfNull(pngBytes);
        if (pngBytes.Length == 0)
        {
            throw new ArgumentException("Screenshot PNG data cannot be empty.", nameof(pngBytes));
        }

        var fileName = $"screenshot-{capturedAt:yyyyMMdd-HHmmss}.png";
        return new SelectedAttachmentFile(
            fileName,
            "image/png",
            pngBytes.LongLength,
            cancellationToken =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult<Stream>(new MemoryStream(pngBytes, writable: false));
            },
            openPreviewStream: () => new MemoryStream(pngBytes, writable: false));
    }
}
