using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RelayCove.Server.Options;
using RelayCove.Shared.Updates;

namespace RelayCove.Server.Services;

public sealed class UpdateHostingService
{
    public const string ArtifactRoutePrefix = "/api/updates/artifacts/";
    private const int MaximumManifestBytes = 64 * 1024;
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private readonly string manifestPath;
    private readonly string manifestDirectoryPath;
    private readonly ILogger<UpdateHostingService> logger;
    private readonly Func<FileStream, CancellationToken, Task<string>> computeSha256Async;
    private readonly SemaphoreSlim validationGate = new(1, 1);
    private ValidationCacheEntry? validationCache;

    public UpdateHostingService(
        IOptions<UpdateOptions> options,
        ILogger<UpdateHostingService> logger)
        : this(options, logger, ComputeSha256Async)
    {
    }

    internal UpdateHostingService(
        IOptions<UpdateOptions> options,
        ILogger<UpdateHostingService> logger,
        Func<FileStream, CancellationToken, Task<string>> computeSha256Async)
    {
        manifestPath = Path.GetFullPath(options.Value.ManifestPath);
        manifestDirectoryPath = Path.GetDirectoryName(manifestPath)
            ?? throw new InvalidOperationException("The update manifest path must have a parent directory.");
        this.logger = logger;
        this.computeSha256Async = computeSha256Async;
    }

    internal async Task<UpdateManifestDto?> GetManifestAsync(CancellationToken cancellationToken)
    {
        var snapshot = await GetVerifiedSnapshotAsync(cancellationToken);
        return snapshot?.Manifest;
    }

    internal async Task<UpdateArtifactReadHandle?> OpenCurrentArtifactAsync(
        string requestedFileName,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var snapshot = await GetVerifiedSnapshotAsync(cancellationToken);
            if (snapshot is null ||
                !string.Equals(snapshot.ArtifactFileName, requestedFileName, StringComparison.Ordinal))
            {
                return null;
            }

            FileStream stream;
            try
            {
                stream = OpenRead(snapshot.ArtifactPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Invalidate(snapshot.CacheEntry);
                logger.LogWarning("The current update artifact could not be opened.");
                continue;
            }

            try
            {
                var identity = ProbeFile(snapshot.ArtifactPath, manifestDirectoryPath);
                if (stream.Length == snapshot.Manifest.Artifact.SizeBytes &&
                    identity == snapshot.ArtifactIdentity)
                {
                    return new UpdateArtifactReadHandle(stream, snapshot.Manifest.Artifact.Sha256);
                }
            }
            catch (IOException)
            {
            }

            await stream.DisposeAsync();
            Invalidate(snapshot.CacheEntry);
            logger.LogWarning("The current update artifact changed before download.");
        }

        return null;
    }

    private async Task<VerifiedUpdateSnapshot?> GetVerifiedSnapshotAsync(CancellationToken cancellationToken)
    {
        var cached = Volatile.Read(ref validationCache);
        if (cached is not null)
        {
            if (IsCacheCurrent(cached))
            {
                return cached.Snapshot;
            }

            Invalidate(cached);
        }

        await validationGate.WaitAsync(cancellationToken);
        try
        {
            cached = Volatile.Read(ref validationCache);
            if (cached is not null)
            {
                if (IsCacheCurrent(cached))
                {
                    return cached.Snapshot;
                }

                Invalidate(cached);
            }

            var validated = await ValidateCurrentFilesAsync(cancellationToken);
            Volatile.Write(ref validationCache, validated);
            return validated.Snapshot;
        }
        finally
        {
            validationGate.Release();
        }
    }

    private bool IsCacheCurrent(ValidationCacheEntry cached)
    {
        if (ProbeFile(manifestPath, manifestDirectoryPath) != cached.ManifestIdentity)
        {
            return false;
        }

        return cached.ArtifactPath is null ||
            ProbeFile(cached.ArtifactPath, manifestDirectoryPath) == cached.ArtifactIdentity;
    }

