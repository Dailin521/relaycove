using Windows.Storage.Streams;

namespace RelayCove.App.Platforms.Windows;

internal static class ComposerEmojiImageStream
{
    internal static async Task<IRandomAccessStream> CreateAsync(byte[] content, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // RichEdit's decoder can clone the stream. The .NET MemoryStream
        // adapter throws for CloneStream, leaving an empty object.
        var output = new InMemoryRandomAccessStream();
        try
        {
            using var source = new MemoryStream(content, writable: false);
            using var input = source.AsInputStream();
            await RandomAccessStream.CopyAsync(input, output).AsTask(cancellationToken);
            output.Seek(0);
            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }
}
