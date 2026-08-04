using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RelayCove.Client.Tests.Packaging;

public sealed partial class ClientReleasePackageTests
{
    private const string RuntimeIdentifier = "win-x64";
    private static readonly TimeSpan PublishTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan VerifyTimeout = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task PublishAndVerify_WhenBuiltTwice_ProducesByteIdenticalSafeClientZip()
    {
        var version = $"0.0.0-client-packaging-{Guid.NewGuid():N}";
        using var firstOutput = new TemporaryArtifactDirectory("first");
        using var secondOutput = new TemporaryArtifactDirectory("second");

        await AssertScriptSucceededAsync(
            "scripts/publish-client.ps1",
            ["-Version", version, "-OutputRoot", firstOutput.Path, "-AllowDirty"],
            PublishTimeout);
        await AssertScriptSucceededAsync(
            "scripts/publish-client.ps1",
            ["-Version", version, "-OutputRoot", secondOutput.Path, "-AllowDirty"],
            PublishTimeout);

        var first = InspectPackage(firstOutput.Path, version);
        var second = InspectPackage(secondOutput.Path, version);
        Assert.Equal(first.ArchiveSha256, second.ArchiveSha256);
        Assert.Equal(first.Sidecar, second.Sidecar);
        Assert.Equal(first.Manifest, second.Manifest);

        await AssertScriptSucceededAsync(
            "scripts/verify-client-release.ps1",
            [
                "-Version", version,
                "-OutputRoot", firstOutput.Path,
                "-CompareOutputRoot", secondOutput.Path,
                "-AllowDirtySource",
            ],
            VerifyTimeout);

        var originalSidecar = await File.ReadAllTextAsync(first.SidecarPath);
        await File.WriteAllTextAsync(first.SidecarPath, new string('0', 64) + "  invalid.zip\n");
        await AssertScriptFailsAsync(
            "scripts/verify-client-release.ps1",
            ["-Version", version, "-OutputRoot", firstOutput.Path, "-AllowDirtySource"],
            VerifyTimeout);
        await File.WriteAllTextAsync(first.SidecarPath, originalSidecar);

        CorruptArchive(first.ArchivePath);
        await AssertScriptFailsAsync(
            "scripts/verify-client-release.ps1",
            ["-Version", version, "-OutputRoot", firstOutput.Path, "-AllowDirtySource"],
            VerifyTimeout);
    }