    private async Task<ValidationCacheEntry> ValidateCurrentFilesAsync(CancellationToken cancellationToken)
    {
        var manifestIdentity = ProbeFile(manifestPath, manifestDirectoryPath);
        if (manifestIdentity.Status != FileProbeStatus.RegularFile ||
            manifestIdentity.Length is < 1 or > MaximumManifestBytes)
        {
            logger.LogWarning("The update manifest is unavailable or outside the supported size limit.");
            return ValidationCacheEntry.Failure(manifestIdentity);
        }

        byte[] content;
        try
        {
            await using var stream = OpenRead(manifestPath, 16 * 1024);
            if (stream.Length != manifestIdentity.Length)
            {
                return ValidationCacheEntry.TransientFailure();
            }

            content = GC.AllocateUninitializedArray<byte>((int)stream.Length);
            await stream.ReadExactlyAsync(content, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("The update manifest could not be read.");
            return ValidationCacheEntry.TransientFailure();
        }

        var manifestIdentityAfterRead = ProbeFile(manifestPath, manifestDirectoryPath);
        if (manifestIdentityAfterRead != manifestIdentity)
        {
            logger.LogWarning("The update manifest changed while it was being read.");
            return ValidationCacheEntry.TransientFailure();
        }

        UpdateManifestDto? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<UpdateManifestDto>(content, WebJson);
        }
        catch (JsonException)
        {
            logger.LogWarning("The update manifest is invalid.");
            return ValidationCacheEntry.Failure(manifestIdentity);
        }

        if (!UpdateManifestValidator.TryValidate(manifest, out _) ||
            !TryGetArtifactFileName(manifest!.Artifact.Url, out var artifactFileName))
        {
            logger.LogWarning("The update manifest is unsupported.");
            return ValidationCacheEntry.Failure(manifestIdentity);
        }

        var artifactPath = Path.Combine(manifestDirectoryPath, artifactFileName);
        var artifactIdentity = ProbeFile(artifactPath, manifestDirectoryPath);
        if (artifactIdentity.Status != FileProbeStatus.RegularFile ||
            artifactIdentity.Length != manifest.Artifact.SizeBytes)
        {
            logger.LogWarning("The current update artifact is unavailable or has an unexpected length.");
            return ValidationCacheEntry.Failure(manifestIdentity, artifactPath, artifactIdentity);
        }

        string actualSha256;
        try
        {
            await using var stream = OpenRead(artifactPath);
            if (stream.Length != artifactIdentity.Length)
            {
                return ValidationCacheEntry.TransientFailure(manifestIdentity, artifactPath);
            }

            actualSha256 = await computeSha256Async(stream, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("The current update artifact could not be verified.");
            return ValidationCacheEntry.TransientFailure(manifestIdentity, artifactPath);
        }

        var artifactIdentityAfterHash = ProbeFile(artifactPath, manifestDirectoryPath);
        if (artifactIdentityAfterHash != artifactIdentity)
        {
            logger.LogWarning("The current update artifact changed while it was being verified.");
            return ValidationCacheEntry.TransientFailure(manifestIdentity, artifactPath);
        }

        if (!string.Equals(actualSha256, manifest.Artifact.Sha256, StringComparison.Ordinal))
        {
            logger.LogWarning("The current update artifact hash does not match its manifest.");
            return ValidationCacheEntry.Failure(manifestIdentity, artifactPath, artifactIdentity);
        }

        var cacheEntry = new ValidationCacheEntry(manifestIdentity, artifactPath, artifactIdentity, null);
        var snapshot = new VerifiedUpdateSnapshot(
            manifest,
            artifactFileName,
            artifactPath,
            artifactIdentity,
            cacheEntry);
        cacheEntry.Snapshot = snapshot;
        return cacheEntry;
    }

    private void Invalidate(ValidationCacheEntry expected)
    {
        _ = Interlocked.CompareExchange(ref validationCache, null, expected);
    }

    private FileIdentity ProbeFile(string filePath, string directoryPath)
    {
        try
        {
            var fullDirectoryPath = Path.GetFullPath(directoryPath);
            var fullFilePath = Path.GetFullPath(filePath);
            if (!IsChildOf(fullFilePath, fullDirectoryPath) ||
                !IsDirectoryTreeFreeOfReparsePoints(fullDirectoryPath))
            {
                return FileIdentity.Unsafe;
            }

            var attributes = File.GetAttributes(fullFilePath);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                return FileIdentity.Unsafe;
            }

            var info = new FileInfo(fullFilePath);
            return new FileIdentity(
                FileProbeStatus.RegularFile,
                info.Length,
                info.LastWriteTimeUtc.Ticks);
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return FileIdentity.Missing;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return FileIdentity.Unsafe;
        }
    }

