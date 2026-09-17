namespace RelayCove.App.Services;

public sealed class MauiPlatformInteractionService : IPlatformInteractionService
{
    public async Task CopyImageAsync(byte[] content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        cancellationToken.ThrowIfCancellationRequested();
        using var input = new global::Windows.Storage.Streams.InMemoryRandomAccessStream();
        using (var writer = new global::Windows.Storage.Streams.DataWriter(input.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(content);
            await writer.StoreAsync().AsTask(cancellationToken);
        }
        input.Seek(0);
        var decoder = await global::Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(input).AsTask(cancellationToken);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(
            global::Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
            global::Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied).AsTask(cancellationToken);
        using var png = new global::Windows.Storage.Streams.InMemoryRandomAccessStream();
        var encoder = await global::Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
            global::Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, png).AsTask(cancellationToken);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync().AsTask(cancellationToken);
        png.Seek(0);
        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var data = new global::Windows.ApplicationModel.DataTransfer.DataPackage();
            data.SetBitmap(global::Windows.Storage.Streams.RandomAccessStreamReference.CreateFromStream(png));
            global::Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
            // Materialize clipboard data before disposing streams, including after the app exits.
            global::Windows.ApplicationModel.DataTransfer.Clipboard.Flush();
        });
    }

    public async Task CopyTextAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        cancellationToken.ThrowIfCancellationRequested();
        await Clipboard.Default.SetTextAsync(text);
    }

    public async Task OpenUriAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);
        cancellationToken.ThrowIfCancellationRequested();
        if (!await Launcher.Default.OpenAsync(uri))
        {
            throw new InvalidOperationException("The system could not open the message link.");
        }
    }
}
