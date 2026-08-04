using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RelayCove.Client.Updates;
using RelayCove.Shared.Updates;

namespace RelayCove.Client.Tests.Updates;

public sealed class ClientUpdateCoreTests
{
    [Fact]
    public async Task FetchAsync_WhenManifestIsValid_ReturnsValidatedManifest()
    {
        var manifest = CreateManifest("1.0.1");
        using var httpClient = new HttpClient(new DelegateHttpHandler((request, _) =>
        {
            Assert.Equal("https://relay.example/base/api/updates/manifest", request.RequestUri!.AbsoluteUri);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(JsonSerializer.Serialize(manifest), Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }));
        var transport = new ClientUpdateManifestHttpTransport(
            httpClient,
            NullLogger<ClientUpdateManifestHttpTransport>.Instance);

        var outcome = await transport.FetchAsync(new Uri("https://relay.example/base/"));

        Assert.Equal(ClientUpdateFetchStatus.Success, outcome.Status);
        Assert.Equal(manifest, outcome.Manifest);
    }

    [Fact]
    public async Task FetchAsync_WhenRedirected_ReturnsRemoteFailure()
    {
        using var httpClient = new HttpClient(new DelegateHttpHandler((request, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://other.example/manifest"),
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }));
        var transport = new ClientUpdateManifestHttpTransport(
            httpClient,
            NullLogger<ClientUpdateManifestHttpTransport>.Instance);

        var outcome = await transport.FetchAsync(new Uri("https://relay.example"));

        Assert.Equal(ClientUpdateFetchStatus.RemoteFailure, outcome.Status);
    }

    [Fact]
    public async Task DownloadAsync_WhenHashMatches_PublishesFixedArchiveAndRemovesPart()
    {
        var payload = Encoding.UTF8.GetBytes("portable update");
        var manifest = CreateManifest("1.0.1", payload);
        var temporaryRoot = CreateTemporaryRoot();
        try
        {
            using var httpClient = new HttpClient(new DelegateHttpHandler((request, _) =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new ByteArrayContent(payload),
                };
                response.Content.Headers.ContentLength = payload.Length;
                return Task.FromResult(response);
            }));
            using var downloader = new ClientUpdatePackageDownloader(
                temporaryRoot,
                httpClient,
                NullLogger<ClientUpdatePackageDownloader>.Instance);

            var outcome = await downloader.DownloadAsync(manifest);

            Assert.Equal(ClientUpdateDownloadStatus.Success, outcome.Status);
            Assert.Equal(Path.Combine(temporaryRoot, "RelayCove-1.0.1.zip"), outcome.ArchivePath);
            Assert.Equal(payload, await File.ReadAllBytesAsync(outcome.ArchivePath!));
            Assert.False(File.Exists(Path.Combine(temporaryRoot, "RelayCove-1.0.1.zip.part")));
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadAsync_WhenHashDoesNotMatch_RejectsAndCleansStagingFile()
    {
        var payload = Encoding.UTF8.GetBytes("tampered update");
        var manifest = CreateManifest("1.0.1", Encoding.UTF8.GetBytes("expected update"));
        var temporaryRoot = CreateTemporaryRoot();
        try
        {
            using var httpClient = new HttpClient(new DelegateHttpHandler((request, _) =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new ByteArrayContent(payload),
                };
                response.Content.Headers.ContentLength = payload.Length;
                return Task.FromResult(response);
            }));
            using var downloader = new ClientUpdatePackageDownloader(
                temporaryRoot,
                httpClient,
                NullLogger<ClientUpdatePackageDownloader>.Instance);

            var outcome = await downloader.DownloadAsync(manifest);

            Assert.Equal(ClientUpdateDownloadStatus.ProtocolError, outcome.Status);
            Assert.Null(outcome.ArchivePath);
            Assert.False(File.Exists(Path.Combine(temporaryRoot, "RelayCove-1.0.1.zip")));
            Assert.False(File.Exists(Path.Combine(temporaryRoot, "RelayCove-1.0.1.zip.part")));
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadAsync_WhenConcurrentSameArtifact_UsesOneHttpRequest()
    {
        var payload = Encoding.UTF8.GetBytes("single flight update");
        var manifest = CreateManifest("1.0.1", payload);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var temporaryRoot = CreateTemporaryRoot();
        try
        {
            using var httpClient = new HttpClient(new DelegateHttpHandler(async (request, _) =>
            {
                Interlocked.Increment(ref calls);
                started.TrySetResult();
                await release.Task;
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new ByteArrayContent(payload),
                };
                response.Content.Headers.ContentLength = payload.Length;
                return response;
            }));
            using var downloader = new ClientUpdatePackageDownloader(
                temporaryRoot,
                httpClient,
                NullLogger<ClientUpdatePackageDownloader>.Instance);

            var first = downloader.DownloadAsync(manifest);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var second = downloader.DownloadAsync(manifest);
            release.TrySetResult();
            var outcomes = await Task.WhenAll(first, second);

            Assert.Equal(1, Volatile.Read(ref calls));
            Assert.All(outcomes, outcome => Assert.Equal(ClientUpdateDownloadStatus.Success, outcome.Status));
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [Fact]
    public async Task CheckAsync_WhenCurrentVersionIsBelowMinimum_ExposesMandatoryState()
    {
        var manifest = CreateManifest("2.0.0", minimumSupportedVersion: "1.1.0");
        var transport = new FakeManifestTransport((_, _) => Task.FromResult(
            ClientUpdateManifestFetchOutcome.Success(manifest)));
        var downloader = new FakeDownloader();
        await using var coordinator = new ClientUpdateCoordinator(
            transport,
            new FixedVersionProvider("1.0.0"),
            downloader,
            NullLogger<ClientUpdateCoordinator>.Instance);

        var state = await coordinator.CheckAsync(new Uri("https://relay.example"));

        Assert.Equal(ClientUpdatePhase.MandatoryAvailable, state.Phase);
        Assert.Equal(UpdateDecisionKind.Unsupported, state.Decision);
        Assert.True(state.IsMandatory);
        Assert.Equal(manifest, state.Manifest);
    }

    [Fact]
    public async Task CheckAsync_WhenSuperseded_DoesNotAllowLateOldResultToReviveState()
    {
        var firstRelease = new TaskCompletionSource<ClientUpdateManifestFetchOutcome>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondManifest = CreateManifest("1.0.0");
        var transport = new FakeManifestTransport((baseUri, _) =>
        {
            if (baseUri.Host == "first.example")
            {
                firstStarted.TrySetResult();
                return firstRelease.Task;
            }

            return Task.FromResult(ClientUpdateManifestFetchOutcome.Success(secondManifest));
        });
        await using var coordinator = new ClientUpdateCoordinator(
            transport,
            new FixedVersionProvider("1.0.0"),
            new FakeDownloader(),
            NullLogger<ClientUpdateCoordinator>.Instance);

        var first = coordinator.CheckAsync(new Uri("https://first.example"));
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = await coordinator.CheckAsync(new Uri("https://second.example"));
        firstRelease.TrySetResult(ClientUpdateManifestFetchOutcome.Success(CreateManifest("2.0.0")));
        await first;

        Assert.Equal(ClientUpdatePhase.NoUpdate, second.Phase);
        Assert.Equal(ClientUpdatePhase.NoUpdate, coordinator.State.Phase);
        Assert.Equal("1.0.0", coordinator.State.Manifest!.Version);
    }

    [Fact]
    public void GetCurrentVersion_WhenInformationalVersionHasBuildMetadata_StripsMetadata()
    {
        var provider = new ClientAssemblyCurrentVersionProvider();

        var version = provider.GetCurrentVersion();

        Assert.DoesNotContain('+', version);
    }

    private static UpdateManifestDto CreateManifest(
        string version,
        byte[]? payload = null,
        string minimumSupportedVersion = "1.0.0")
    {
        payload ??= Encoding.UTF8.GetBytes("default artifact");
        return new UpdateManifestDto(
            UpdateConstants.SchemaVersion,
            UpdateConstants.Channel,
            version,
            minimumSupportedVersion,
            Mandatory: false,
            new UpdateArtifactDto(
                UpdateConstants.ArtifactTypePortableZip,
                "https://updates.example/opaque-artifact",
                payload.LongLength,
                Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant()),
            "Release notes");
    }

    private static string CreateTemporaryRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "RelayCove.Client.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FixedVersionProvider(string version) : IClientCurrentVersionProvider
    {
        public string GetCurrentVersion() => version;
    }

    private sealed class FakeManifestTransport(
        Func<Uri, CancellationToken, Task<ClientUpdateManifestFetchOutcome>> fetchAsync) :
        IClientUpdateManifestTransport
    {
        public Task<ClientUpdateManifestFetchOutcome> FetchAsync(
            Uri serverBaseUri,
            CancellationToken cancellationToken = default) => fetchAsync(serverBaseUri, cancellationToken);
    }

    private sealed class FakeDownloader : IClientUpdateDownloader
    {
        public void Cancel()
        {
        }

        public Task<ClientUpdateDownloadOutcome> DownloadAsync(
            UpdateManifestDto manifest,
            Action<ClientUpdateDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ClientUpdateDownloadOutcome.Success(Path.GetTempFileName()));
    }

    private sealed class DelegateHttpHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => sendAsync(request, cancellationToken);
    }
}
