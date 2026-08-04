using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RelayCove.Shared.Updates;

namespace RelayCove.Updater;

internal sealed class PortablePackageValidator
{
    private const int MaximumEntries = 10_000;
    private const long MaximumUncompressedBytes = UpdateConstants.MaximumArtifactBytes;
    private const long MaximumManifestBytes = 8L * 1024 * 1024;
    private readonly Action? archiveLocked;

    internal PortablePackageValidator(Action? archiveLocked = null)
    {
        this.archiveLocked = archiveLocked;
    }

    internal PackageValidationResult ValidateAndExtract(UpdaterOptions options, string stagingPath)
    {
        using var archiveStream = new FileStream(
            options.ArchivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        if (archiveStream.Length != options.ExpectedSize || options.ExpectedSize > UpdateConstants.MaximumArtifactBytes ||
            !string.Equals(HashStream(archiveStream), options.ExpectedSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Update archive validation failed.");
        }

        archiveStream.Position = 0;
        archiveLocked?.Invoke();
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: false);
        var packageRoot = $"RelayCove.Client-{options.ExpectedVersion}-win-x64";
        var prefix = packageRoot + "/";
        var files = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        long totalLength = 0;
        foreach (var entry in archive.Entries)
        {
            if (files.Count >= MaximumEntries || !IsSafeEntry(entry, prefix, out var relativePath) ||
                entry.Length > MaximumUncompressedBytes - totalLength || !files.TryAdd(relativePath, entry))
            {
                throw new InvalidDataException("Update archive layout is invalid.");
            }

            totalLength += entry.Length;
        }

        if (!files.TryGetValue("manifest.json", out var manifestEntry) || manifestEntry.Length > MaximumManifestBytes)
        {
            throw new InvalidDataException("Update package manifest is missing.");
        }

        using var manifest = ReadManifest(manifestEntry, options, packageRoot);
        ValidateManifestFiles(manifest, files);
        if (!files.ContainsKey("RelayCove.Client.exe") || !files.ContainsKey("RelayCove.Updater.exe"))
        {
            throw new InvalidDataException("Update package entry points are missing.");
        }

        Directory.CreateDirectory(stagingPath);
        foreach (var pair in files)
        {
            var destination = Path.Combine(stagingPath, pair.Key.Replace('/', Path.DirectorySeparatorChar));
            var destinationDirectory = Path.GetDirectoryName(destination) ?? throw new InvalidDataException("Update package path is invalid.");
            Directory.CreateDirectory(destinationDirectory);
            using var input = pair.Value.Open();
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
        }

        return new PackageValidationResult { StagingPath = stagingPath };
    }

    private static bool IsSafeEntry(ZipArchiveEntry entry, string prefix, out string relativePath)
    {
        relativePath = string.Empty;
        var name = entry.FullName;
        if (string.IsNullOrWhiteSpace(name) || name.Contains('\\') || Path.IsPathFullyQualified(name) ||
            !name.StartsWith(prefix, StringComparison.Ordinal) || name.EndsWith("/", StringComparison.Ordinal) ||
            entry.ExternalAttributes != 0x00000080)
        {
            return false;
        }

        relativePath = name[prefix.Length..];
        var segments = relativePath.Split('/', StringSplitOptions.None);
        return segments.Length > 0 && segments.All(IsSafeSegment);
    }

    private static JsonDocument ReadManifest(ZipArchiveEntry entry, UpdaterOptions options, string packageRoot)
    {
        using var stream = entry.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        try
        {
            var document = JsonDocument.Parse(memory.ToArray());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !HasNumber(root, "schemaVersion", 1) ||
                !HasString(root, "version", options.ExpectedVersion.ToString()) ||
                !HasString(root, "rid", "win-x64") ||
                !HasString(root, "packageRoot", packageRoot) ||
                !HasBoolean(root, "sourceTreeClean", true) ||
                !HasBoolean(root, "selfContained", true) ||
                !HasBoolean(root, "windowsAppSdkSelfContained", true) ||
                !root.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
            {
                document.Dispose();
                throw new InvalidDataException("Update package manifest is invalid.");
            }

            return document;
        }
        catch (JsonException)
        {
            throw new InvalidDataException("Update package manifest is invalid.");
        }
    }

    private static void ValidateManifestFiles(JsonDocument manifest, IReadOnlyDictionary<string, ZipArchiveEntry> archiveFiles)
    {
        var expected = new Dictionary<string, (long Length, string Hash)>(StringComparer.OrdinalIgnoreCase);
        string? previousPath = null;
        foreach (var file in manifest.RootElement.GetProperty("files").EnumerateArray())
        {
            if (file.ValueKind != JsonValueKind.Object || !file.TryGetProperty("path", out var pathValue) ||
                !file.TryGetProperty("length", out var lengthValue) || !file.TryGetProperty("sha256", out var hashValue) ||
                !file.TryGetProperty("attributes", out var attributesValue) || pathValue.ValueKind != JsonValueKind.String ||
                !lengthValue.TryGetInt64(out var length) || hashValue.ValueKind != JsonValueKind.String ||
                attributesValue.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException("Update package manifest files are invalid.");
            }

            var path = pathValue.GetString()!;
            var hash = hashValue.GetString()!;
            if (!IsManifestPath(path) || !IsLowerSha256(hash) || length < 0 || attributesValue.GetString() != "00000080" ||
                (previousPath is not null && string.CompareOrdinal(previousPath, path) >= 0) || !expected.TryAdd(path, (length, hash)))
            {
                throw new InvalidDataException("Update package manifest files are invalid.");
            }

            previousPath = path;
        }

        if (expected.Count != archiveFiles.Count - 1)
        {
            throw new InvalidDataException("Update package manifest file count is invalid.");
        }

        foreach (var pair in expected)
        {
            if (!archiveFiles.TryGetValue(pair.Key, out var entry) || entry.Length != pair.Value.Length ||
                !string.Equals(HashEntry(entry), pair.Value.Hash, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Update package file verification failed.");
            }
        }
    }

    private static bool IsManifestPath(string path) => !string.IsNullOrWhiteSpace(path) && !path.Contains('\\') &&
        !Path.IsPathFullyQualified(path) && path.Split('/').All(IsSafeSegment);

    private static bool IsSafeSegment(string segment)
    {
        if (string.IsNullOrEmpty(segment) || segment is "." or ".." || segment.Contains(':') || segment.EndsWith(' ') || segment.EndsWith('.'))
        {
            return false;
        }

        var deviceName = segment.Split('.', 2, StringSplitOptions.None)[0];
        return !string.Equals(deviceName, "CON", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(deviceName, "PRN", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(deviceName, "AUX", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(deviceName, "NUL", StringComparison.OrdinalIgnoreCase) &&
            !(deviceName.Length == 4 && (deviceName.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || deviceName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && deviceName[3] is >= '1' and <= '9');
    }

    private static bool HasNumber(JsonElement element, string name, int expected) => element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value) && value == expected;
    private static bool HasString(JsonElement element, string name, string expected) => element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String && property.GetString() == expected;
    private static bool HasBoolean(JsonElement element, string name, bool expected) => element.TryGetProperty(name, out var property) && property.ValueKind is JsonValueKind.True or JsonValueKind.False && property.GetBoolean() == expected;
    private static string HashStream(Stream stream) => Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    private static string HashEntry(ZipArchiveEntry entry) { using var stream = entry.Open(); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
    private static bool IsLowerSha256(string value) => value.Length == 64 && value == value.ToLowerInvariant() && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
