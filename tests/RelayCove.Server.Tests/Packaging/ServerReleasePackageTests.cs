using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RelayCove.Server.Tests.Packaging;

public sealed partial class ServerReleasePackageTests
{
    private const string RuntimeIdentifier = "linux-x64";
    private static readonly TimeSpan PublishTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan VerifyTimeout = TimeSpan.FromMinutes(3);

    [Fact]
    public async Task ServerRelease_WhenBuiltTwice_IsEquivalentAndRejectsCorruption()
    {
        using var firstOutput = new TemporaryDefaultReleaseDirectory();
        using var secondOutput = new TemporaryArtifactDirectory("release-b");
        var version = firstOutput.Version;

        await AssertScriptSucceededAsync(
            "scripts/publish-server.ps1",
            new[] { "-Version", version, "-AllowDirty" },
            PublishTimeout);
        await AssertScriptSucceededAsync(
            "scripts/publish-server.ps1",
            new[] { "-Version", version, "-OutputRoot", secondOutput.Path, "-AllowDirty" },
            PublishTimeout);

        var firstPackage = InspectPackage(firstOutput.OutputRoot, version);
        var secondPackage = InspectPackage(secondOutput.Path, version);

        Assert.Equal(firstPackage.ArchiveSha256, secondPackage.ArchiveSha256);
        Assert.Equal(firstPackage.SidecarText, secondPackage.SidecarText);
        Assert.Equal(firstPackage.ManifestText, secondPackage.ManifestText);

        await AssertScriptSucceededAsync(
            "scripts/verify-server-release.ps1",
            new[]
            {
                "-Version", version,
                "-CompareOutputRoot", secondOutput.Path,
                "-AllowDirtySource",
            },
            VerifyTimeout);

        var sameRootComparison = await PowerShellProcess.RunAsync(
            "scripts/verify-server-release.ps1",
            new[]
            {
                "-Version", version,
                "-OutputRoot", secondOutput.Path,
                "-CompareOutputRoot", secondOutput.Path,
                "-AllowDirtySource",
            },
            VerifyTimeout);
        Assert.NotEqual(0, sameRootComparison.ExitCode);
        Assert.Contains("distinct release build root", sameRootComparison.CombinedOutput, StringComparison.Ordinal);

        CorruptArchive(secondPackage.ArchivePath);

        var corruptResult = await PowerShellProcess.RunAsync(
            "scripts/verify-server-release.ps1",
            new[]
            {
                "-Version", version,
                "-OutputRoot", secondOutput.Path,
                "-AllowDirtySource",
            },
            VerifyTimeout);

        Assert.NotEqual(0, corruptResult.ExitCode);
    }

    private static ReleasePackageInspection InspectPackage(string outputRoot, string version)
    {
        var packageName = $"RelayCove.Server-{version}-{RuntimeIdentifier}";
        var releaseDirectory = System.IO.Path.Combine(outputRoot, "server", version);
        var archivePath = System.IO.Path.Combine(releaseDirectory, $"{packageName}.tar.gz");
        var sidecarPath = $"{archivePath}.sha256";
        Assert.True(File.Exists(archivePath), $"Release archive is missing: {archivePath}");
        Assert.True(File.Exists(sidecarPath), $"SHA-256 sidecar is missing: {sidecarPath}");

        AssertArchiveDoesNotContainDynamicPaxHeaders(archivePath);
        using var archiveForHash = File.OpenRead(archivePath);
        var archiveHash = Convert.ToHexString(SHA256.HashData(archiveForHash)).ToLowerInvariant();
        var sidecarText = File.ReadAllText(sidecarPath).Trim();
        Assert.Matches($"^{archiveHash}  {Regex.Escape(System.IO.Path.GetFileName(archivePath))}$", sidecarText);

        var fileRecords = ReadArchive(archivePath, packageName, out var manifestText);
        AssertPackageEntries(fileRecords, packageName);
        AssertManifest(manifestText, fileRecords, packageName, version);
        AssertStagedProductionConfiguration(fileRecords, version);

        return new ReleasePackageInspection(archivePath, archiveHash, sidecarText, manifestText);
    }

    private static void AssertArchiveDoesNotContainDynamicPaxHeaders(string archivePath)
    {
        var forbiddenMarker = System.Text.Encoding.ASCII.GetBytes("PaxHeaders.");
        using var archive = File.OpenRead(archivePath);
        using var gzip = new GZipStream(archive, CompressionMode.Decompress);
        var buffer = new byte[64 * 1024];
        var matched = 0;
        int read;

        while ((read = gzip.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var index = 0; index < read; index++)
            {
                var value = buffer[index];
                if (value == forbiddenMarker[matched])
                {
                    matched++;
                    Assert.True(
                        matched < forbiddenMarker.Length,
                        "Archive contains a process-dependent PaxHeaders entry.");
                }
                else
                {
                    matched = value == forbiddenMarker[0] ? 1 : 0;
                }
            }
        }
    }

