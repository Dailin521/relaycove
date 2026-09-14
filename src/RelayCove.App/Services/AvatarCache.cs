using RelayCove.Core;

namespace RelayCove.App.Services;

/// <summary>Account-scoped avatar files, read locally before a coalesced background refresh.</summary>
public sealed class AvatarCache : IDisposable
{
    private const int MaximumAvatarBytes = 1024 * 1024;
    private readonly IClientSession _session;
    private readonly string _cacheRoot;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _files = new(1, 1);
    private readonly SemaphoreSlim _downloads = new(4, 4);
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly HashSet<AccountId> _clearingAccounts = [];
    private AccountId? _accountId;
    private bool _connected;
    private bool _disposed;
    private long _generation;

    public event EventHandler<AvatarChangedEventArgs>? AvatarChanged;

    public AvatarCache(IClientSession session) : this(session, FileSystem.CacheDirectory) { }

    internal AvatarCache(IClientSession session, string cacheRoot)
    {
        _session = session;
        _cacheRoot = Path.GetFullPath(cacheRoot);
        _accountId = session.AccountId;
        _connected = session.State.Connection.Status == ConnectionStatus.Connected;
        _session.StateChanged += OnStateChanged;
    }

    internal async Task<CachedAvatar> GetAsync(string sourceUrl, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Entry entry;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ResetAccountIfChanged();
            var accountId = _accountId ?? throw new InvalidOperationException("No account is active.");
            if (_clearingAccounts.Contains(accountId)) throw new OperationCanceledException();
            if (!_entries.TryGetValue(sourceUrl, out entry!))
            {
                entry = new Entry(accountId, sourceUrl, _generation);
                _entries.Add(sourceUrl, entry);
                entry.LocalRead = Task.Run(() => ReadLocalAsync(entry));
            }
            entry.LastUsed = DateTimeOffset.UtcNow;
            TrimMemory(entry);
        }
        await entry.LocalRead.WaitAsync(cancellationToken).ConfigureAwait(false);
        Task<CachedAvatar> refresh;
        lock (_gate)
        {
            RequireCurrent(entry);
            refresh = StartRefresh(entry);
            if (entry.Avatar is { } cached) return cached;
        }
        var result = await refresh.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate) RequireCurrent(entry);
        return result;
    }

    internal async Task WaitForRefreshAsync(string sourceUrl)
    {
        Entry entry;
        lock (_gate) entry = _entries[sourceUrl];
        try { await (entry.Refresh ?? Task.CompletedTask).ConfigureAwait(false); }
        finally { await entry.Settled.ConfigureAwait(false); }
    }

    internal int EntryCount { get { lock (_gate) return _entries.Count; } }

    private Task<CachedAvatar> StartRefresh(Entry entry)
    {
        // One refresh per URL during this run; reconnect permits a retry after failure.
        if (entry.Refresh is null)
        {
            entry.Refresh = Task.Run(() => RefreshAsync(entry));
            entry.Settled = entry.Refresh.ContinueWith(task =>
            {
                _ = task.Exception;
                lock (_gate) TrimMemory(null);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        return entry.Refresh;
    }

    private async Task ReadLocalAsync(Entry entry)
    {
        try
        {
            foreach (var (extension, contentType) in Formats)
            {
                var path = GetPath(entry, extension);
                if (!File.Exists(path)) continue;
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                    4096, FileOptions.Asynchronous);
                if (stream.Length is <= 0 or > MaximumAvatarBytes) continue;
                var bytes = new byte[checked((int)stream.Length)];
                await stream.ReadExactlyAsync(bytes, entry.Cancellation.Token).ConfigureAwait(false);
                lock (_gate)
                {
                    if (IsCurrent(entry)) entry.Avatar = new CachedAvatar(new RealmMediaResult(bytes, contentType), new Uri(path));
                }
                return;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (OperationCanceledException) { }
    }

    private async Task<CachedAvatar> RefreshAsync(Entry entry)
    {
        await entry.LocalRead.ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(entry.Cancellation.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await _downloads.WaitAsync(token).ConfigureAwait(false);
        try
        {
            lock (_gate) RequireCurrent(entry);
            var result = await _session.GetRealmMediaAsync(
                new RealmMediaRequest(entry.SourceUrl, RealmMediaKind.Avatar, MaximumAvatarBytes), token).ConfigureAwait(false);
            var extension = NotificationAvatarFileStore.GetSafeImageExtension(result.ContentType);
            if (extension is null || result.Content.Length is <= 0 or > MaximumAvatarBytes)
                throw new InvalidOperationException("Unsupported avatar image.");

            lock (_gate)
            {
                RequireCurrent(entry);
                if (entry.Avatar is { } existing && existing.Media.Content.AsSpan().SequenceEqual(result.Content))
                    return existing;
            }
            var path = GetPath(entry, extension);
            Uri? fileUri = null;
            await _files.WaitAsync(token).ConfigureAwait(false);
            try
            {
                lock (_gate) RequireCurrent(entry);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await File.WriteAllBytesAsync(temporaryPath, result.Content, token).ConfigureAwait(false);
                    lock (_gate)
                    {
                        RequireCurrent(entry);
                        File.Move(temporaryPath, path, overwrite: true);
                        fileUri = new Uri(path);
                    }
                    // Only remove obsolete formats belonging to this exact avatar key.
                    foreach (var (oldExtension, _) in Formats)
                        if (oldExtension != extension) File.Delete(GetPath(entry, oldExtension));
                }
                finally
                {
                    if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            finally { _files.Release(); }

            CachedAvatar avatar;
            lock (_gate)
            {
                RequireCurrent(entry);
                avatar = new CachedAvatar(result, fileUri);
                entry.Avatar = avatar;
            }
            AvatarChanged?.Invoke(this, new AvatarChangedEventArgs(entry.AccountId, entry.SourceUrl));
            return avatar;
        }
        finally { _downloads.Release(); }
    }

    public async Task ClearAccountAsync(AccountId accountId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _clearingAccounts.Add(accountId);
            if (_accountId == accountId) _generation++;
            foreach (var entry in _entries.Values.Where(entry => entry.AccountId == accountId).ToArray())
            {
                entry.Cancellation.Cancel();
                _entries.Remove(entry.SourceUrl);
            }
        }
        await _files.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var directory = NotificationAvatarFileStore.GetAccountCacheDirectory(_cacheRoot, accountId);
            await Task.Run(() =>
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            }).ConfigureAwait(false);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        finally
        {
            lock (_gate) _clearingAccounts.Remove(accountId);
            _files.Release();
        }
    }

    private void OnStateChanged(object? sender, ClientStateChangedEventArgs args)
    {
        lock (_gate)
        {
            if (_disposed) return;
            ResetAccountIfChanged();
            var connected = args.State.Connection.Status == ConnectionStatus.Connected;
            if (connected && !_connected)
            {
                foreach (var entry in _entries.Values.ToArray())
                {
                    if (entry.Refresh is { IsCompleted: true, IsCompletedSuccessfully: false }) entry.Refresh = null;
                    StartRefresh(entry);
                }
            }
            _connected = connected;
        }
    }

    private void ResetAccountIfChanged()
    {
        if (_accountId == _session.AccountId) return;
        foreach (var entry in _entries.Values) entry.Cancellation.Cancel();
        _entries.Clear();
        _generation++;
        _accountId = _session.AccountId;
        _connected = false;
    }

    private bool IsCurrent(Entry entry) => !_disposed && !entry.Cancellation.IsCancellationRequested &&
        _session.AccountId == entry.AccountId && entry.Generation == _generation;

    private void RequireCurrent(Entry entry)
    {
        if (!IsCurrent(entry)) throw new OperationCanceledException();
    }

    private void TrimMemory(Entry? current)
    {
        while (_entries.Count > 64)
        {
            var oldest = _entries.Values.Where(entry => entry != current && entry.LocalRead.IsCompleted &&
                    entry.Refresh is { IsCompleted: true }).MinBy(entry => entry.LastUsed);
            if (oldest is null) return;
            _entries.Remove(oldest.SourceUrl);
        }
    }

    private string GetPath(Entry entry, string extension) => Path.Combine(
        NotificationAvatarFileStore.GetAccountCacheDirectory(_cacheRoot, entry.AccountId),
        NotificationAvatarFileStore.CreateFileStem(entry.AccountId, entry.SourceUrl) + extension);

    private static readonly (string Extension, string ContentType)[] Formats =
        [(".png", "image/png"), (".jpg", "image/jpeg"), (".gif", "image/gif"), (".webp", "image/webp")];

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _session.StateChanged -= OnStateChanged;
            foreach (var entry in _entries.Values) entry.Cancellation.Cancel();
            _entries.Clear();
        }
        // Pending I/O still releases the semaphores after cancellation.
    }

    internal sealed record CachedAvatar(RealmMediaResult Media, Uri? FileUri);

    private sealed class Entry(AccountId accountId, string sourceUrl, long generation)
    {
        public long Generation { get; } = generation;
        public AccountId AccountId { get; } = accountId;
        public string SourceUrl { get; } = sourceUrl;
        public CancellationTokenSource Cancellation { get; } = new();
        public Task LocalRead { get; set; } = Task.CompletedTask;
        public Task<CachedAvatar>? Refresh { get; set; }
        public Task Settled { get; set; } = Task.CompletedTask;
        public CachedAvatar? Avatar { get; set; }
        public DateTimeOffset LastUsed { get; set; } = DateTimeOffset.UtcNow;
    }
}
