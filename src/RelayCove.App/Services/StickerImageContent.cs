using System.Security.Cryptography;
using System.Text;

namespace RelayCove.App.Services;

internal static class StickerImageContent
{
    internal const int MaximumBytes = 25 * 1024 * 1024;

    internal static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximumBytes, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length,
                maximumBytes - (int)output.Length + 1)), cancellationToken).ConfigureAwait(false);
            if (read == 0) return output.ToArray();
            if (output.Length + read > maximumBytes) throw new InvalidDataException("图片或目录文件过大。");
            output.Write(buffer, 0, read);
        }
    }

    internal static StickerMedia Validate(byte[] content)
    {
        if (content.Length > MaximumBytes) throw new InvalidDataException("图片不能超过 25 MiB。");
        var bytes = content.AsSpan();
        var format = bytes.Length >= 24 && bytes.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            && bytes.Slice(12, 4).SequenceEqual("IHDR"u8) ? ("image/png", ".png")
            : bytes.Length >= 4 && bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255 ? ("image/jpeg", ".jpg")
            : bytes.Length >= 13 && (bytes.StartsWith("GIF89a"u8) || bytes.StartsWith("GIF87a"u8)) ? ("image/gif", ".gif")
            : bytes.Length >= 16 && bytes.StartsWith("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8)
              && (bytes.Slice(12, 4).SequenceEqual("VP8 "u8) || bytes.Slice(12, 4).SequenceEqual("VP8L"u8)
                  || bytes.Slice(12, 4).SequenceEqual("VP8X"u8)) ? ("image/webp", ".webp")
            : throw new InvalidDataException("仅支持 PNG、JPEG、WebP 和 GIF 图片。");
        var hash = Convert.ToHexStringLower(SHA256.HashData(content));
        return new StickerMedia(content, format.Item1, hash + format.Item2, hash);
    }

    internal static string HashText(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    internal static bool IsHash(string hash) => hash.Length == 64 && hash.All(character =>
        character is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static async Task WriteAtomicAsync(string path, byte[] content, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, content, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
