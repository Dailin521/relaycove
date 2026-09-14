using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace RelayCove.App.Tests;

public sealed class InstallerPackagingTests
{
    private static readonly string AppAssemblyPath = typeof(RelayCove.App.ViewModels.AppUpdateViewModel).Assembly.Location;
    private static readonly FileVersionInfo AppVersion = FileVersionInfo.GetVersionInfo(AppAssemblyPath);
    private static string PackageVersion => $"{AppVersion.FileMajorPart}.{AppVersion.FileMinorPart}.{AppVersion.FileBuildPart}";
    [Theory]
    [InlineData("recover", true, 3)]
    [InlineData("denied", false, 3)]
    [InlineData("syntax", false, 1)]
    [InlineData("missing", false, 1)]
    [InlineData("success", true, 1)]
    public async Task PackageInstaller_WhenCompilerCompletesOrFails_PublishesOnlySuccessfulOutput(
        string mode, bool succeeds, int attempts)
    {
        var fixture = CreateFixture(mode);
        var result = await RunAsync(fixture);
        Assert.Equal(succeeds, result == 0);
        Assert.Equal(attempts, int.Parse(File.ReadAllText(Path.Combine(fixture, "calls.txt"))));
        var outputs = File.ReadAllLines(Path.Combine(fixture, "outputs.txt"));
        Assert.Equal(attempts, outputs.Distinct().Count());
        Assert.All(outputs, path => Assert.DoesNotContain("payload", path));
        var installer = InstallerPath(fixture);
        Assert.Equal(succeeds ? "new installer" : "old installer", File.ReadAllText(installer));
        Assert.Equal(Hash(installer) + "  " + Path.GetFileName(installer), File.ReadAllText(ManifestPath(fixture)).Trim());
        Assert.Equal("unrelated", File.ReadAllText(Path.Combine(fixture, "artifacts", "package", "unrelated.txt")));
        var updatePath = Path.Combine(fixture, "artifacts", "package", "update-win-x64.json");
        Assert.Equal(succeeds, File.Exists(updatePath));
        if (succeeds)
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(updatePath));
            var update = manifest.RootElement;
            Assert.Equal(1, update.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("com.relaycove.client", update.GetProperty("productId").GetString());
            Assert.Equal("win-x64", update.GetProperty("platform").GetString());
            Assert.Equal(PackageVersion, update.GetProperty("version").GetString());
            Assert.Equal(AppVersion.FilePrivatePart, update.GetProperty("buildNumber").GetInt32());
            Assert.Equal(Path.GetFileName(installer), update.GetProperty("installerName").GetString());
            Assert.Equal(new FileInfo(installer).Length, update.GetProperty("size").GetInt64());
            Assert.Equal(Hash(installer).ToLowerInvariant(), update.GetProperty("sha256").GetString());
        }
    }

    [Fact]
    public async Task PackageInstaller_WhenManifestCannotBeReplaced_RestoresPreviousInstaller()
    {
        var fixture = CreateFixture("success");
        // Allow the pre-publish backup read, while denying rename/replacement.
        using var manifestLock = new FileStream(ManifestPath(fixture), FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.NotEqual(0, await RunAsync(fixture));
        Assert.Equal("old installer", File.ReadAllText(InstallerPath(fixture)));
        Assert.Equal(Hash(InstallerPath(fixture)) + "  " + Path.GetFileName(InstallerPath(fixture)),
            File.ReadAllText(ManifestPath(fixture)).Trim());
    }

    [Fact]
    public async Task PackageInstaller_WhenUpdateManifestCannotBeReplaced_RestoresPreviousInstallerAndChecksum()
    {
        var fixture = CreateFixture("success");
        var updatePath = Path.Combine(fixture, "artifacts", "package", "update-win-x64.json");
        await File.WriteAllTextAsync(updatePath, "previous update metadata");
        using var updateLock = new FileStream(updatePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.NotEqual(0, await RunAsync(fixture));
        Assert.Equal("old installer", File.ReadAllText(InstallerPath(fixture)));
        Assert.Equal(Hash(InstallerPath(fixture)) + "  " + Path.GetFileName(InstallerPath(fixture)),
            File.ReadAllText(ManifestPath(fixture)).Trim());
        Assert.Equal("previous update metadata", File.ReadAllText(updatePath));
    }

    [Fact]
    public async Task PackageInstaller_WhenProjectBuildChangesButZipDoesNot_RejectsBeforeCompiler()
    {
        var fixture = CreateFixture("success");
        var projectPath = Path.Combine(fixture, "src", "RelayCove.App", "RelayCove.App.csproj");
        var project = System.Xml.Linq.XDocument.Load(projectPath);
        project.Descendants("ApplicationVersion").Single().Value = (AppVersion.FilePrivatePart + 1).ToString();
        project.Save(projectPath);

        Assert.NotEqual(0, await RunAsync(fixture));
        Assert.False(File.Exists(Path.Combine(fixture, "calls.txt")));
        Assert.Equal("old installer", File.ReadAllText(InstallerPath(fixture)));
        Assert.False(File.Exists(Path.Combine(fixture, "artifacts", "package", "update-win-x64.json")));
    }

    private static string CreateFixture(string mode)
    {
        var root = FindWorkspaceRoot();
        var fixture = Path.Combine(root, ".verify", "installer-script-tests", Guid.NewGuid().ToString("N"), "中文 空格");
        Directory.CreateDirectory(Path.Combine(fixture, "scripts"));
        Directory.CreateDirectory(Path.Combine(fixture, "src", "RelayCove.App"));
        Directory.CreateDirectory(Path.Combine(fixture, "artifacts", "package"));
        File.Copy(Path.Combine(root, "scripts", "package-installer.ps1"), Path.Combine(fixture, "scripts", "package-installer.ps1"));
        File.Copy(Path.Combine(root, "scripts", "write-update-manifest.ps1"), Path.Combine(fixture, "scripts", "write-update-manifest.ps1"));
        File.WriteAllText(Path.Combine(fixture, "src", "RelayCove.App", "RelayCove.App.csproj"),
            $"<Project><PropertyGroup><ApplicationDisplayVersion>{PackageVersion}</ApplicationDisplayVersion><ApplicationVersion>{AppVersion.FilePrivatePart}</ApplicationVersion></PropertyGroup></Project>");
        var archivePath = Path.Combine(fixture, "artifacts", "package", $"RichChat-{PackageVersion}-win-x64.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            foreach (var name in new[] { "RichChat.exe", "Assets/RichChat-R.ico", "coreclr.dll", "Microsoft.UI.Xaml.dll", "e_sqlite3.dll", "LICENSE", "THIRD-PARTY-NOTICES.md" })
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                writer.Write("inert fixture");
            }
            archive.CreateEntryFromFile(AppAssemblyPath, "RichChat.dll");
        }
        File.WriteAllText(Path.ChangeExtension(archivePath, ".sha256"), Hash(archivePath) + "  " + Path.GetFileName(archivePath));
        File.WriteAllText(InstallerPath(fixture), "old installer");
        File.WriteAllText(ManifestPath(fixture), Hash(InstallerPath(fixture)) + "  " + Path.GetFileName(InstallerPath(fixture)));
        File.WriteAllText(Path.Combine(fixture, "artifacts", "package", "unrelated.txt"), "unrelated");
        File.WriteAllText(Path.Combine(fixture, "mode.txt"), mode);
        File.WriteAllText(Path.Combine(fixture, "compiler.ps1"), """
            $ErrorActionPreference = 'Stop'
            $callsPath = Join-Path $PSScriptRoot 'calls.txt'
            $calls = if (Test-Path -LiteralPath $callsPath) { [int][IO.File]::ReadAllText($callsPath) + 1 } else { 1 }
            [IO.File]::WriteAllText($callsPath, [string]$calls)
            $outputRoot = ($args | Where-Object { $_.StartsWith('/DOutputRoot=') }).Substring(13)
            $payload = ($args | Where-Object { $_.StartsWith('/DPublishRoot=') }).Substring(14)
            $filename = ($args | Where-Object { $_.StartsWith('/F') }).Substring(2)
            if ($filename -notmatch '^installer-[a-f0-9]{32}$') { throw 'Compile output must use a temporary name.' }
            if (@(Get-ChildItem -LiteralPath $payload -Recurse -File).Count -ne 8) { throw 'Payload is contaminated.' }
            Add-Content -LiteralPath (Join-Path $PSScriptRoot 'outputs.txt') -Value $outputRoot
            $mode = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'mode.txt'))
            if ($mode -eq 'denied' -or ($mode -eq 'recover' -and $calls -lt 3)) {
                [IO.File]::WriteAllText((Join-Path $outputRoot ($filename + '.exe')), 'incomplete')
                Write-Output 'Resource update error: EndUpdateResource failed (5)'
                $global:LASTEXITCODE = 2
                return
            }
            if ($mode -eq 'syntax') {
                Write-Output 'Unknown directive; compile aborted.'
                $global:LASTEXITCODE = 2
                return
            }
            if ($mode -ne 'missing') { [IO.File]::WriteAllText((Join-Path $outputRoot ($filename + '.exe')), 'new installer') }
            $global:LASTEXITCODE = 0
            """);
        return fixture;
    }

    private static async Task<int> RunAsync(string fixture)
    {
        var start = new ProcessStartInfo("pwsh.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                     Path.Combine(fixture, "scripts", "package-installer.ps1"), "-IsccPath", Path.Combine(fixture, "compiler.ps1") })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        await File.WriteAllTextAsync(Path.Combine(fixture, "run.log"), await stdout + await stderr);
        return process.ExitCode;
    }

    private static string InstallerPath(string fixture) => Path.Combine(fixture, "artifacts", "package", $"RichChat-{PackageVersion}-win-x64-Setup.exe");
    private static string ManifestPath(string fixture) => Path.ChangeExtension(InstallerPath(fixture), ".sha256");
    private static string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));

    private static string FindWorkspaceRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "RelayCove.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("Workspace root was not found.");
    }
}