    private static IReadOnlyDictionary<string, ArchiveFileRecord> ReadArchive(
        string archivePath,
        string packageName,
        out string manifestText)
    {
        var files = new Dictionary<string, ArchiveFileRecord>(StringComparer.OrdinalIgnoreCase);
        var allPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? capturedManifest = null;

        using var archive = File.OpenRead(archivePath);
        using var gzip = new GZipStream(archive, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        TarEntry? entry;
        while ((entry = reader.GetNextEntry()) is not null)
        {
            Assert.Equal(TarEntryFormat.Ustar, entry.Format);
            var ustarEntry = Assert.IsType<UstarTarEntry>(entry);
            Assert.Equal(DateTimeOffset.UnixEpoch, entry.ModificationTime);
            Assert.Equal(0, entry.Uid);
            Assert.Equal(0, entry.Gid);
            Assert.True(string.IsNullOrEmpty(ustarEntry.UserName));
            Assert.True(string.IsNullOrEmpty(ustarEntry.GroupName));
            Assert.DoesNotContain('\\', entry.Name);
            var entryName = entry.Name.Replace('\\', '/').TrimEnd('/');
            AssertArchivePathIsSafe(entryName, packageName);
            Assert.True(allPaths.Add(entryName), $"Duplicate or case-colliding archive path: {entryName}");
            Assert.DoesNotContain(
                entry.EntryType,
                new[]
                {
                    TarEntryType.SymbolicLink,
                    TarEntryType.HardLink,
                    TarEntryType.BlockDevice,
                    TarEntryType.CharacterDevice,
                    TarEntryType.Fifo,
                });

            if (!IsRegularFile(entry.EntryType))
            {
                continue;
            }

            Assert.NotNull(entry.DataStream);
            var relativePath = entryName[(packageName.Length + 1)..];

            if (relativePath.Equals("manifest.json", StringComparison.Ordinal))
            {
                using var content = new MemoryStream();
                entry.DataStream.CopyTo(content);
                var bytes = content.ToArray();
                capturedManifest = System.Text.Encoding.UTF8.GetString(bytes);
                continue;
            }

            string? textContent = null;
            string sha256;
            if (relativePath.Equals(
                    "deploy/appsettings.Production.example.json",
                    StringComparison.Ordinal))
            {
                using var content = new MemoryStream();
                entry.DataStream.CopyTo(content);
                var bytes = content.ToArray();
                sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                textContent = System.Text.Encoding.UTF8.GetString(bytes);
            }
            else
            {
                sha256 = Convert.ToHexString(SHA256.HashData(entry.DataStream)).ToLowerInvariant();
            }

            Assert.True(files.TryAdd(
                relativePath,
                new ArchiveFileRecord(
                    entry.Length,
                    sha256,
                    entry.Mode,
                    textContent)),
                $"Duplicate package file: {relativePath}");
        }

        Assert.NotNull(capturedManifest);
        manifestText = capturedManifest;
        return files;
    }

    private static void AssertPackageEntries(
        IReadOnlyDictionary<string, ArchiveFileRecord> files,
        string packageName)
    {
        var requiredPaths = new[]
        {
            "app/RelayCove.Server",
            "migrate/RelayCove.Migrations",
            "deploy/relaycove.service",
            "deploy/nginx.conf",
            "deploy/appsettings.Production.example.json",
            "deploy/relaycove.env.example",
            "deploy/DEPLOYMENT.md",
        };

        foreach (var requiredPath in requiredPaths)
        {
            Assert.True(files.ContainsKey(requiredPath),
                $"{packageName} is missing required file: {requiredPath}");
        }

        AssertExecutable(files["app/RelayCove.Server"], "app/RelayCove.Server");
        AssertExecutable(files["migrate/RelayCove.Migrations"], "migrate/RelayCove.Migrations");

        foreach (var pair in files)
        {
            var path = pair.Key;
            Assert.False(ForbiddenPathRegex().IsMatch(path), $"Forbidden release entry: {path}");
            Assert.Equal(
                UnixFileMode.None,
                pair.Value.Mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite));
        }
    }

    private static void AssertManifest(
        string manifestText,
        IReadOnlyDictionary<string, ArchiveFileRecord> archiveFiles,
        string packageName,
        string version)
    {
        using var document = JsonDocument.Parse(manifestText);
        var root = document.RootElement;
        Assert.Equal(1, GetProperty(root, "schemaVersion").GetInt32());
        Assert.Equal(version, GetProperty(root, "version").GetString());
        Assert.Equal(RuntimeIdentifier, GetProperty(root, "rid").GetString());
        Assert.True(GetProperty(root, "selfContained").GetBoolean());
        Assert.Equal(packageName, GetProperty(root, "packageRoot").GetString());
        Assert.Matches("^[0-9a-f]{40}$", GetProperty(root, "commit").GetString() ?? string.Empty);
        Assert.False(string.IsNullOrWhiteSpace(GetProperty(root, "sdkVersion").GetString()));
        Assert.Contains(
            GetProperty(root, "sourceTreeClean").ValueKind,
            new[] { JsonValueKind.True, JsonValueKind.False });

        var manifestFiles = GetProperty(root, "files").EnumerateArray().ToArray();
        Assert.Equal(archiveFiles.Count, manifestFiles.Length);
        var paths = manifestFiles.Select(file => GetProperty(file, "path").GetString()!).ToArray();
        Assert.Equal(paths.OrderBy(path => path, StringComparer.Ordinal).ToArray(), paths);

        foreach (var file in manifestFiles)
        {
            var path = GetProperty(file, "path").GetString();
            Assert.NotNull(path);
            Assert.True(archiveFiles.TryGetValue(path, out var archiveFile),
                $"Manifest lists a file absent from the archive: {path}");
            Assert.Equal(archiveFile.Length, GetProperty(file, "length").GetInt64());
            Assert.Equal(archiveFile.Sha256, GetProperty(file, "sha256").GetString());
            Assert.Equal(ToOctalMode(archiveFile.Mode), ReadManifestMode(GetProperty(file, "mode")));
        }
    }

