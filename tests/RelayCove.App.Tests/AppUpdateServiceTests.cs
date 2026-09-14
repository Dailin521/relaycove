using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RelayCove.App.Services;

namespace RelayCove.App.Tests;

public sealed class AppUpdateServiceTests
{
    private const string AssetRoot = "https://github.com/Dailin521/relaycove/releases/download/v1.0.5/";
    private const string InstallerName = "RichChat-1.0.5-win-x64-Setup.exe";
    private static readonly byte[] Installer = Encoding.UTF8.GetBytes("inert installer fixture");

    [Theory]
    [InlineData(11, true)]
    [InlineData(12, false)]
    [InlineData(13, false)]
    public async Task CheckForUpdateAsync_WhenLegacyReleaseHasHigherDisplayVersion_UsesManifestBuildNumber(int current, bool available)
    {
        var fixture = new Fixture();
        fixture.Releases.Insert(0, Release("v2.4.0", [], published: "2026-09-15T00:00:00Z"));
        fixture.Releases.Add(Release("v9.0.0", [], prerelease: true));
        using var service = fixture.CreateService();
        var result = await service.CheckForUpdateAsync(current);
        Assert.Equal(available, result is not null);
        if (result is not null)
        {
            Assert.Equal("1.0.5", result.Version);
            Assert.Equal(12, result.BuildNumber);
            Assert.Equal("<script>plain release notes</script>", result.ReleaseNotes);
        }
        Assert.All(fixture.Requests, request => Assert.Null(request.Headers.Authorization));
    }

    [Theory]
    [InlineData("schemaVersion", "2")]
    [InlineData("productId", "\"wrong\"")]
    [InlineData("platform", "\"linux\"")]
    [InlineData("version", "\"1.0.6\"")]
    [InlineData("buildNumber", "0")]
    [InlineData("installerName", "\"../malicious.exe\"")]
    [InlineData("size", "0")]
    [InlineData("size", "9999999999")]
    [InlineData("sha256", "\"bad\"")]
    public async Task CheckForUpdateAsync_WhenManifestFieldIsInvalid_DoesNotOfferUpdate(string key, string jsonValue)
    {
        var fixture = new Fixture();
        fixture.Manifest[key] = JsonSerializer.Deserialize<JsonElement>(jsonValue);
        using var service = fixture.CreateService();
        Assert.Null(await service.CheckForUpdateAsync(11));
    }

    [Theory]
    [InlineData("url")]
    [InlineData("size")]
    [InlineData("digest")]
    public async Task CheckForUpdateAsync_WhenAssetDoesNotMatchManifest_DoesNotOfferUpdate(string mismatch)
    {
        var fixture = new Fixture();
        fixture.InstallerAsset[mismatch == "url" ? "browser_download_url" : mismatch] = mismatch switch
        {
            "url" => "https://github.com/attacker/repo/releases/download/v1.0.5/" + InstallerName,
            "size" => 999L,
            _ => "sha256:" + new string('0', 64)
        };
        using var service = fixture.CreateService();
        Assert.Null(await service.CheckForUpdateAsync(11));
    }

