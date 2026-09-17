namespace RelayCove.App.Platforms.Windows;

// A separate source type limits decoded-image reuse to authenticated Realm media.
internal sealed class RealmImageSource : StreamImageSource;

// WinUI's animated GIF presentation can flatten an indexed transparent color
// onto white. Keep the original bytes so message rendering can compose those
// frames to PNG while preserving their alpha channel.
internal sealed class RealmGifImageSource : StreamImageSource
{
    public RealmGifImageSource(byte[] content)
    {
        Content = content ?? throw new ArgumentNullException(nameof(content));
        Stream = _ => Task.FromResult<Stream>(new MemoryStream(Content, writable: false));
    }

    public byte[] Content { get; }
    public bool HasTransparentPixels => GifTransparency.HasTransparentPixels(Content);
}

internal static class GifTransparency
{
    internal static bool HasTransparentPixels(ReadOnlySpan<byte> content)
    {
        if (content.Length < 14 ||
            !(content.StartsWith("GIF87a"u8) || content.StartsWith("GIF89a"u8))) return false;

        // Graphic Control Extensions contain the transparent palette-index flag.
        for (var index = 6; index <= content.Length - 8; index++)
        {
            if (content[index] == 0x21 && content[index + 1] == 0xf9 && content[index + 2] == 0x04 &&
                (content[index + 3] & 0x01) != 0) return true;
        }

        return false;
    }
}
