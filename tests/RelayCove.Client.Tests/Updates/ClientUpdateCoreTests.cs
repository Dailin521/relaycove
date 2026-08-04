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
    public void Canonicalize_WhenRemoteServerUsesHttp_RejectsBaseUri()
    {
        Assert.Throws<ArgumentException>(() =>
            ClientUpdateServerUri.Canonicalize(new Uri("http://relay.example")));
    }

    [Theory]
    [InlineData("http://localhost:5080/base")]
    [InlineData("http://127.0.0.1:5080/base")]
    [InlineData("http://[::1]:5080/base")]
    public void Canonicalize_WhenLoopbackServerUsesHttp_AllowsBaseUri(string value)
    {
        var canonical = ClientUpdateServerUri.Canonicalize(new Uri(value));

        Assert.Equal(Uri.UriSchemeHttp, canonical.Scheme);
        Assert.EndsWith("/", canonical.AbsolutePath, StringComparison.Ordinal);
    }

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
    public async Task FetchAsync_WhenHandlerIgnoresCancellation_ReturnsTransientFailureAtCheckTimeout()
    {
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var httpClient = new HttpClient(new DelegateHttpHandler((_, _) =>
        {
            requestStarted.TrySetResult();
            return new TaskCompletionSource<HttpResponseMessage>(
                TaskCreationOptions.RunContinuationsAsynchronously).Task;
        }));
        var transport = new ClientUpdateManifestHttpTransport(
            httpClient,
            NullLogger<ClientUpdateManifestHttpTransport>.Instance,
            TimeSpan.FromMilliseconds(50));

        var outcome = await transport.FetchAsync(new Uri("https://relay.example"))
            .WaitAsync(TimeSpan.FromSeconds(5));

        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ClientUpdateFetchStatus.TransientFailure, outcome.Status);
    }

    [Fact]
    public async Task FetchAsync_WhenBodyIgnoresCancellationAfterHeaders_ReturnsTransientFailureAtCheckTimeout()
    {
        var bodyStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var httpClient = new HttpClient(new DelegateHttpHandler((request, _) =>
        {
            var content = new StalledContent(bodyStarted);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = content,
            });
        }));
        var transport = new ClientUpdateManifestHttpTransport(
            httpClient,
            NullLogger<ClientUpdateManifestHttpTransport>.Instance,
            TimeSpan.FromMilliseconds(50));

        var outcome = await transport.FetchAsync(new Uri("https://relay.example"))
            .WaitAsync(TimeSpan.FromSeconds(5));

        await bodyStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ClientUpdateFetchStatus.TransientFailure, outcome.Status);
    }

    [Fact]
    public async Task RunAsync_WhenAddressChangesDuringPreflight_UsesCapturedNormalizedAddressForLogin()
    {
        var preflightStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePreflight = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? preflightAddress = null;
        string? loginAddress = null;
        var typedAddress = " https://first.relay.example/base ";

        var attempt = ClientUpdateLoginPreflight.RunAsync(
            typedAddress,
            address =>
            {
                preflightAddress = address;
                preflightStarted.TrySetResult();
                return releasePreflight.Task;
            },
            address =>
            {
                loginAddress = address;
                return Task.CompletedTask;
            });

        await preflightStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        typedAddress = "https://second.relay.example";
        releasePreflight.TrySetResult(true);

        Assert.True(await attempt);
        Assert.Equal("https://first.relay.example/base", preflightAddress);
        Assert.Equal(preflightAddress, loginAddress);
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
    public async Task FetchAsync_WhenValidEscapedManifestExceedsThirtyTwoKiB_AcceptsPayload()
    {
        var releaseNotes = new string('\u0001', UpdateConstants.MaximumReleaseNotesLength);
        var manifest = CreateManifest("1.0.1") with { ReleaseNotes = releaseNotes };
        var payload = JsonSerializer.SerializeToUtf8Bytes(manifest);
        Assert.InRange(payload.Length, (32 * 1024) + 1, 64 * 1024);
        using var httpClient = new HttpClient(new DelegateHttpHandler((request, _) =>
        {
            var content = new ByteArrayContent(payload);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = content,
            });
        }));
        var transport = new ClientUpdateManifestHttpTransport(
            httpClient,
            NullLogger<ClientUpdateManifestHttpTransport>.Instance);

        var outcome = await transport.FetchAsync(new Uri("https://relay.example"));

        Assert.Equal(ClientUpdateFetchStatus.Success, outcome.Status);
        Assert.Equal(releaseNotes, outcome.Manifest!.ReleaseNotes);
    }

    [Fact]
    public async Task FetchAsync_WhenUnknownLengthPayloadExceedsSixtyFourKiB_RejectsPayload()
    {
        using var httpClient = new HttpClient(new DelegateHttpHandler((request, _) =>
        {
            var content = new UnknownLengthContent(new byte[(64 * 1024) + 1]);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = content,
            });
        }));
        var transport = new ClientUpdateManifestHttpTransport(
            httpClient,
            NullLogger<ClientUpdateManifestHttpTransport>.Instance);

        var outcome = await transport.FetchAsync(new Uri("https://relay.example"));

        Assert.Equal(ClientUpdateFetchStatus.ProtocolError, outcome.Status);
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
    public async Task DownloadAsync_WhenDifferentArtifactFlightIsNotCanceled_ReturnsInProgress()
    {
        var firstPayload = Encoding.UTF8.GetBytes("first active update");
        var secondPayload = Encoding.UTF8.GetBytes("second update");
        var firstManifest = CreateManifest("1.0.1", firstPayload);
        var secondManifest = CreateManifest("1.0.2", secondPayload);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var temporaryRoot = CreateTemporaryRoot();
        try
        {
            using var httpClient = new HttpClient(new DelegateHttpHandler(async (request, _) =>
            {
                started.TrySetResult();
                await release.Task;
                return PackageResponse(request, firstPayload);
            }));
            using var downloader = new ClientUpdatePackageDownloader(
                temporaryRoot,
                httpClient,
                NullLogger<ClientUpdatePackageDownloader>.Instance);

            var first = downloader.DownloadAsync(firstManifest);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var second = await downloader.DownloadAsync(secondManifest);
            release.TrySetResult();
            var firstOutcome = await first;

            Assert.Equal(ClientUpdateDownloadStatus.InProgress, second.Status);
            Assert.Equal(ClientUpdateDownloadStatus.Success, firstOutcome.Status);
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadAsync_WhenCanceledDifferentArtifactIsStillDraining_WaitsThenStartsNewFlight()
    {
        var firstPayload = Encoding.UTF8.GetBytes("first canceled update");
        var secondPayload = Encoding.UTF8.GetBytes("replacement update");
        var firstManifest = CreateManifest("1.0.1", firstPayload);
        var secondManifest = CreateManifest("1.0.2", secondPayload);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var temporaryRoot = CreateTemporaryRoot();
        try
        {
            using var httpClient = new HttpClient(new DelegateHttpHandler(async (request, _) =>
            {
                var call = Interlocked.Increment(ref calls);
                if (call == 1)
                {
                    firstStarted.TrySetResult();
                    await releaseFirst.Task;
                    return PackageResponse(request, firstPayload);
                }

                secondStarted.TrySetResult();
                await releaseSecond.Task;
                return PackageResponse(request, secondPayload);
            }));
            using var downloader = new ClientUpdatePackageDownloader(
                temporaryRoot,
                httpClient,
                NullLogger<ClientUpdatePackageDownloader>.Instance);

            var first = downloader.DownloadAsync(firstManifest);
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            downloader.Cancel();
            var replacement = downloader.DownloadAsync(secondManifest);
            using var waiterCancellation = new CancellationTokenSource();
            var canceledWaiter = downloader.DownloadAsync(
                secondManifest,
                cancellationToken: waiterCancellation.Token);
            Assert.False(replacement.IsCompleted);
            Assert.Equal(1, Volatile.Read(ref calls));

            releaseFirst.TrySetResult();
            var firstOutcome = await first;
            await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            waiterCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledWaiter);
            Assert.False(replacement.IsCompleted);
            releaseSecond.TrySetResult();
            var replacementOutcome = await replacement;

            Assert.Equal(ClientUpdateDownloadStatus.Canceled, firstOutcome.Status);
            Assert.Equal(ClientUpdateDownloadStatus.Success, replacementOutcome.Status);
            Assert.Equal(2, Volatile.Read(ref calls));
            Assert.Equal(
                Path.Combine(temporaryRoot, "RelayCove-1.0.2.zip"),
                replacementOutcome.ArchivePath);
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadAsync_WhenCanceledSameArtifactIsStillDraining_WaitsThenRestartsFlight()
    {
        var payload = Encoding.UTF8.GetBytes("same artifact retry");
        var manifest = CreateManifest("1.0.1", payload);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var temporaryRoot = CreateTemporaryRoot();
        try
        {
            using var httpClient = new HttpClient(new DelegateHttpHandler(async (request, _) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    firstStarted.TrySetResult();
                    await releaseFirst.Task;
                }

                return PackageResponse(request, payload);
            }));
            using var downloader = new ClientUpdatePackageDownloader(
                temporaryRoot,
                httpClient,
                NullLogger<ClientUpdatePackageDownloader>.Instance);

            var first = downloader.DownloadAsync(manifest);
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            downloader.Cancel();
            var retry = downloader.DownloadAsync(manifest);

            Assert.False(retry.IsCompleted);
            Assert.Equal(1, Volatile.Read(ref calls));
            releaseFirst.TrySetResult();
            var firstOutcome = await first;
            var retryOutcome = await retry;

            Assert.Equal(ClientUpdateDownloadStatus.Canceled, firstOutcome.Status);
            Assert.Equal(ClientUpdateDownloadStatus.Success, retryOutcome.Status);
            Assert.Equal(2, Volatile.Read(ref calls));
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
    public async Task CheckAsync_WhenDownloadIsStillDraining_AllowsNewManifestDownloadWithoutStaleRevival()
    {
        var firstManifest = CreateManifest("1.0.1");
        var secondManifest = CreateManifest("1.0.2", Encoding.UTF8.GetBytes("second artifact"));
        var manifestCall = 0;
        var transport = new FakeManifestTransport((_, _) => Task.FromResult(
            ClientUpdateManifestFetchOutcome.Success(
                Interlocked.Increment(ref manifestCall) == 1 ? firstManifest : secondManifest)));
        var downloader = new DelayedSwitchingDownloader();
        await using var coordinator = new ClientUpdateCoordinator(
            transport,
            new FixedVersionProvider("1.0.0"),
            downloader,
            NullLogger<ClientUpdateCoordinator>.Instance);

        await coordinator.CheckAsync(new Uri("https://relay.example"));
        var oldDownload = coordinator.DownloadAsync();
        await downloader.FirstStarted.WaitAsync(TimeSpan.FromSeconds(5));
        var checkedState = await coordinator.CheckAsync(new Uri("https://relay.example"));
        Assert.Equal("1.0.2", checkedState.Manifest!.Version);

        var newDownload = coordinator.DownloadAsync();
        await downloader.SecondStarted.WaitAsync(TimeSpan.FromSeconds(5));
        downloader.CompleteFirst(ClientUpdateDownloadOutcome.Failure(
            ClientUpdateDownloadStatus.Canceled));
        await oldDownload;
        downloader.CompleteSecond(ClientUpdateDownloadOutcome.Success(
            Path.Combine(Path.GetTempPath(), "RelayCove-1.0.2.zip")));
        var downloadedState = await newDownload;

        Assert.Equal(2, downloader.CallCount);
        Assert.Equal(1, downloader.CancelCount);
        Assert.Equal(ClientUpdatePhase.Downloaded, downloadedState.Phase);
        Assert.Equal("1.0.2", downloadedState.Manifest!.Version);
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

    private static HttpResponseMessage PackageResponse(HttpRequestMessage request, byte[] payload)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new ByteArrayContent(payload),
        };
        response.Content.Headers.ContentLength = payload.Length;
        return response;
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

    private sealed class DelayedSwitchingDownloader : IClientUpdateDownloader
    {
        private readonly TaskCompletionSource firstStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource secondStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<ClientUpdateDownloadOutcome> firstCompletion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<ClientUpdateDownloadOutcome> secondCompletion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int callCount;
        private int cancelCount;

        public int CallCount => Volatile.Read(ref callCount);

        public int CancelCount => Volatile.Read(ref cancelCount);

        public Task FirstStarted => firstStarted.Task;

        public Task SecondStarted => secondStarted.Task;

        public void Cancel() => Interlocked.Increment(ref cancelCount);

        public Task<ClientUpdateDownloadOutcome> DownloadAsync(
            UpdateManifestDto manifest,
            Action<ClientUpdateDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref callCount);
            if (call == 1)
            {
                firstStarted.TrySetResult();
                return firstCompletion.Task;
            }

            secondStarted.TrySetResult();
            return secondCompletion.Task;
        }

        public void CompleteFirst(ClientUpdateDownloadOutcome outcome) =>
            firstCompletion.TrySetResult(outcome);

        public void CompleteSecond(ClientUpdateDownloadOutcome outcome) =>
            secondCompletion.TrySetResult(outcome);
    }

    private sealed class DelegateHttpHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => sendAsync(request, cancellationToken);
    }

    private sealed class UnknownLengthContent(byte[] payload) : HttpContent
    {
        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context) => stream.WriteAsync(payload).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class StalledContent(TaskCompletionSource bodyStarted) : HttpContent
    {
        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context)
        {
            _ = stream;
            _ = context;
            bodyStarted.TrySetResult();
            return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
