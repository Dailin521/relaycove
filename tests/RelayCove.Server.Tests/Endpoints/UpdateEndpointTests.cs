using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using RelayCove.Server.Tests.Infrastructure;
using RelayCove.Shared.Updates;

namespace RelayCove.Server.Tests.Endpoints;

public sealed class UpdateEndpointTests : IAsyncLifetime, IDisposable
{
    private const string ArtifactFileName = "RelayCove.Client-1.0.1-rc.1-win-x64.zip";
    private readonly string updatesDirectory = Path.Combine(
        Path.GetTempPath(),
        "RelayCove.Tests",
        "Updates",
        Guid.NewGuid().ToString("N"));
    private readonly RelayCoveWebApplicationFactory factory;

    public UpdateEndpointTests()
    {
        factory = new RelayCoveWebApplicationFactory(
            1_000,
            1_000,
            configurationOverrides: new Dictionary<string, string?>
            {
                ["Update:ManifestPath"] = Path.Combine(updatesDirectory, "manifest.json"),
            });
    }

    [Fact]
    public async Task GetManifest_WhenCurrentManifestIsValid_ReturnsItWithoutAuthentication()
    {
        var artifact = new byte[] { 1, 2, 3, 5, 8, 13 };
        var expected = await WriteCurrentReleaseAsync(artifact);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/updates/manifest");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected, await response.Content.ReadFromJsonAsync<UpdateManifestDto>());
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task DownloadArtifact_WhenRequestMatchesCurrentManifest_StreamsExactVerifiedZip()
    {
        var artifact = Enumerable.Range(0, 512 * 1024).Select(index => (byte)(index % 251)).ToArray();
        var expected = await WriteCurrentReleaseAsync(artifact);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            $"/api/updates/artifacts/{ArtifactFileName}",
            HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(artifact, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("application/zip", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal($"\"{expected.Artifact.Sha256}\"", response.Headers.ETag!.ToString());
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task DownloadArtifact_WhenFileIsNotTheCurrentManifestArtifact_DoesNotExposeIt()
    {
        await WriteCurrentReleaseAsync([1, 2, 3]);
        await File.WriteAllBytesAsync(Path.Combine(updatesDirectory, "not-current.zip"), [9, 9, 9]);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/updates/artifacts/not-current.zip");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task DownloadArtifact_WhenCurrentArtifactIsMissing_FailsClosed()
    {
        await WriteManifestAsync(CreateManifest(
            $"https://updates.example.test/api/updates/artifacts/{ArtifactFileName}",
            3,
            new string('a', 64)));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/api/updates/artifacts/{ArtifactFileName}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DownloadArtifact_WhenFileLengthOrHashDoesNotMatch_FailsClosedWithoutSensitiveLogs()
    {
        var expected = await WriteCurrentReleaseAsync([1, 2, 3]);
        await File.WriteAllBytesAsync(Path.Combine(updatesDirectory, ArtifactFileName), [1, 2, 3, 4]);
        var logOffset = factory.LogMessages.Count;
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/api/updates/artifacts/{ArtifactFileName}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var logs = string.Join('\n', factory.LogMessages.Skip(logOffset));
        Assert.DoesNotContain(updatesDirectory, logs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ArtifactFileName, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(expected.Artifact.Sha256, logs, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://updates.example.test/downloads/RelayCove.Client-1.0.1-rc.1-win-x64.zip")]
    [InlineData("https://updates.example.test/api/updates/artifacts/%2Fsecret.zip")]
    public async Task GetManifest_WhenArtifactUrlCannotMapToOneSafeHostedLeaf_FailsClosed(string artifactUrl)
    {
        await WriteManifestAsync(CreateManifest(artifactUrl, 3, new string('a', 64)));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/updates/manifest");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetManifest_WhenManifestExceedsBound_FailsClosed()
    {
        Directory.CreateDirectory(updatesDirectory);
        await File.WriteAllBytesAsync(
            Path.Combine(updatesDirectory, "manifest.json"),
            new byte[64 * 1024 + 1]);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/updates/manifest");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetManifest_WhenArtifactExceedsSharedLimit_FailsClosed()
    {
        await WriteManifestAsync(CreateManifest(
            $"https://updates.example.test/api/updates/artifacts/{ArtifactFileName}",
            UpdateConstants.MaximumArtifactBytes + 1,
            new string('a', 64)));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/updates/manifest");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    public async Task InitializeAsync()
    {
        await factory.InitializeDatabaseAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        factory.Dispose();
        if (Directory.Exists(updatesDirectory))
        {
            Directory.Delete(updatesDirectory, recursive: true);
        }
    }

    private async Task<UpdateManifestDto> WriteCurrentReleaseAsync(byte[] artifact)
    {
        Directory.CreateDirectory(updatesDirectory);
        await File.WriteAllBytesAsync(Path.Combine(updatesDirectory, ArtifactFileName), artifact);
        var manifest = CreateManifest(
            $"https://updates.example.test/api/updates/artifacts/{ArtifactFileName}",
            artifact.LongLength,
            Convert.ToHexString(SHA256.HashData(artifact)).ToLowerInvariant());
        await WriteManifestAsync(manifest);
        return manifest;
    }

    private async Task WriteManifestAsync(UpdateManifestDto manifest)
    {
        Directory.CreateDirectory(updatesDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(updatesDirectory, "manifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    private static UpdateManifestDto CreateManifest(string artifactUrl, long sizeBytes, string sha256) =>
        new(
            UpdateConstants.SchemaVersion,
            UpdateConstants.Channel,
            "1.0.1-rc.1",
            "1.0.0",
            false,
            new UpdateArtifactDto(
                UpdateConstants.ArtifactTypePortableZip,
                artifactUrl,
                sizeBytes,
                sha256),
            "Internal RC update.");
}
