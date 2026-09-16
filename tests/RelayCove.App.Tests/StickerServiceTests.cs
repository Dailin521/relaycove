using System.Net;
using System.Text;
using System.Text.Json;
using RelayCove.App.Services;

namespace RelayCove.App.Tests;

public sealed class StickerServiceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "RelayCove-sticker-tests", Guid.NewGuid().ToString("N"));
    private static readonly byte[] Gif = Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7");

    [Fact]
    public async Task GetCatalogAsync_WhenFreshIndexIsCached_ExposesSearchFieldsAndAvoidsAnotherRequest()
    {
        var handler = new FakeHandler(_ => IndexResponse());
        using var service = new ChineseBqbStickerCatalogService(root, handler);
        var first = await service.GetCatalogAsync();
        var entry = Assert.Single(first.Entries);
        Assert.Equal("开心", entry.Label);
        Assert.Equal("cats", entry.Category);
        Assert.Equal("猫猫", entry.CategoryTitle);
        Assert.Equal("https://zhaoolee.com/ChineseBQB/media/one.gif", entry.SourceUrl);
        Assert.False(first.IsOffline);
        await service.GetCatalogAsync();
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task GetCatalogAsync_WhenStaleAndOffline_RetainsCachedSearchEntriesAcrossRestart()
    {
        var clock = DateTimeOffset.UtcNow.AddDays(-2);
        using (var service = new ChineseBqbStickerCatalogService(root, new FakeHandler(_ => IndexResponse()), now: () => clock))
            Assert.Single((await service.GetCatalogAsync()).Entries);
        using var offline = new ChineseBqbStickerCatalogService(root, new FakeHandler(_ => throw new HttpRequestException()), now: () => clock.AddDays(2));
        var result = await offline.GetCatalogAsync();
        Assert.True(result.IsOffline);
        Assert.Single(result.Entries);
    }

    [Fact]
    public async Task GetCatalogAsync_WhenNoCacheAndOffline_ReturnsEmptyOfflineSnapshot()
    {
        using var service = new ChineseBqbStickerCatalogService(root, new FakeHandler(_ => throw new HttpRequestException()));
        var result = await service.GetCatalogAsync();
        Assert.True(result.IsOffline);
        Assert.Empty(result.Entries);
    }

    [Theory]
    [InlineData("https://evil.example/media/one.gif")]
    [InlineData("http://zhaoolee.com/ChineseBQB/media/one.gif")]
    [InlineData("https://zhaoolee.com/ChineseBQB/media/../catalog/search.json")]
    [InlineData("https://zhaoolee.com/ChineseBQB/media/%2e%2e/secret.gif")]
    [InlineData("https://user:password@zhaoolee.com/ChineseBQB/media/one.gif")]
    [InlineData("https://zhaoolee.com/ChineseBQB/media/one.gif?secret=abc")]
    public async Task GetMediaAsync_WhenUrlIsUntrusted_RejectsBeforeHttp(string url)
    {
        var handler = new FakeHandler(_ => ImageResponse(Gif));
        using var service = new ChineseBqbStickerCatalogService(root, handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetMediaAsync(Entry() with { SourceUrl = url }));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task GetMediaAsync_WhenRedirectChangesHost_RejectsWithoutFollowing()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = new Uri("https://evil.example/media/one.gif") }
        });
        using var service = new ChineseBqbStickerCatalogService(root, handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetMediaAsync(Entry()));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task GetMediaAsync_WhenDownloadAndCacheRead_ReturnsOriginalGifBytes()
    {
        var handler = new FakeHandler(request =>
        {
            Assert.Null(request.Headers.Authorization);
            Assert.Null(request.Headers.Referrer);
            return ImageResponse(Gif);
        });
        using var service = new ChineseBqbStickerCatalogService(root, handler);
        var downloaded = await service.GetMediaAsync(Entry());
        var cached = await service.GetMediaAsync(Entry());
        Assert.Equal(Gif, downloaded.Content);
        Assert.Equal(Gif, cached.Content);
        Assert.Equal("image/gif", cached.ContentType);
        Assert.EndsWith(".gif", cached.FileName);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task GetMediaAsync_WhenCacheBudgetExceeded_EvictsLeastRecentlyReadWithoutInvalidatingReturnedBytes()
    {
        var clock = DateTimeOffset.UtcNow;
        var handler = new FakeHandler(_ => ImageResponse(Gif));
        using var service = new ChineseBqbStickerCatalogService(root, handler, cacheBudget: Gif.Length * 2, now: () => clock);
        var first = await service.GetMediaAsync(Entry("one"));
        clock = clock.AddMinutes(1);
        await service.GetMediaAsync(Entry("two"));
        clock = clock.AddMinutes(1);
        await service.GetMediaAsync(Entry("one"));
        clock = clock.AddMinutes(1);
        await service.GetMediaAsync(Entry("three"));
        await service.GetMediaAsync(Entry("one"));
        Assert.Equal(3, handler.Calls);
        await service.GetMediaAsync(Entry("two"));
        Assert.Equal(4, handler.Calls);
        Assert.Equal(Gif, first.Content);
        Assert.True(new DirectoryInfo(Path.Combine(root, "chinesebqb", "media")).GetFiles().Sum(file => file.Length) <= Gif.Length * 2);
    }

    [Fact]
    public async Task GetMediaAsync_WhenHttpLengthExceedsLimit_Rejects()
    {
        using var service = new ChineseBqbStickerCatalogService(root, new FakeHandler(_ =>
        {
            var response = ImageResponse(Gif);
            response.Content.Headers.ContentLength = 25 * 1024 * 1024 + 1;
            return response;
        }));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetMediaAsync(Entry()));
    }

    [Fact]
    public async Task AddAsync_WhenBytesAreDuplicate_DeduplicatesWithinAccountAndIsolatesOtherAccounts()
    {
        var store = new LocalStickerLibraryStore(root);
        var first = await store.AddAsync("../../first", new MemoryStream(Gif), "最初");
        var duplicate = await store.AddAsync("../../first", new MemoryStream(Gif), "重复");
        Assert.Equal(first, duplicate);
        Assert.Single(await store.ListAsync("../../first"));
        Assert.Empty(await store.ListAsync("second"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReadAsync("second", first.Hash));
        await store.AddAsync("second", new MemoryStream(Gif), "另一个账号");
        Assert.Equal(2, Directory.GetDirectories(Path.Combine(root, "favorites")).Length);
        Assert.All(Directory.GetDirectories(Path.Combine(root, "favorites")), path => Assert.Matches("^[0-9a-f]{64}$", Path.GetFileName(path)));
    }

    [Fact]
    public async Task AddAsync_WhenImportedSourceDisappears_FavoriteKeepsOriginalBytesAcrossRestart()
    {
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "wrong-extension.txt");
        await File.WriteAllBytesAsync(source, Gif);
        var store = new LocalStickerLibraryStore(root);
        StickerFavorite favorite;
        await using (var stream = File.OpenRead(source)) favorite = await store.AddAsync("account", stream, "动图");
        File.Delete(source);
        var restarted = new LocalStickerLibraryStore(root);
        Assert.Equal(Gif, (await restarted.ReadAsync("account", favorite.Hash)).Content);
        Assert.EndsWith(".gif", favorite.FileName);
    }

    [Fact]
    public async Task RenameAndRemoveAsync_WhenFavoriteExists_PersistChangesAndDeleteOnlyOwnedFile()
    {
        var store = new LocalStickerLibraryStore(root);
        var favorite = await store.AddAsync("account", new MemoryStream(Gif), "旧名称");
        await store.RenameAsync("account", favorite.Hash, "新名称");
        Assert.Equal("新名称", Assert.Single(await new LocalStickerLibraryStore(root).ListAsync("account")).Label);
        await store.RemoveAsync("account", favorite.Hash);
        Assert.Empty(await store.ListAsync("account"));
        Assert.Empty(Directory.GetFiles(root, "*.gif", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task AddAsync_WhenContentIsNotSupportedImage_RejectsRegardlessOfLabel()
    {
        var store = new LocalStickerLibraryStore(root);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.AddAsync("account", new MemoryStream("<svg></svg>"u8.ToArray()), "valid.png"));
        Assert.Empty(await store.ListAsync("account"));
    }

    [Fact]
    public async Task AddAsync_WhenStreamExceedsSizeLimit_RejectsBeforePersisting()
    {
        var store = new LocalStickerLibraryStore(root);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.AddAsync("account", new MemoryStream(new byte[25 * 1024 * 1024 + 1]), "oversize"));
        Assert.Empty(await store.ListAsync("account"));
    }

    [Fact]
    public async Task ListAsync_WhenMetadataContainsTraversal_RejectsWithoutReadingExternalFile()
    {
        var store = new LocalStickerLibraryStore(root);
        var favorite = await store.AddAsync("account", new MemoryStream(Gif), "图片");
        var metadata = Assert.Single(Directory.GetFiles(root, "library.json", SearchOption.AllDirectories));
        await File.WriteAllTextAsync(metadata, JsonSerializer.Serialize(new[] { favorite with { FileName = "../../external.gif" } }));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ListAsync("account"));
    }

    private static StickerCatalogEntry Entry(string id = "one") => new(id, "开心", "media/" + id + ".gif",
        "thumbs/" + id + ".webp", 1, 1, true, Gif.Length, "cats", "猫猫");

    [Fact]
    public async Task GetMediaAsync_WhenCachedFileIsCorrupt_DownloadsFreshBytesOnRetry()
    {
        var handler = new FakeHandler(_ => ImageResponse(Gif));
        using var service = new ChineseBqbStickerCatalogService(root, handler);
        await service.GetMediaAsync(Entry());
        var path = Assert.Single(Directory.GetFiles(Path.Combine(root, "chinesebqb", "media"), "*.bin"));
        await File.WriteAllBytesAsync(path, "broken"u8.ToArray());
        Assert.Equal(Gif, (await service.GetMediaAsync(Entry())).Content);
        Assert.Equal(2, handler.Calls);
    }

    private static HttpResponseMessage IndexResponse() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("""[{"id":"one","label":"开心","src":"media/one.gif","thumb":"thumbs/one.webp","width":1,"height":1,"animated":true,"bytes":42,"category":"cats","categoryTitle":"猫猫"}]""", Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage ImageResponse(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    public void Dispose()
    {
        // The test owns this unique, absolute temporary directory.
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(response(request));
        }
    }
}
