using System.Net;
using System.Text.Json;

namespace RelayCove.App.Services;

public sealed class ChineseBqbStickerCatalogService : IStickerCatalogService, IDisposable
{
    private static readonly Uri BaseUri = new("https://zhaoolee.com/ChineseBQB/");
    private static readonly Uri IndexUri = new(BaseUri, "catalog/search.json");
    private const int MaximumIndexBytes = 8 * 1024 * 1024;
    private readonly HttpClient client;
    private readonly string cacheDirectory;
    private readonly long cacheBudget;
    private readonly Func<DateTimeOffset> now;
    private readonly SemaphoreSlim catalogGate = new(1, 1);
    private readonly SemaphoreSlim cacheGate = new(1, 1);
    private readonly SemaphoreSlim downloads = new(4, 4);
    private StickerCatalogSnapshot? snapshot;
    private DateTimeOffset refreshedAt;

    public ChineseBqbStickerCatalogService(string cacheRoot) : this(cacheRoot, new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseDefaultCredentials = false,
        Credentials = null
    }) { }

    internal ChineseBqbStickerCatalogService(string cacheRoot, HttpMessageHandler handler,
        long cacheBudget = 200L * 1024 * 1024, Func<DateTimeOffset>? now = null)
    {
        if (cacheBudget < 0) throw new ArgumentOutOfRangeException(nameof(cacheBudget));
        cacheDirectory = Path.Combine(Path.GetFullPath(cacheRoot), "chinesebqb");
        this.cacheBudget = cacheBudget;
        this.now = now ?? (() => DateTimeOffset.UtcNow);
        client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task<StickerCatalogSnapshot> GetCatalogAsync(CancellationToken cancellationToken = default)
    {
        await catalogGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var indexPath = Path.Combine(cacheDirectory, "search.json");
            if (snapshot is null && File.Exists(indexPath))
            {
                try
                {
                    await using var file = File.OpenRead(indexPath);
                    snapshot = new StickerCatalogSnapshot(ParseIndex(await StickerImageContent.ReadBoundedAsync(file,
                        MaximumIndexBytes, cancellationToken).ConfigureAwait(false)), false);
                    refreshedAt = File.GetLastWriteTimeUtc(indexPath);
                }
                catch (Exception exception) when (exception is IOException or JsonException or ArgumentException) { }
            }
            if (snapshot is not null && now() - refreshedAt < TimeSpan.FromHours(24)) return snapshot;
            try
            {
                var bytes = await DownloadAsync(IndexUri, MaximumIndexBytes, cancellationToken).ConfigureAwait(false);
                var entries = ParseIndex(bytes);
                await StickerImageContent.WriteAtomicAsync(indexPath, bytes, cancellationToken).ConfigureAwait(false);
                refreshedAt = now();
                File.SetLastWriteTimeUtc(indexPath, refreshedAt.UtcDateTime);
                snapshot = new StickerCatalogSnapshot(entries, false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException
                or ArgumentException or OperationCanceledException)
            {
                snapshot = new StickerCatalogSnapshot(snapshot?.Entries ?? [], true);
            }
            return snapshot;
        }
        finally { catalogGate.Release(); }
    }

    public async Task<StickerMedia> GetMediaAsync(StickerCatalogEntry entry, bool thumbnail = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var uri = ValidateUri(thumbnail ? entry.ThumbnailUrl : entry.SourceUrl, true);
        var path = Path.Combine(cacheDirectory, "media", StickerImageContent.HashText(uri.AbsoluteUri) + ".bin");
        await cacheGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(path))
            {
                try
                {
                    StickerMedia media;
                    await using (var file = File.OpenRead(path))
                        media = StickerImageContent.Validate(await StickerImageContent.ReadBoundedAsync(file,
                            StickerImageContent.MaximumBytes, cancellationToken).ConfigureAwait(false));
                    File.SetLastWriteTimeUtc(path, now().UtcDateTime);
                    return media;
                }
                catch (InvalidDataException)
                {
                    // A corrupt disposable cache entry must not make every later retry fail.
                    File.Delete(path);
                }
            }
        }
        finally { cacheGate.Release(); }

        await downloads.WaitAsync(cancellationToken).ConfigureAwait(false);
        StickerMedia downloaded;
        try
        {
            downloaded = StickerImageContent.Validate(await DownloadAsync(uri,
                StickerImageContent.MaximumBytes, cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
            throw new InvalidOperationException("图片下载失败，请稍后重试。");
        }
        finally { downloads.Release(); }

        if (downloaded.Content.LongLength <= cacheBudget)
        {
            await cacheGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await StickerImageContent.WriteAtomicAsync(path, downloaded.Content, cancellationToken).ConfigureAwait(false);
                File.SetLastWriteTimeUtc(path, now().UtcDateTime);
                var files = new DirectoryInfo(Path.GetDirectoryName(path)!).GetFiles("*.bin")
                    .OrderBy(file => file.LastWriteTimeUtc).ToArray();
                var total = files.Sum(file => file.Length);
                foreach (var file in files)
                {
                    if (total <= cacheBudget) break;
                    total -= file.Length;
                    file.Delete();
                }
            }
            finally { cacheGate.Release(); }
        }
        // Return owned bytes so later LRU eviction cannot invalidate a send or favorite operation.
        return downloaded;
    }

    private async Task<byte[]> DownloadAsync(Uri uri, int maximumBytes, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        for (var redirects = 0; redirects <= 3; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod
                or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                if (response.Headers.Location is null || redirects == 3) throw new InvalidDataException("表情地址无效。");
                uri = ValidateUri(new Uri(uri, response.Headers.Location).AbsoluteUri, uri != IndexUri);
                continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > maximumBytes) throw new InvalidDataException("图片或目录文件过大。");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            return await StickerImageContent.ReadBoundedAsync(stream, maximumBytes, timeout.Token).ConfigureAwait(false);
        }
        throw new InvalidDataException("表情地址无效。");
    }

    private static IReadOnlyList<StickerCatalogEntry> ParseIndex(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() > 20000)
            throw new InvalidDataException("表情目录格式无效。");
        var entries = new List<StickerCatalogEntry>();
        foreach (var row in document.RootElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) throw new InvalidDataException("表情目录格式无效。");
            string Text(string name) => row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty : string.Empty;
            int Number(string name) => row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt32(out var result) ? result : 0;
            var source = ValidateUri(Text("src"), true);
            var thumb = ValidateUri(Text("thumb"), true);
            entries.Add(new StickerCatalogEntry(Text("id"), Text("label"), source.AbsoluteUri, thumb.AbsoluteUri,
                Number("width"), Number("height"), row.TryGetProperty("animated", out var animated) && animated.ValueKind == JsonValueKind.True,
                Number("bytes"), Text("category"), Text("categoryTitle")));
        }
        return entries;
    }

    private static Uri ValidateUri(string value, bool image)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\\') || value.Contains('%') || value.Split('/').Contains("..")
            || !Uri.TryCreate(BaseUri, value, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(uri.Host, BaseUri.Host, StringComparison.OrdinalIgnoreCase) || !uri.IsDefaultPort
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || !(image ? uri.AbsolutePath.StartsWith("/ChineseBQB/media/", StringComparison.Ordinal)
                         || uri.AbsolutePath.StartsWith("/ChineseBQB/thumbs/", StringComparison.Ordinal)
                       : uri.AbsolutePath == IndexUri.AbsolutePath))
            throw new InvalidDataException("表情地址无效。");
        return uri;
    }

    public void Dispose()
    {
        client.Dispose();
        catalogGate.Dispose();
        cacheGate.Dispose();
        downloads.Dispose();
    }
}
