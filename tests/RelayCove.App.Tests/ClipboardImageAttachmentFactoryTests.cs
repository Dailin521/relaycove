using RelayCove.App.Platforms.Windows;

namespace RelayCove.App.Tests;

public sealed class ClipboardImageAttachmentFactoryTests
{
    [Fact]
    public async Task CreateFromPng_WhenScreenshotIsValid_CreatesReusableImageAttachment()
    {
        byte[] bytes = [1, 2, 3, 4];
        var capturedAt = new DateTimeOffset(2026, 8, 26, 12, 34, 56, TimeSpan.FromHours(8));

        var attachment = ClipboardImageAttachmentFactory.CreateFromPng(bytes, capturedAt);

        Assert.Equal("screenshot-20260826-123456.png", attachment.FileName);
        Assert.Equal("image/png", attachment.ContentType);
        Assert.Equal(bytes.Length, attachment.Length);
        Assert.True(attachment.HasPreview);
        await using var first = await attachment.OpenReadAsync();
        await using var second = await attachment.OpenReadAsync();
        await using var preview = attachment.OpenPreviewStream();
        Assert.NotSame(first, second);
        Assert.Equal(bytes, ReadAllBytes(first));
        Assert.Equal(bytes, ReadAllBytes(second));
        Assert.Equal(bytes, ReadAllBytes(preview));
    }

    private static byte[] ReadAllBytes(Stream stream)
    {
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
