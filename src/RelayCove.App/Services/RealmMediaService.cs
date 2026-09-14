using RelayCove.App.Platforms.Windows;
using RelayCove.Core;

namespace RelayCove.App.Services;

public sealed class RealmMediaService : IRealmMediaService, IDisposable
{
    private const long CacheBudgetBytes = 64L * 1024 * 1024;
    private const long ImageLimitBytes = 25L * 1024 * 1024;
    private readonly IClientSession _session;
    private readonly AvatarCache _avatars;
    private readonly SemaphoreSlim _reads = new(4, 4);
    private readonly object _gate = new();
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private long _cacheBytes;
    private bool _disposed;

    public event EventHandler<AvatarChangedEventArgs>? AvatarChanged;

    public RealmMediaService(IClientSession session, AvatarCache avatars)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _avatars = avatars;
        _avatars.AvatarChanged += OnAvatarChanged;
        _session.StateChanged += OnSessionStateChanged;
    }

    private void OnAvatarChanged(object? sender, AvatarChangedEventArgs args) => AvatarChanged?.Invoke(this, args);

    private void OnSessionStateChanged(object? sender, ClientStateChangedEventArgs args)
    {
        if (_session.AccountId is not null) return;
        lock (_gate) { _cache.Clear(); _cacheBytes = 0; }
    }

    public async Task<ImageSource> GetImageAsync(
        string sourceUrl,
        RealmMediaKind kind,
        CancellationToken cancellationToken = default)
    {
        if (kind == RealmMediaKind.File) throw new ArgumentOutOfRangeException(nameof(kind));
        var accountId = _session.AccountId ?? throw new InvalidOperationException("No account is active.");
        var key = CreateCacheKey(accountId, kind, sourceUrl);
        if (kind == RealmMediaKind.Avatar)
        {
            var avatar = await _avatars.GetAsync(sourceUrl, cancellationToken).ConfigureAwait(false);
            if (_session.AccountId != accountId) throw new OperationCanceledException();
            return Add(key, avatar.Media.Content);
        }
        if (TryGet(key, out var cached)) return cached;
        await _reads.WaitAsync(cancellationToken);
        try
        {
            if (TryGet(key, out cached)) return cached;
            var result = await _session.GetRealmMediaAsync(
                new RealmMediaRequest(sourceUrl, kind, ImageLimitBytes),
                cancellationToken);
            return Add(key, result.Content);
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

    public Task<RealmMediaDownloadResult> DownloadFileAsync(
        string sourceUrl,
        Stream destination,
        IProgress<RealmMediaTransferProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        _session.DownloadRealmMediaAsync(
            new RealmMediaRequest(
                sourceUrl,
                RealmMediaKind.File,
                Math.Min(100L * 1024 * 1024, _session.MaxFileUploadBytes)),
            destination,
            progress,
            cancellationToken);

    private bool TryGet(string key, out ImageSource source)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var entry))
            {
                entry.LastUsed = DateTimeOffset.UtcNow;
                source = entry.Source;
                return true;
            }
        }
        source = null!;
        return false;
    }

    private ImageSource Add(string key, byte[] content)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var existing))
            {
                if (existing.Content.AsSpan().SequenceEqual(content)) return existing.Source;
                _cacheBytes -= existing.Content.LongLength;
            }
            var source = ToImageSource(content);
            if (_disposed || content.LongLength > CacheBudgetBytes) return source;
            _cache[key] = new CacheEntry(content, source, DateTimeOffset.UtcNow);
            _cacheBytes += content.LongLength;
            while (_cacheBytes > CacheBudgetBytes && _cache.Count > 0)
            {
                var oldest = _cache.MinBy(pair => pair.Value.LastUsed);
                _cache.Remove(oldest.Key);
                _cacheBytes -= oldest.Value.Content.LongLength;
            }
            return source;
        }
    }

    private static ImageSource ToImageSource(byte[] content) =>
        new RealmImageSource
        {
            Stream = _ => Task.FromResult<Stream>(new MemoryStream(content, writable: false))
        };

    internal static string CreateCacheKey(AccountId accountId, RealmMediaKind kind, string sourceUrl) =>
        $"{accountId.Value}:{kind}:{sourceUrl}";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _avatars.AvatarChanged -= OnAvatarChanged;
        _session.StateChanged -= OnSessionStateChanged;
        lock (_gate)
        {
            _cache.Clear();
            _cacheBytes = 0;
        }
        _reads.Dispose();
    }

    private sealed class CacheEntry(byte[] content, ImageSource source, DateTimeOffset lastUsed)
    {
        public byte[] Content { get; } = content;
        public ImageSource Source { get; } = source;
        public DateTimeOffset LastUsed { get; set; } = lastUsed;
    }
}