    private static ClientReleaseInspection InspectPackage(string outputRoot, string version)
    {
        var packageName = $"RelayCove.Client-{version}-{RuntimeIdentifier}";
        var container = Path.Combine(outputRoot, "client", version);
        var archivePath = Path.Combine(container, $"{packageName}.zip");
        var sidecarPath = $"{archivePath}.sha256";
        Assert.True(File.Exists(archivePath), $"Client release ZIP is missing: {archivePath}");
        Assert.True(File.Exists(sidecarPath), $"Client release ZIP sidecar is missing: {sidecarPath}");

        var archiveSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archivePath))).ToLowerInvariant();
        var sidecar = File.ReadAllText(sidecarPath).TrimEnd('\r', '\n');
        Assert.Equal($"{archiveSha256}  {Path.GetFileName(archivePath)}", sidecar);

        using var archive = ZipFile.OpenRead(archivePath);
        var entries = archive.Entries.ToArray();
        Assert.NotEmpty(entries);
        Assert.Equal(
            entries.Select(entry => entry.FullName).OrderBy(name => name, StringComparer.Ordinal),
            entries.Select(entry => entry.FullName));

        var entriesByPath = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            Assert.True(entriesByPath.TryAdd(entry.FullName, entry), $"Duplicate ZIP entry: {entry.FullName}");
            Assert.False(string.IsNullOrWhiteSpace(entry.FullName));
            Assert.False(entry.FullName.Contains('\\'));
            Assert.False(Path.IsPathFullyQualified(entry.FullName));
            Assert.All(entry.FullName.Split('/'), segment => Assert.NotEqual("..", segment));
            Assert.True(
                entry.FullName.Equals($"{packageName}/", StringComparison.Ordinal) ||
                entry.FullName.StartsWith($"{packageName}/", StringComparison.Ordinal),
                $"ZIP entry escapes the package root: {entry.FullName}");
            Assert.Equal(new DateTime(1980, 1, 1, 0, 0, 0), entry.LastWriteTime.DateTime);
            if (!entry.FullName.EndsWith("/", StringComparison.Ordinal))
            {
                Assert.Equal(0x00000080, entry.ExternalAttributes);
                Assert.DoesNotMatch(ForbiddenPath(), entry.FullName);
            }
        }

        foreach (var requiredPath in RequiredPaths(packageName))
        {
            Assert.True(entriesByPath.ContainsKey(requiredPath), $"ZIP is missing required entry: {requiredPath}");
        }

        var executable = ReadEntry(entriesByPath[$"{packageName}/RelayCove.Client.exe"]);
        AssertWindowsX64Pe(executable);
        AssertSelfContainedRuntimeConfig(
            ReadEntryText(entriesByPath[$"{packageName}/RelayCove.Client.runtimeconfig.json"]));

        var manifestText = ReadEntryText(entriesByPath[$"{packageName}/manifest.json"]);
        AssertManifest(manifestText, entriesByPath, packageName, version);
        return new ClientReleaseInspection(archivePath, sidecarPath, archiveSha256, sidecar, manifestText);
    }

    private static IEnumerable<string> RequiredPaths(string packageName)
    {
        foreach (var path in new[]
                 {
                     "RelayCove.Client.exe",
                     "RelayCove.Client.dll",
                     "RelayCove.Client.deps.json",
                     "RelayCove.Client.runtimeconfig.json",
                     "hostfxr.dll",
                     "hostpolicy.dll",
                     "coreclr.dll",
                     "Microsoft.WindowsAppRuntime.Bootstrap.dll",
                     "Microsoft.WindowsAppRuntime.dll",
                     "Microsoft.UI.Xaml.Controls.dll",
                     "Microsoft.ui.xaml.dll",
                     "Microsoft.Windows.ApplicationModel.WindowsAppRuntime.Projection.dll",
                     "WinRT.Runtime.dll",
                     "e_sqlite3.dll",
                     "manifest.json",
                 })
        {
            yield return $"{packageName}/{path}";
        }
    }

    private static void AssertManifest(
        string manifestText,
        IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        string packageName,
        string version)
    {
        using var document = JsonDocument.Parse(manifestText);
        var root = document.RootElement;
        Assert.Equal(1, GetProperty(root, "schemaVersion").GetInt32());
        Assert.Equal(version, GetProperty(root, "version").GetString());
        Assert.Equal(RuntimeIdentifier, GetProperty(root, "rid").GetString());
        Assert.True(GetProperty(root, "selfContained").GetBoolean());
        Assert.True(GetProperty(root, "windowsAppSdkSelfContained").GetBoolean());
        Assert.Equal(packageName, GetProperty(root, "packageRoot").GetString());
        Assert.Matches("^[0-9a-f]{40}$", GetProperty(root, "commit").GetString() ?? string.Empty);
        Assert.False(string.IsNullOrWhiteSpace(GetProperty(root, "sdkVersion").GetString()));

        var files = GetProperty(root, "files").EnumerateArray().ToArray();
        Assert.Equal(entries.Count - 1, files.Length);
        var paths = files.Select(file => GetProperty(file, "path").GetString()!).ToArray();
        Assert.Equal(paths.OrderBy(path => path, StringComparer.Ordinal), paths);
        Assert.Equal(paths.Length, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        foreach (var file in files)
        {
            var path = GetProperty(file, "path").GetString();
            Assert.NotNull(path);
            Assert.True(entries.TryGetValue($"{packageName}/{path}", out var entry),
                $"Manifest lists an absent ZIP entry: {path}");
            var bytes = ReadEntry(entry!);
            Assert.Equal(entry!.Length, GetProperty(file, "length").GetInt64());
            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                GetProperty(file, "sha256").GetString());
            Assert.Equal("00000080", GetProperty(file, "attributes").GetString());
        }
    }

    private static JsonElement GetProperty(JsonElement element, string name)
    {
        Assert.True(element.TryGetProperty(name, out var property), $"JSON property is missing: {name}");
        return property;
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static string ReadEntryText(ZipArchiveEntry entry) =>
        System.Text.Encoding.UTF8.GetString(ReadEntry(entry));

    private static void AssertWindowsX64Pe(byte[] content)
    {
        Assert.True(content.Length >= 512, "Client entry is too short for a PE header.");
        Assert.Equal((byte)'M', content[0]);
        Assert.Equal((byte)'Z', content[1]);
        var peOffset = BitConverter.ToInt32(content, 60);
        Assert.InRange(peOffset, 64, content.Length - 26);
        Assert.Equal((byte)'P', content[peOffset]);
        Assert.Equal((byte)'E', content[peOffset + 1]);
        Assert.Equal((ushort)0x8664, BitConverter.ToUInt16(content, peOffset + 4));
        Assert.Equal((ushort)0x020b, BitConverter.ToUInt16(content, peOffset + 24));
    }

    private static void AssertSelfContainedRuntimeConfig(string runtimeConfigText)
    {
        using var document = JsonDocument.Parse(runtimeConfigText);
        var runtimeOptions = GetProperty(document.RootElement, "runtimeOptions");
        Assert.False(runtimeOptions.TryGetProperty("framework", out _));
        Assert.False(runtimeOptions.TryGetProperty("frameworks", out _));

        var frameworkNames = GetProperty(runtimeOptions, "includedFrameworks")
            .EnumerateArray()
            .Select(framework => GetProperty(framework, "name").GetString())
            .ToArray();
        Assert.Contains("Microsoft.NETCore.App", frameworkNames);
        Assert.Contains("Microsoft.WindowsDesktop.App", frameworkNames);
    }

    private static async Task AssertScriptSucceededAsync(
        string path,
        IReadOnlyList<string> arguments,
        TimeSpan timeout)
    {
        var result = await PowerShellProcess.RunAsync(path, arguments, timeout);
        Assert.True(result.ExitCode == 0, $"{path} failed:{Environment.NewLine}{result.CombinedOutput}");
    }

    private static async Task AssertScriptFailsAsync(
        string path,
        IReadOnlyList<string> arguments,
        TimeSpan timeout)
    {
        var result = await PowerShellProcess.RunAsync(path, arguments, timeout);
        Assert.NotEqual(0, result.ExitCode);
    }

    private static void CorruptArchive(string archivePath)
    {
        using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        stream.Position = stream.Length / 2;
        var value = stream.ReadByte();
        Assert.NotEqual(-1, value);
        stream.Position--;
        stream.WriteByte((byte)(value ^ 0xff));
    }

    [GeneratedRegex(@"(?:^|/)(?:bin|obj|data|uploads|logs|cache|temp)(?:/|$)|\.(?:pdb|cs|csproj|sln|user|db|sqlite|pfx|p12|pem|key|bak|tmp)$|(?:^|/)\.env(?:\.|$)|(?:^|/)[^/]*secret[^/]*\.json$|(?:^|/)[^/]*(?:credential|refresh[-_.]?token|access[-_.]?token)[^/]*\.(?:bin|json|dat)$", RegexOptions.IgnoreCase)]
    private static partial Regex ForbiddenPath();
}

internal sealed record ClientReleaseInspection(
    string ArchivePath,
    string SidecarPath,
    string ArchiveSha256,
    string Sidecar,
    string Manifest);
