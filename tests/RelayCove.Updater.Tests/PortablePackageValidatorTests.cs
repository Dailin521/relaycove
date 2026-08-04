using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RelayCove.Updater.Tests;

public sealed class PortablePackageValidatorTests
{
    [Fact]
    public void ValidateAndExtract_WhenPackageIsValid_ExtractsRequiredFiles()
    {
        using var temporary = new TemporaryDirectory();
        var archive = PackageFixture.Create(temporary.Path);
        var options = PackageFixture.CreateOptions(archive.Path, archive.Hash, archive.Size);
        var staging = Path.Combine(temporary.Path, "staging");

        new PortablePackageValidator().ValidateAndExtract(options, staging);

        Assert.True(File.Exists(Path.Combine(staging, "RelayCove.Client.exe")));
        Assert.True(File.Exists(Path.Combine(staging, "RelayCove.Updater.exe")));
    }

    [Fact]
    public void ValidateAndExtract_WhenArchiveHasZipSlip_RejectsBeforeExtraction()
    {
        using var temporary = new TemporaryDirectory();
        var archive = PackageFixture.Create(temporary.Path, additionalEntry: "../escape.txt");
        var options = PackageFixture.CreateOptions(archive.Path, archive.Hash, archive.Size);

        Assert.Throws<InvalidDataException>(() => new PortablePackageValidator().ValidateAndExtract(options, Path.Combine(temporary.Path, "staging")));
    }

    [Fact]
    public void ValidateAndExtract_WhenManifestHashIsWrong_Rejects()
    {
        using var temporary = new TemporaryDirectory();
        var archive = PackageFixture.Create(temporary.Path, wrongManifestHash: true);
        var options = PackageFixture.CreateOptions(archive.Path, archive.Hash, archive.Size);

        Assert.Throws<InvalidDataException>(() => new PortablePackageValidator().ValidateAndExtract(options, Path.Combine(temporary.Path, "staging")));
    }

    [Fact]
    public void ValidateAndExtract_WhenExpectedHashIsWrong_Rejects()
    {
        using var temporary = new TemporaryDirectory();
        var archive = PackageFixture.Create(temporary.Path);
        var options = PackageFixture.CreateOptions(archive.Path, new string('b', 64), archive.Size);

        Assert.Throws<InvalidDataException>(() => new PortablePackageValidator().ValidateAndExtract(options, Path.Combine(temporary.Path, "staging")));
    }
}

internal static class PackageFixture
{
    internal static (string Path, string Hash, long Size) Create(string root, string? additionalEntry = null, bool wrongManifestHash = false)
    {
        var archivePath = System.IO.Path.Combine(root, "release.zip");
        const string packageRoot = "RelayCove.Client-1.0.1-rc.1-win-x64";
        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["RelayCove.Client.exe"] = Encoding.UTF8.GetBytes("client"),
            ["RelayCove.Updater.exe"] = Encoding.UTF8.GetBytes("updater"),
        };
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            foreach (var pair in entries)
            {
                var entry = archive.CreateEntry($"{packageRoot}/{pair.Key}");
                entry.ExternalAttributes = 0x00000080;
                using var stream = entry.Open();
                stream.Write(pair.Value);
            }

            if (additionalEntry is not null)
            {
                var entry = archive.CreateEntry($"{packageRoot}/{additionalEntry}");
                entry.ExternalAttributes = 0x00000080;
                using var stream = entry.Open();
                stream.WriteByte(1);
            }

            var files = entries.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new
            {
                path = pair.Key,
                length = pair.Value.LongLength,
                sha256 = wrongManifestHash && pair.Key == "RelayCove.Client.exe" ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(pair.Value)).ToLowerInvariant(),
                attributes = "00000080",
            });
            var manifest = JsonSerializer.Serialize(new
            {
                schemaVersion = 1, version = "1.0.1-rc.1", rid = "win-x64", packageRoot,
                sourceTreeClean = true, selfContained = true, windowsAppSdkSelfContained = true, files,
            });
            var manifestEntry = archive.CreateEntry($"{packageRoot}/manifest.json");
            manifestEntry.ExternalAttributes = 0x00000080;
            using var manifestStream = new StreamWriter(manifestEntry.Open(), new UTF8Encoding(false));
            manifestStream.Write(manifest);
        }

        return (archivePath, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archivePath))).ToLowerInvariant(), new FileInfo(archivePath).Length);
    }

    internal static UpdaterOptions CreateOptions(string path, string hash, long size) => new()
    {
        ArchivePath = path, ExpectedSha256 = hash, ExpectedSize = size,
        ExpectedVersion = RelayCove.Shared.Updates.SemanticVersion.Parse("1.0.1-rc.1"),
        CurrentVersion = RelayCove.Shared.Updates.SemanticVersion.Parse("1.0.0"),
        TargetPath = Path.Combine(Path.GetTempPath(), "relaycove-target"), WaitProcessId = 1,
        WaitProcessStartTimeUtcTicks = 1, WaitTimeoutSeconds = 1, Bootstrapped = true,
    };
}

internal sealed class TemporaryDirectory : IDisposable
{
    internal TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"relaycove-updater-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    internal string Path { get; }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, true);
        }
    }
}