    private static bool TryGetArtifactFileName(string artifactUrl, out string fileName)
    {
        fileName = string.Empty;
        if (!Uri.TryCreate(artifactUrl, UriKind.Absolute, out var artifactUri))
        {
            return false;
        }

        var path = Uri.UnescapeDataString(artifactUri.AbsolutePath);
        var fileNameStart = path.LastIndexOf('/') + 1;
        var routeStart = fileNameStart - ArtifactRoutePrefix.Length;
        if (routeStart < 0 ||
            !path.AsSpan(routeStart, ArtifactRoutePrefix.Length)
                .Equals(ArtifactRoutePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var candidate = path[fileNameStart..];
        if (!IsSafeFileName(candidate))
        {
            return false;
        }

        fileName = candidate;
        return true;
    }

    private static FileStream OpenRead(string path, int bufferSize = 64 * 1024) =>
        new(path, new FileStreamOptions
        {
            Access = FileAccess.Read,
            Mode = FileMode.Open,
            Share = FileShare.Read,
            BufferSize = bufferSize,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        });

    private static async Task<string> ComputeSha256Async(
        FileStream stream,
        CancellationToken cancellationToken) =>
        Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();

    private static bool IsDirectoryTreeFreeOfReparsePoints(string directoryPath)
    {
        for (var current = new DirectoryInfo(directoryPath); current is not null; current = current.Parent)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsChildOf(string filePath, string directoryPath)
    {
        var relativePath = Path.GetRelativePath(directoryPath, filePath);
        return !Path.IsPathRooted(relativePath) &&
            !relativePath.Equals("..", StringComparison.Ordinal) &&
            !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
            !relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static bool IsSafeFileName(string candidate) =>
        !string.IsNullOrWhiteSpace(candidate) &&
        candidate.Length <= 255 &&
        !candidate.Equals(".", StringComparison.Ordinal) &&
        !candidate.Equals("..", StringComparison.Ordinal) &&
        !candidate.EndsWith(".", StringComparison.Ordinal) &&
        !candidate.EndsWith(' ') &&
        !candidate.Contains('/') &&
        !candidate.Contains('\\') &&
        !candidate.Contains(':') &&
        !candidate.Contains('\0') &&
        !candidate.Any(character => character < ' ' || character is '<' or '>' or '"' or '|' or '?' or '*') &&
        string.Equals(candidate, Path.GetFileName(candidate), StringComparison.Ordinal);

    private enum FileProbeStatus
    {
        Missing,
        Unsafe,
        RegularFile,
    }

    private readonly record struct FileIdentity(FileProbeStatus Status, long Length, long LastWriteUtcTicks)
    {
        public static FileIdentity Missing { get; } = new(FileProbeStatus.Missing, 0, 0);

        public static FileIdentity Unsafe { get; } = new(FileProbeStatus.Unsafe, 0, 0);
    }

    private sealed class ValidationCacheEntry(
        FileIdentity manifestIdentity,
        string? artifactPath,
        FileIdentity artifactIdentity,
        VerifiedUpdateSnapshot? snapshot)
    {
        public FileIdentity ManifestIdentity { get; } = manifestIdentity;

        public string? ArtifactPath { get; } = artifactPath;

        public FileIdentity ArtifactIdentity { get; } = artifactIdentity;

        public VerifiedUpdateSnapshot? Snapshot { get; set; } = snapshot;

        public static ValidationCacheEntry Failure(FileIdentity manifestIdentity) =>
            new(manifestIdentity, null, FileIdentity.Missing, null);

        public static ValidationCacheEntry Failure(
            FileIdentity manifestIdentity,
            string artifactPath,
            FileIdentity artifactIdentity) =>
            new(manifestIdentity, artifactPath, artifactIdentity, null);

        public static ValidationCacheEntry TransientFailure() =>
            new(FileIdentity.Unsafe, null, FileIdentity.Missing, null);

        public static ValidationCacheEntry TransientFailure(
            FileIdentity manifestIdentity,
            string artifactPath) =>
            new(manifestIdentity, artifactPath, FileIdentity.Unsafe, null);
    }

    private sealed record VerifiedUpdateSnapshot(
        UpdateManifestDto Manifest,
        string ArtifactFileName,
        string ArtifactPath,
        FileIdentity ArtifactIdentity,
        ValidationCacheEntry CacheEntry);
}

internal sealed class UpdateArtifactReadHandle(FileStream stream, string sha256) : IAsyncDisposable
{
    public FileStream Stream { get; } = stream;

    public string Sha256 { get; } = sha256;

    public ValueTask DisposeAsync() => Stream.DisposeAsync();
}
