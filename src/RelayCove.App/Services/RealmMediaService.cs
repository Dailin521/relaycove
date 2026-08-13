using RelayCove.Core;

namespace RelayCove.App.Services;

public sealed class RealmMediaService : IRealmMediaService, IDisposable
{
    private const long CacheBudgetBytes = 64L * 1024 * 1024;
    private const long ImageLimitBytes = 25L * 1024 * 1024;
    private readonly IClientSession _session;
    private readonly SemaphoreSlim _reads = new(4, 4);
    private readonly object _gate = new();
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private long _cacheBytes;
    private bool _disposed;

    public RealmMediaService(IClientSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public async Task<ImageSource> GetImageAsync(
        string sourceUrl,
        RealmMediaKind kind,
        CancellationToken cancellationToken = default)
    {
        if (kind == RealmMediaKind.File) throw new ArgumentOutOfRangeException(nameof(kind));
        var key = $"{kind}:{sourceUrl}";
        if (TryGet(key, out var cached)) return ToImageSource(cached);
        await _reads.WaitAsync(cancellationToken);
        try
        {
            if (TryGet(key, out cached)) return ToImageSource(cached);
            var result = await _session.GetRealmMediaAsync(
                new RealmMediaRequest(sourceUrl, kind, ImageLimitBytes),
                cancellationToken);
            Add(key, result.Content);
            return ToImageSource(result.Content);
        }
        finally
        {
            _reads.Release();
        }
    }

    public Task<RealmMediaResult> GetFileAsync(string sourceUrl, CancellationToken cancellationToken = default) =>
        _session.GetRealmMediaAsync(
            new RealmMediaRequest(
                sourceUrl,
                RealmMediaKind.File,
                Math.Min(100L * 1024 * 1024, _session.MaxFileUploadBytes)),
            cancellationToken);

    private bool TryGet(string key, out byte[] content)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var entry))
            {
                entry.LastUsed = DateTimeOffset.UtcNow;
                content = entry.Content;
                return true;
            }
        }
        content = [];
        return false;
    }

    private void Add(string key, byte[] content)
    {
        if (content.LongLength > CacheBudgetBytes) return;
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var existing))
            {
                _cacheBytes -= existing.Content.LongLength;
            }
            _cache[key] = new CacheEntry(content, DateTimeOffset.UtcNow);
            _cacheBytes += content.LongLength;
            while (_cacheBytes > CacheBudgetBytes && _cache.Count > 0)
            {
                var oldest = _cache.MinBy(pair => pair.Value.LastUsed);
                _cache.Remove(oldest.Key);
                _cacheBytes -= oldest.Value.Content.LongLength;
            }
        }
    }

    private static ImageSource ToImageSource(byte[] content) =>
        ImageSource.FromStream(() => new MemoryStream(content, writable: false));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate)
        {
            _cache.Clear();
            _cacheBytes = 0;
        }
        _reads.Dispose();
    }

    private sealed class CacheEntry(byte[] content, DateTimeOffset lastUsed)
    {
        public byte[] Content { get; } = content;
        public DateTimeOffset LastUsed { get; set; } = lastUsed;
    }
}
