using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RelayCove.Server.Options;
using RelayCove.Shared.Updates;

namespace RelayCove.Server.Services;

public sealed class UpdateHostingService(
    IOptions<UpdateOptions> options,
    ILogger<UpdateHostingService> logger)
{
    public const string ArtifactRoutePrefix = "/api/updates/artifacts/";
    private const int MaximumManifestBytes = 64 * 1024;

    private readonly string manifestPath = Path.GetFullPath(options.Value.ManifestPath);

    public async Task<UpdateManifestDto?> GetManifestAsync(CancellationToken cancellationToken)
    {
        var manifest = await ReadManifestAsync(cancellationToken);
        return manifest?.Manifest;
    }

    public async Task<UpdateArtifactReadHandle?> OpenCurrentArtifactAsync(
        string requestedFileName,
        CancellationToken cancellationToken)
    {
        var manifest = await ReadManifestAsync(cancellationToken);
        if (manifest is null ||
            !string.Equals(manifest.ArtifactFileName, requestedFileName, StringComparison.Ordinal))
        {
            return null;
        }

        var artifactPath = Path.Combine(manifest.DirectoryPath, manifest.ArtifactFileName);
        if (!IsSafeExistingFile(artifactPath, manifest.DirectoryPath))
        {
            logger.LogWarning("The current update artifact is unavailable.");
            return null;
        }

        FileStream stream;
        try
        {
            stream = new FileStream(artifactPath, new FileStreamOptions
            {
                Access = FileAccess.Read,
                Mode = FileMode.Open,
                Share = FileShare.Read,
                BufferSize = 64 * 1024,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("The current update artifact could not be opened.");
            return null;
        }

        try
        {
            if (stream.Length != manifest.Manifest.Artifact.SizeBytes)
            {
                await stream.DisposeAsync();
                logger.LogWarning("The current update artifact length does not match its manifest.");
                return null;
            }

            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
            if (!string.Equals(hash, manifest.Manifest.Artifact.Sha256, StringComparison.Ordinal))
            {
                await stream.DisposeAsync();
                logger.LogWarning("The current update artifact hash does not match its manifest.");
                return null;
            }

            stream.Position = 0;
            return new UpdateArtifactReadHandle(stream, manifest.Manifest.Artifact.Sha256);
        }
        catch (OperationCanceledException)
        {
            await stream.DisposeAsync();
            throw;
        }
        catch (IOException)
        {
            await stream.DisposeAsync();
            logger.LogWarning("The current update artifact could not be verified.");
            return null;
        }
    }

    private async Task<CurrentManifest?> ReadManifestAsync(CancellationToken cancellationToken)
    {
        if (!IsSafeExistingFile(manifestPath, Path.GetDirectoryName(manifestPath)!))
        {
            logger.LogWarning("The update manifest is unavailable.");
            return null;
        }

        byte[] content;
        try
        {
            await using var stream = new FileStream(manifestPath, new FileStreamOptions
            {
                Access = FileAccess.Read,
                Mode = FileMode.Open,
                Share = FileShare.Read,
                BufferSize = 16 * 1024,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            });
            if (stream.Length is < 1 or > MaximumManifestBytes)
            {
                logger.LogWarning("The update manifest is outside the supported size limit.");
                return null;
            }

            content = GC.AllocateUninitializedArray<byte>((int)stream.Length);
            await stream.ReadExactlyAsync(content, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("The update manifest could not be read.");
            return null;
        }

        UpdateManifestDto? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<UpdateManifestDto>(content, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (JsonException)
        {
            logger.LogWarning("The update manifest is invalid.");
            return null;
        }

        if (!UpdateManifestValidator.TryValidate(manifest, out _) ||
            !TryGetArtifactFileName(manifest!.Artifact.Url, out var fileName))
        {
            logger.LogWarning("The update manifest is unsupported.");
            return null;
        }

        return new CurrentManifest(manifest, Path.GetDirectoryName(manifestPath)!, fileName);
    }

    private static bool TryGetArtifactFileName(string artifactUrl, out string fileName)
    {
        fileName = string.Empty;
        if (!Uri.TryCreate(artifactUrl, UriKind.Absolute, out var artifactUri))
        {
            return false;
        }

        var path = Uri.UnescapeDataString(artifactUri.AbsolutePath);
        if (!path.StartsWith(ArtifactRoutePrefix, StringComparison.Ordinal) ||
            path.Length == ArtifactRoutePrefix.Length)
        {
            return false;
        }

        var candidate = path[ArtifactRoutePrefix.Length..];
        if (!IsSafeFileName(candidate))
        {
            return false;
        }

        fileName = candidate;
        return true;
    }

    private static bool IsSafeExistingFile(string filePath, string directoryPath)
    {
        try
        {
            var fullDirectoryPath = Path.GetFullPath(directoryPath);
            var fullFilePath = Path.GetFullPath(filePath);
            if (!IsChildOf(fullFilePath, fullDirectoryPath) ||
                (File.GetAttributes(fullFilePath) & FileAttributes.ReparsePoint) != 0 ||
                !IsDirectoryTreeFreeOfReparsePoints(fullDirectoryPath))
            {
                return false;
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

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

    private sealed record CurrentManifest(UpdateManifestDto Manifest, string DirectoryPath, string ArtifactFileName);
}

public sealed class UpdateArtifactReadHandle(FileStream stream, string sha256) : IAsyncDisposable
{
    public FileStream Stream { get; } = stream;

    public string Sha256 { get; } = sha256;

    public ValueTask DisposeAsync() => Stream.DisposeAsync();
}
