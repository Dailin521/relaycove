using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using RelayCove.Shared.Updates;

namespace RelayCove.Client.Updates;

internal sealed class ClientUpdatePackageDownloader : IClientUpdateDownloader, IDisposable
{
    private const int CopyBufferSize = 80 * 1024;
    private readonly object gate = new();
    private readonly HttpClient httpClient;
    private readonly string cacheRoot;
    private readonly ILogger logger;
    private DownloadFlight? activeFlight;
    private int disposed;

    public ClientUpdatePackageDownloader(string cacheRoot, HttpClient httpClient, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        if (!Path.IsPathFullyQualified(cacheRoot))
        {
            throw new ArgumentException("The update cache root must be absolute.", nameof(cacheRoot));
        }

        this.cacheRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cacheRoot));
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task<ClientUpdateDownloadOutcome> DownloadAsync(
        UpdateManifestDto manifest,
        Action<ClientUpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!UpdateManifestValidator.TryValidate(manifest, out _))
        {
            throw new ArgumentException("The update manifest is invalid.", nameof(manifest));
        }

        return JoinOrStartAsync(manifest, progress, cancellationToken);
    }

    public void Cancel()
    {
        lock (gate)
        {
            activeFlight?.Cancellation.Cancel();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        Cancel();
    }

    public override string ToString() =>
        $"{nameof(ClientUpdatePackageDownloader)} {{ CacheRoot = [REDACTED] }}";

    private async Task<ClientUpdateDownloadOutcome> JoinOrStartAsync(
        UpdateManifestDto manifest,
        Action<ClientUpdateDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            ThrowIfDisposed();
            Task<ClientUpdateDownloadOutcome>? flightTask = null;
            Task<ClientUpdateDownloadOutcome>? canceledFlightTask = null;
            lock (gate)
            {
                if (activeFlight is { } existing)
                {
                    if (existing.Cancellation.IsCancellationRequested)
                    {
                        canceledFlightTask = existing.Task!;
                    }
                    else if (HasSameArtifact(existing.Manifest, manifest))
                    {
                        existing.AddProgress(progress);
                        flightTask = existing.Task!;
                    }
                    else
                    {
                        return ClientUpdateDownloadOutcome.Failure(
                            ClientUpdateDownloadStatus.InProgress);
                    }
                }
                else
                {
                    var flight = new DownloadFlight(manifest, progress);
                    activeFlight = flight;
                    flight.Task = DownloadCoreAsync(flight);
                    flightTask = flight.Task;
                }
            }

            if (canceledFlightTask is not null)
            {
                await DrainCanceledFlightAsync(canceledFlightTask, cancellationToken).ConfigureAwait(false);
                continue;
            }

            return await WaitForCallerAsync(flightTask!, cancellationToken).ConfigureAwait(false);
        }
    }

    private static Task<ClientUpdateDownloadOutcome> WaitForCallerAsync(
        Task<ClientUpdateDownloadOutcome> flightTask,
        CancellationToken cancellationToken) =>
        cancellationToken.CanBeCanceled
            ? flightTask.WaitAsync(cancellationToken)
            : flightTask;

    private async Task DrainCanceledFlightAsync(
        Task<ClientUpdateDownloadOutcome> canceledFlightTask,
        CancellationToken cancellationToken)
    {
        try
        {
            await WaitForCallerAsync(canceledFlightTask, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            logger.LogWarning(
                "Canceled update package flight drained with a failure; errorType={ErrorType}.",
                exception.GetType().Name);
        }
    }

    private async Task<ClientUpdateDownloadOutcome> DownloadCoreAsync(DownloadFlight flight)
    {
        string? stagingPath = null;
        try
        {
            var manifest = flight.Manifest;
            var artifact = manifest.Artifact;
            var finalPath = GetFinalPath(manifest);
            stagingPath = GetStagingPath(manifest);
            EnsureSafeDirectory(cacheRoot);
            if (await IsMatchingFileAsync(finalPath, artifact.SizeBytes, artifact.Sha256, flight.Cancellation.Token)
                    .ConfigureAwait(false))
            {
                return ClientUpdateDownloadOutcome.Success(finalPath);
            }

            DeleteOwnedFile(stagingPath);
            DeleteOwnedFile(finalPath);
            var artifactUri = new Uri(artifact.Url, UriKind.Absolute);
            using var request = new HttpRequestMessage(HttpMethod.Get, artifactUri);
            request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
            using var response = await httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    flight.Cancellation.Token)
                .ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK || !HasOriginalRequestUri(response, artifactUri) ||
                !HasExpectedHeaders(response, artifact.SizeBytes))
            {
                return ClientUpdateDownloadOutcome.Failure(MapResponseFailure(response.StatusCode));
            }

            await using (var input = await response.Content
                .ReadAsStreamAsync(flight.Cancellation.Token)
                .ConfigureAwait(false))
            await using (var staging = new FileStream(
                stagingPath,
                new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    BufferSize = CopyBufferSize,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                }))
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[CopyBufferSize];
                long bytesWritten = 0;
                while (true)
                {
                    var read = await input.ReadAsync(buffer, flight.Cancellation.Token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    if (read > artifact.SizeBytes - bytesWritten)
                    {
                        return ClientUpdateDownloadOutcome.Failure(ClientUpdateDownloadStatus.ProtocolError);
                    }

                    await staging.WriteAsync(buffer.AsMemory(0, read), flight.Cancellation.Token)
                        .ConfigureAwait(false);
                    hash.AppendData(buffer, 0, read);
                    bytesWritten += read;
                    flight.ReportProgress(new ClientUpdateDownloadProgress(bytesWritten, artifact.SizeBytes), logger);
                }

                await staging.FlushAsync(flight.Cancellation.Token).ConfigureAwait(false);
                var actualHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                if (bytesWritten != artifact.SizeBytes ||
                    !string.Equals(actualHash, artifact.Sha256, StringComparison.Ordinal))
                {
                    return ClientUpdateDownloadOutcome.Failure(ClientUpdateDownloadStatus.ProtocolError);
                }
            }

            flight.Cancellation.Token.ThrowIfCancellationRequested();
            File.Move(stagingPath, finalPath, overwrite: false);
            stagingPath = null;
            return ClientUpdateDownloadOutcome.Success(finalPath);
        }
        catch (OperationCanceledException) when (flight.Cancellation.IsCancellationRequested)
        {
            return ClientUpdateDownloadOutcome.Failure(ClientUpdateDownloadStatus.Canceled);
        }
        catch (OperationCanceledException)
        {
            return ClientUpdateDownloadOutcome.Failure(ClientUpdateDownloadStatus.TransientFailure);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or
            UnauthorizedAccessException or System.Security.SecurityException or InvalidDataException)
        {
            logger.LogWarning(
                "Update package download failed; errorType={ErrorType}.",
                exception.GetType().Name);
            return ClientUpdateDownloadOutcome.Failure(MapExceptionFailure(exception));
        }
        finally
        {
            try
            {
                DeleteOwnedFile(stagingPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(
                    "Update package staging cleanup failed; errorType={ErrorType}.",
                    exception.GetType().Name);
            }

            lock (gate)
            {
                if (ReferenceEquals(activeFlight, flight))
                {
                    activeFlight = null;
                }
            }

            flight.Cancellation.Dispose();
        }
    }

    private string GetFinalPath(UpdateManifestDto manifest) =>
        ResolveOwnedPath($"RelayCove-{manifest.Version}.zip");

    private string GetStagingPath(UpdateManifestDto manifest) =>
        ResolveOwnedPath($"RelayCove-{manifest.Version}.zip.part");

    private string ResolveOwnedPath(string fileName)
    {
        if (!fileName.EndsWith(".zip", StringComparison.Ordinal) &&
            !fileName.EndsWith(".zip.part", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The update cache file name is invalid.");
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cacheRoot));
        var candidate = Path.GetFullPath(Path.Combine(root, fileName));
        if (!string.Equals(Path.GetFileName(candidate), fileName, StringComparison.Ordinal) ||
            !string.Equals(Path.GetDirectoryName(candidate), root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The update cache path escaped its root.");
        }

        return candidate;
    }

    private static bool HasSameArtifact(UpdateManifestDto first, UpdateManifestDto second) =>
        string.Equals(first.Version, second.Version, StringComparison.Ordinal) &&
        first.Artifact.SizeBytes == second.Artifact.SizeBytes &&
        string.Equals(first.Artifact.Sha256, second.Artifact.Sha256, StringComparison.Ordinal) &&
        string.Equals(first.Artifact.Url, second.Artifact.Url, StringComparison.Ordinal);

    private static bool HasOriginalRequestUri(HttpResponseMessage response, Uri expectedRequestUri) =>
        response.RequestMessage?.RequestUri is { } effectiveRequestUri &&
        Uri.Compare(
            effectiveRequestUri,
            expectedRequestUri,
            UriComponents.AbsoluteUri,
            UriFormat.UriEscaped,
            StringComparison.Ordinal) == 0;

    private static bool HasExpectedHeaders(HttpResponseMessage response, long expectedSize) =>
        response.Content.Headers.ContentLength == expectedSize &&
        response.Content.Headers.ContentRange is null &&
        response.Content.Headers.ContentEncoding.Count == 0;

    private static ClientUpdateDownloadStatus MapResponseFailure(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or
            HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or
            HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout
            ? ClientUpdateDownloadStatus.TransientFailure
            : ClientUpdateDownloadStatus.ProtocolError;

    private static ClientUpdateDownloadStatus MapExceptionFailure(Exception exception) =>
        exception is UnauthorizedAccessException or System.Security.SecurityException or InvalidDataException
            ? ClientUpdateDownloadStatus.StorageFailure
            : ClientUpdateDownloadStatus.TransientFailure;

    private static async Task<bool> IsMatchingFileAsync(
        string path,
        long expectedSize,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        RejectReparsePoint(path);
        var info = new FileInfo(path);
        if (info.Length != expectedSize)
        {
            return false;
        }

        await using var stream = new FileStream(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                BufferSize = CopyBufferSize,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            });
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return string.Equals(
            Convert.ToHexString(hash).ToLowerInvariant(),
            expectedSha256,
            StringComparison.Ordinal);
    }

    private static void EnsureSafeDirectory(string path)
    {
        var current = new DirectoryInfo(path);
        while (current is not null)
        {
            if (current.Exists)
            {
                RejectReparsePoint(current.FullName);
            }

            current = current.Parent;
        }

        Directory.CreateDirectory(path);
        RejectReparsePoint(path);
    }

    private static void DeleteOwnedFile(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return;
        }

        RejectReparsePoint(path);
        File.Delete(path);
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("The update cache contains a reparse point.");
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(ClientUpdatePackageDownloader));
        }
    }

    private sealed class DownloadFlight
    {
        private readonly object progressGate = new();
        private readonly List<Action<ClientUpdateDownloadProgress>> progressCallbacks = [];

        public DownloadFlight(UpdateManifestDto manifest, Action<ClientUpdateDownloadProgress>? progress)
        {
            Manifest = manifest;
            AddProgress(progress);
        }

        public CancellationTokenSource Cancellation { get; } = new();

        public UpdateManifestDto Manifest { get; }

        public Task<ClientUpdateDownloadOutcome>? Task { get; set; }

        public void AddProgress(Action<ClientUpdateDownloadProgress>? progress)
        {
            if (progress is null)
            {
                return;
            }

            lock (progressGate)
            {
                progressCallbacks.Add(progress);
            }
        }

        public void ReportProgress(ClientUpdateDownloadProgress progress, ILogger logger)
        {
            Action<ClientUpdateDownloadProgress>[] callbacks;
            lock (progressGate)
            {
                callbacks = progressCallbacks.ToArray();
            }

            foreach (var callback in callbacks)
            {
                try
                {
                    callback(progress);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
                {
                    logger.LogWarning(
                        "Update package progress callback failed; errorType={ErrorType}.",
                        exception.GetType().Name);
                }
            }
        }
    }
}