    private static void AssertStagedProductionConfiguration(
        IReadOnlyDictionary<string, ArchiveFileRecord> archiveFiles,
        string version)
    {
        const string configurationPath = "deploy/appsettings.Production.example.json";
        var configurationText = archiveFiles[configurationPath].TextContent;
        Assert.NotNull(configurationText);
        Assert.DoesNotContain("REPLACE_WITH_PACKAGE_VERSION", configurationText, StringComparison.Ordinal);

        using var document = JsonDocument.Parse(configurationText);
        var authentication = GetProperty(document.RootElement, "Authentication");
        Assert.Equal(version, GetProperty(authentication, "ServerVersion").GetString());
    }

    private static JsonElement GetProperty(JsonElement element, string name)
    {
        Assert.True(element.TryGetProperty(name, out var value), $"Manifest property is missing: {name}");
        return value;
    }

    private static string ReadManifestMode(JsonElement mode)
    {
        return mode.ValueKind switch
        {
            JsonValueKind.String => mode.GetString()!.TrimStart('0').PadLeft(3, '0'),
            JsonValueKind.Number => ReadNumericManifestMode(mode.GetInt32()),
            _ => throw new InvalidDataException("Manifest mode must be a string or number."),
        };
    }

    private static string ReadNumericManifestMode(int mode)
    {
        Assert.InRange(mode, 0, 777);
        return mode <= 511
            ? Convert.ToString(mode, 8).PadLeft(3, '0')
            : mode.ToString("000", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string ToOctalMode(UnixFileMode mode)
    {
        var numericMode = Convert.ToString((int)mode, 8);
        return numericMode.TrimStart('0').PadLeft(3, '0');
    }

    private static void AssertArchivePathIsSafe(string path, string packageName)
    {
        Assert.False(string.IsNullOrWhiteSpace(path));
        Assert.False(path.StartsWith('/'), $"Archive contains an absolute path: {path}");
        Assert.False(DriveRootRegex().IsMatch(path), $"Archive contains a drive-rooted path: {path}");
        Assert.DoesNotContain(path.Split('/'), segment => segment is "." or "..");
        Assert.True(
            path.Equals(packageName, StringComparison.Ordinal) ||
            path.StartsWith($"{packageName}/", StringComparison.Ordinal),
            $"Archive entry escapes the frozen package root: {path}");
    }

    private static bool IsRegularFile(TarEntryType type) =>
        type is TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile;

    private static void AssertExecutable(ArchiveFileRecord file, string path)
    {
        Assert.NotEqual(UnixFileMode.None, file.Mode & UnixFileMode.UserExecute);
        Assert.Equal(
            UnixFileMode.None,
            file.Mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite));
        Assert.True(file.Length > 0, $"Executable is empty: {path}");
    }

    private static async Task AssertScriptSucceededAsync(
        string scriptPath,
        IReadOnlyList<string> arguments,
        TimeSpan timeout)
    {
        var result = await PowerShellProcess.RunAsync(scriptPath, arguments, timeout);
        Assert.True(result.ExitCode == 0,
            $"{scriptPath} failed with exit code {result.ExitCode}.{Environment.NewLine}{result.CombinedOutput}");
    }

    private static void CorruptArchive(string archivePath)
    {
        using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(stream.Length > 16, "Archive is unexpectedly too small to corrupt.");
        stream.Position = stream.Length / 2;
        var original = stream.ReadByte();
        Assert.NotEqual(-1, original);
        stream.Position--;
        stream.WriteByte((byte)(original ^ 0xff));
        stream.Flush(flushToDisk: true);
    }

    [GeneratedRegex(@"^[A-Za-z]:[/\\]")]
    private static partial Regex DriveRootRegex();

    [GeneratedRegex(
        @"(?:^|/)(?:uploads?|logs?|obj|bin|\.git)(?:/|$)|(?:^|/)appsettings\.Development\.json$|\.(?:pdb|cs|csproj|sln|user|db|db-wal|db-shm|tmp|bak)$|(?:^|/)(?:relaycove\.env|\.env)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex ForbiddenPathRegex();
}

internal sealed record ArchiveFileRecord(
    long Length,
    string Sha256,
    UnixFileMode Mode,
    string? TextContent);

internal sealed record ReleasePackageInspection(
    string ArchivePath,
    string ArchiveSha256,
    string SidecarText,
    string ManifestText);
