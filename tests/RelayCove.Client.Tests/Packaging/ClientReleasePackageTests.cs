using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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

        await AssertStandaloneUpdaterAsync(firstOutput.Path, first.ArchivePath, version);

        var originalSidecar = await File.ReadAllTextAsync(first.SidecarPath);
        var archiveBackupPath = Path.Combine(firstOutput.Path, $"archive-backup-{Guid.NewGuid():N}.zip");
        File.Copy(first.ArchivePath, archiveBackupPath);
        try
        {
            await File.WriteAllTextAsync(first.SidecarPath, new string('0', 64) + "  invalid.zip\n");
            await AssertScriptFailsAsync(
                "scripts/verify-client-release.ps1",
                ["-Version", version, "-OutputRoot", firstOutput.Path, "-AllowDirtySource"],
                VerifyTimeout);
            await File.WriteAllTextAsync(first.SidecarPath, originalSidecar);

            RemoveArchiveEntry(first.ArchivePath, $"RelayCove.Client-{version}-{RuntimeIdentifier}/RelayCove.Updater.exe");
            await WriteArchiveSidecarAsync(first.ArchivePath, first.SidecarPath);
            var missingUpdater = await PowerShellProcess.RunAsync(
                "scripts/verify-client-release.ps1",
                ["-Version", version, "-OutputRoot", firstOutput.Path, "-AllowDirtySource"],
                VerifyTimeout);
            Assert.NotEqual(0, missingUpdater.ExitCode);
            Assert.Contains("RelayCove.Updater.exe", missingUpdater.CombinedOutput, StringComparison.Ordinal);
            RestoreArchive(archiveBackupPath, first.ArchivePath, first.SidecarPath, originalSidecar);

            await AddUpdaterCompanionAsync(first.ArchivePath, packageName: $"RelayCove.Client-{version}-{RuntimeIdentifier}");
            await WriteArchiveSidecarAsync(first.ArchivePath, first.SidecarPath);
            var companionUpdater = await PowerShellProcess.RunAsync(
                "scripts/verify-client-release.ps1",
                ["-Version", version, "-OutputRoot", firstOutput.Path, "-AllowDirtySource"],
                VerifyTimeout);
            Assert.NotEqual(0, companionUpdater.ExitCode);
            Assert.Contains("forbidden updater companion", companionUpdater.CombinedOutput, StringComparison.OrdinalIgnoreCase);
            RestoreArchive(archiveBackupPath, first.ArchivePath, first.SidecarPath, originalSidecar);

            CorruptArchive(first.ArchivePath);
            await AssertScriptFailsAsync(
                "scripts/verify-client-release.ps1",
                ["-Version", version, "-OutputRoot", firstOutput.Path, "-AllowDirtySource"],
                VerifyTimeout);
        }
        finally
        {
            File.Delete(archiveBackupPath);
        }
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
        var updater = ReadEntry(entriesByPath[$"{packageName}/RelayCove.Updater.exe"]);
        Assert.InRange(updater.Length, 1024 * 1024, 1024 * 1024 * 1024);
        AssertWindowsX64Pe(updater);
        Assert.Equal(
            [$"{packageName}/RelayCove.Updater.exe"],
            entries.Where(entry => entry.FullName.StartsWith($"{packageName}/RelayCove.Updater.", StringComparison.Ordinal))
                .Select(entry => entry.FullName));
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
                     "RelayCove.Updater.exe",
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

    private static async Task AssertStandaloneUpdaterAsync(string outputRoot, string archivePath, string version)
    {
        var packageName = $"RelayCove.Client-{version}-{RuntimeIdentifier}";
        var extractionRoot = Path.Combine(outputRoot, "updater-smoke");
        ZipFile.ExtractToDirectory(archivePath, extractionRoot);
        var packageRoot = Path.Combine(extractionRoot, packageName);
        var updaterPath = Path.Combine(packageRoot, "RelayCove.Updater.exe");

        var help = await RunExecutableAsync(updaterPath, ["--help"], TimeSpan.FromMinutes(1), packageRoot);
        Assert.Equal(0, help.ExitCode);
        Assert.Contains("RelayCove Updater", help.CombinedOutput, StringComparison.Ordinal);

        var expectedHash = new string('a', 64);
        var invalid = await RunExecutableAsync(
            updaterPath,
            ["apply", "--expected-sha256", expectedHash],
            TimeSpan.FromMinutes(1),
            packageRoot);
        Assert.NotEqual(0, invalid.ExitCode);
        Assert.DoesNotContain(packageRoot, invalid.CombinedOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(expectedHash, invalid.CombinedOutput, StringComparison.Ordinal);
    }

    private static async Task<PackagingProcessResult> RunExecutableAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        using var cancellation = new CancellationTokenSource(timeout);
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellation.Token);
        var standardError = process.StandardError.ReadToEndAsync(cancellation.Token);
        try
        {
            await process.WaitForExitAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException($"Executable exceeded {timeout}: {executablePath}");
        }

        return new PackagingProcessResult(process.ExitCode, await standardOutput, await standardError);
    }

    private static void RemoveArchiveEntry(string archivePath, string entryName)
    {
        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Update);
        var entry = archive.GetEntry(entryName);
        Assert.NotNull(entry);
        entry.Delete();
    }

    private static async Task WriteArchiveSidecarAsync(string archivePath, string sidecarPath)
    {
        await using var archiveStream = new FileStream(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(archiveStream)).ToLowerInvariant();
        await File.WriteAllTextAsync(sidecarPath, $"{hash}  {Path.GetFileName(archivePath)}{Environment.NewLine}");
    }

    private static void RestoreArchive(
        string backupPath,
        string archivePath,
        string sidecarPath,
        string originalSidecar)
    {
        File.Copy(backupPath, archivePath, overwrite: true);
        File.WriteAllText(sidecarPath, originalSidecar);
    }

    private static async Task AddUpdaterCompanionAsync(string archivePath, string packageName)
    {
        var companionPath = $"{packageName}/RelayCove.Updater.dll";
        var manifestPath = $"{packageName}/manifest.json";
        var companionContent = Encoding.UTF8.GetBytes("not a standalone updater");
        var temporaryArchivePath = $"{archivePath}.mutation-{Guid.NewGuid():N}.tmp";

        try
        {
            using (var sourceArchive = ZipFile.OpenRead(archivePath))
            {
                var manifestEntry = sourceArchive.GetEntry(manifestPath);
                Assert.NotNull(manifestEntry);
                JsonObject manifest;
                await using (var manifestStream = manifestEntry.Open())
                {
                    manifest = Assert.IsType<JsonObject>(await JsonNode.ParseAsync(manifestStream));
                }

                var files = Assert.IsType<JsonArray>(manifest["files"]);
                files.Add(new JsonObject
                {
                    ["path"] = "RelayCove.Updater.dll",
                    ["length"] = companionContent.LongLength,
                    ["sha256"] = Convert.ToHexString(SHA256.HashData(companionContent)).ToLowerInvariant(),
                    ["attributes"] = "00000080",
                });
                manifest["files"] = new JsonArray(
                    files.Select(file => file!.DeepClone())
                        .OrderBy(
                            file => file!["path"]!.GetValue<string>(),
                            StringComparer.Ordinal)
                        .ToArray());
                var manifestContent = Encoding.UTF8.GetBytes(
                    manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) +
                    Environment.NewLine);

                await using var targetStream = new FileStream(
                    temporaryArchivePath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 81920,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                using var targetArchive = new ZipArchive(
                    targetStream,
                    ZipArchiveMode.Create,
                    leaveOpen: true,
                    Encoding.UTF8);
                foreach (var entryName in sourceArchive.Entries.Select(entry => entry.FullName)
                             .Append(companionPath)
                             .OrderBy(name => name, StringComparer.Ordinal))
                {
                    var targetEntry = targetArchive.CreateEntry(entryName, CompressionLevel.Optimal);
                    targetEntry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                    targetEntry.ExternalAttributes = 0x00000080;
                    await using var targetEntryStream = targetEntry.Open();
                    if (entryName == manifestPath)
                    {
                        await targetEntryStream.WriteAsync(manifestContent);
                    }
                    else if (entryName == companionPath)
                    {
                        await targetEntryStream.WriteAsync(companionContent);
                    }
                    else
                    {
                        var sourceEntry = sourceArchive.GetEntry(entryName);
                        Assert.NotNull(sourceEntry);
                        await using var sourceEntryStream = sourceEntry.Open();
                        await sourceEntryStream.CopyToAsync(targetEntryStream);
                    }
                }
            }

            File.Move(temporaryArchivePath, archivePath, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryArchivePath);
        }
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