    [Theory]
    [InlineData("http://github.com/unsafe")]
    [InlineData("https://evil.example/unsafe")]
    [InlineData("https://github.com.evil.example/unsafe")]
    [InlineData("https://user:secret@github.com/unsafe")]
    [InlineData("https://github.com:8443/unsafe")]
    public async Task CheckForUpdateAsync_WhenRedirectIsUnsafe_FailsWithoutSendingRequest(string destination)
    {
        var requests = 0;
        using var service = new GitHubAppUpdateService(new Handler(request =>
        {
            requests++;
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri(destination);
            return response;
        }));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CheckForUpdateAsync(11));
        Assert.Equal(1, requests);
        Assert.DoesNotContain(destination, error.ToString());
        Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task CheckForUpdateAsync_WhenApiFails_RedactsTransportDetails()
    {
        using var service = new GitHubAppUpdateService(new Handler(_ => throw new HttpRequestException("secret signed-url")));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CheckForUpdateAsync(11));
        Assert.DoesNotContain("secret", error.ToString());
    }

    [Fact]
    public async Task CheckForUpdateAsync_WhenResponseExceedsLimit_FailsClosed()
    {
        using var service = new GitHubAppUpdateService(new Handler(_ => Response(new byte[1024 * 1024 + 1])));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CheckForUpdateAsync(11));
    }

    [Fact]
    public async Task DownloadAsync_WhenRedirectAndHashAreValid_PublishesOnlyVerifiedInstaller()
    {
        var fixture = new Fixture();
        fixture.DownloadResponse = _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("https://release-assets.githubusercontent.com/fixture?signed=value");
            return response;
        };
        using var service = fixture.CreateService();
        var update = Assert.IsType<AppUpdateInfo>(await service.CheckForUpdateAsync(11));
        var directory = NewDirectory();
        var existing = Path.Combine(directory, InstallerName);
        await File.WriteAllTextAsync(existing, "existing unrelated installer");
        var progress = new ProgressSink();
        var path = await service.DownloadAsync(update, directory, progress);
        Assert.Equal(Installer, await File.ReadAllBytesAsync(path));
        Assert.Equal("existing unrelated installer", await File.ReadAllTextAsync(existing));
        Assert.Empty(Directory.GetFiles(directory, "*.partial", SearchOption.AllDirectories));
        Assert.Equal(1d, progress.Value);
        Assert.Contains(fixture.Requests, request => request.RequestUri!.Host == "release-assets.githubusercontent.com");
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("truncated")]
    [InlineData("oversized")]
    public async Task DownloadAsync_WhenBodyIsInvalid_RemovesPartialAndPublishesNothing(string mode)
    {
        var fixture = new Fixture();
        var body = mode switch
        {
            "hash" => new byte[Installer.Length],
            "truncated" => Installer[..^1],
            _ => Installer.Concat(new byte[] { 1 }).ToArray()
        };
        fixture.DownloadResponse = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new NonSeekStream(body))
        };
        using var service = fixture.CreateService();
        var update = Assert.IsType<AppUpdateInfo>(await service.CheckForUpdateAsync(11));
        var directory = NewDirectory();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DownloadAsync(update, directory));
        Assert.Empty(Directory.GetFiles(directory, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DownloadAsync_WhenStreamingIsCanceledOrTimesOut_RemovesPartial(bool userCancellation)
    {
        var fixture = new Fixture();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.DownloadResponse = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new WaitingStream(started))
        };
        using var service = fixture.CreateService(userCancellation ? TimeSpan.FromSeconds(10) : TimeSpan.FromMilliseconds(200));
        var update = Assert.IsType<AppUpdateInfo>(await service.CheckForUpdateAsync(11));
        var directory = NewDirectory();
        using var cancellation = new CancellationTokenSource();
        var download = service.DownloadAsync(update, directory, cancellationToken: cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (userCancellation)
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
        }
        else await Assert.ThrowsAsync<InvalidOperationException>(() => download);
        Assert.Empty(Directory.GetFiles(directory, "*", SearchOption.AllDirectories));
    }

    private static string NewDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RelayCove-update-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static Dictionary<string, object?> Release(string tag, object[] assets, bool prerelease = false,
        string published = "2026-09-14T00:00:00Z") => new()
    {
        ["tag_name"] = tag, ["draft"] = false, ["prerelease"] = prerelease,
        ["published_at"] = published, ["body"] = "<script>plain release notes</script>", ["assets"] = assets
    };

    private static HttpResponseMessage Response(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    private sealed class Fixture
    {
        internal Dictionary<string, object?> Manifest { get; } = new()
        {
            ["schemaVersion"] = 1, ["productId"] = "com.relaycove.client", ["platform"] = "win-x64",
            ["version"] = "1.0.5", ["buildNumber"] = 12, ["installerName"] = InstallerName,
            ["size"] = Installer.Length, ["sha256"] = Convert.ToHexString(SHA256.HashData(Installer))
        };
        internal Dictionary<string, object?> InstallerAsset { get; } = new()
        {
            ["name"] = InstallerName, ["size"] = Installer.Length,
            ["browser_download_url"] = AssetRoot + InstallerName,
            ["digest"] = "sha256:" + Convert.ToHexString(SHA256.HashData(Installer))
        };
        internal List<Dictionary<string, object?>> Releases { get; } = [];
        internal List<HttpRequestMessage> Requests { get; } = [];
        internal Func<HttpRequestMessage, HttpResponseMessage>? DownloadResponse { get; set; }

        internal Fixture() => Releases.Add(Release("v1.0.5", []));

        internal GitHubAppUpdateService CreateService(TimeSpan? downloadTimeout = null)
        {
            var manifest = JsonSerializer.SerializeToUtf8Bytes(Manifest);
            Releases.Single(release => (string)release["tag_name"]! == "v1.0.5")["assets"] = new object[]
            {
                new { name = "update-win-x64.json", size = manifest.Length,
                    browser_download_url = AssetRoot + "update-win-x64.json" }, InstallerAsset
            };
            return new GitHubAppUpdateService(new Handler(request =>
            {
                Requests.Add(request);
                var uri = request.RequestUri!;
                if (uri.Host == "api.github.com") return Response(JsonSerializer.SerializeToUtf8Bytes(Releases));
                if (uri.AbsolutePath.EndsWith("update-win-x64.json")) return Response(manifest);
                if (uri.Host == "release-assets.githubusercontent.com") return Response(Installer);
                return DownloadResponse?.Invoke(request) ?? Response(Installer);
            }), downloadTimeout: downloadTimeout);
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(send(request));
    }

    private sealed class ProgressSink : IProgress<double>
    {
        internal double Value { get; private set; }
        public void Report(double value) => Value = value;
    }

    private class NonSeekStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    private sealed class WaitingStream(TaskCompletionSource started) : NonSeekStream([])
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
