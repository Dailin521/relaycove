using RelayCove.App.Platforms.Windows;

namespace RelayCove.App.Tests;

public sealed class ComposerEmojiImageStreamTests
{
    [Fact]
    public async Task CreateAsync_WhenNativeDecoderClonesStream_KeepsImageDataAfterOriginalCloses()
    {
        byte[] content = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a6ioAAAAASUVORK5CYII=");
        using var stream = await ComposerEmojiImageStream.CreateAsync(content, CancellationToken.None);
        using var clone = stream.CloneStream();
        stream.Dispose();
        using var input = clone.AsStreamForRead();
        using var copy = new MemoryStream();
        await input.CopyToAsync(copy);

        Assert.Equal(content, copy.ToArray());
    }

    [Fact]
    public async Task CreateAsync_WhenCanceled_DoesNotReturnImageData()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ComposerEmojiImageStream.CreateAsync([1, 2, 3], cancellation.Token));
    }
}
