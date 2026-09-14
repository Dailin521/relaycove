using System.Runtime.CompilerServices;

namespace RelayCove.App.Platforms.Windows;

// Keep only weak references: displayed images own the decoded bitmaps, and the
// existing media cache owns the source keys. This adds no bitmap retention budget.
internal sealed class DecodedImageCache<TImage> where TImage : class
{
    private readonly ConditionalWeakTable<object, Entry> _entries = new();

    internal async Task<TImage?> GetAsync(
        object source,
        Func<CancellationToken, Task<TImage?>> decodeAsync,
        CancellationToken cancellationToken = default)
    {
        var entry = _entries.GetValue(source, static _ => new Entry());
        await entry.Gate.WaitAsync(cancellationToken);
        try
        {
            if (entry.Image is not null && entry.Image.TryGetTarget(out var cached)) return cached;
            var image = await decodeAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (image is not null) entry.Image = new WeakReference<TImage>(image);
            return image;
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    private sealed class Entry
    {
        internal SemaphoreSlim Gate { get; } = new(1, 1);
        internal WeakReference<TImage>? Image { get; set; }
    }
}
