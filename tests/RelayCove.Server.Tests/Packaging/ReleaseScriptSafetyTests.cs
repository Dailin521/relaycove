namespace RelayCove.Server.Tests.Packaging;

public sealed class ReleaseScriptSafetyTests
{
    private static readonly TimeSpan ScriptTimeout = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData("scripts/publish-server.ps1", "-AllowDirty")]
    [InlineData("scripts/verify-server-release.ps1", "-AllowDirtySource")]
    public async Task ReleaseScript_WhenOutputRootIsOutsideArtifacts_RejectsBeforeWriting(
        string scriptPath,
        string dirtySourceSwitch)
    {
        var outsideRoot = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"relaycove-packaging-outside-{Guid.NewGuid():N}");

        var result = await PowerShellProcess.RunAsync(
            scriptPath,
            new[]
            {
                "-Version", "0.0.0-path-safety-test",
                "-OutputRoot", outsideRoot,
                dirtySourceSwitch,
            },
            ScriptTimeout);

        Assert.NotEqual(0, result.ExitCode);
        Assert.False(Directory.Exists(outsideRoot),
            $"Rejected output root was created outside artifacts: {outsideRoot}{Environment.NewLine}{result.CombinedOutput}");
    }

    [Theory]
    [InlineData("scripts/publish-server.ps1", "-AllowDirty")]
    [InlineData("scripts/verify-server-release.ps1", "-AllowDirtySource")]
    public async Task ReleaseScript_WhenVersionTraversesDirectories_RejectsWithoutTouchingSibling(
        string scriptPath,
        string dirtySourceSwitch)
    {
        using var outputRoot = new TemporaryArtifactDirectory("path-safety");
        var sentinelName = $"sentinel-{Guid.NewGuid():N}";
        var sentinelPath = System.IO.Path.Combine(outputRoot.Path, sentinelName);
        Directory.CreateDirectory(sentinelPath);
        var sentinelFile = System.IO.Path.Combine(sentinelPath, "keep.txt");
        await File.WriteAllTextAsync(sentinelFile, "owned by packaging path-safety test");

        var result = await PowerShellProcess.RunAsync(
            scriptPath,
            new[]
            {
                "-Version", $"../{sentinelName}",
                "-OutputRoot", outputRoot.Path,
                dirtySourceSwitch,
            },
            ScriptTimeout);

        Assert.NotEqual(0, result.ExitCode);
        Assert.True(File.Exists(sentinelFile),
            $"Traversal input touched a sibling path.{Environment.NewLine}{result.CombinedOutput}");
        Assert.Equal(
            "owned by packaging path-safety test",
            await File.ReadAllTextAsync(sentinelFile));
    }

    [Theory]
    [InlineData("scripts/publish-server.ps1", "-AllowDirty")]
    [InlineData("scripts/verify-server-release.ps1", "-AllowDirtySource")]
    public async Task ReleaseScript_WhenVersionHasTrailingNewline_RejectsBeforeUse(
        string scriptPath,
        string dirtySourceSwitch)
    {
        using var outputRoot = new TemporaryArtifactDirectory("version-newline");

        var result = await PowerShellProcess.RunAsync(
            scriptPath,
            new[]
            {
                "-Version", "1.0.0-rc.1\n",
                "-OutputRoot", outputRoot.Path,
                dirtySourceSwitch,
            },
            ScriptTimeout);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Version must be", result.CombinedOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("> dotnet", result.CombinedOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Verifier_WhenUsingDefaultOutputRoot_ResolvesArtifactsItself()
    {
        var version = $"0.0.0-missing-{Guid.NewGuid():N}";

        var result = await PowerShellProcess.RunAsync(
            "scripts/verify-server-release.ps1",
            new[] { "-Version", version, "-AllowDirtySource" },
            ScriptTimeout);

        Assert.NotEqual(0, result.ExitCode);
        Assert.DoesNotContain("must remain inside", result.CombinedOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            PackagingTestPaths.GetRepositoryPath("artifacts", "server", version),
            result.CombinedOutput,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Publisher_WhenRepositoryHasUntrackedFileAndCalledExternally_RejectsAsDirty()
    {
        using var outputRoot = new TemporaryArtifactDirectory("dirty-source");
        var probePath = PackagingTestPaths.GetRepositoryPath(
            "tests",
            "RelayCove.Server.Tests",
            "Packaging",
            $".dirty-probe-{Guid.NewGuid():N}");
        var externalWorkingDirectory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"relaycove-packaging-cwd-{Guid.NewGuid():N}");
        Directory.CreateDirectory(externalWorkingDirectory);

        try
        {
            await File.WriteAllTextAsync(probePath, "untracked dirty-source probe");
            var result = await PowerShellProcess.RunAsync(
                "scripts/publish-server.ps1",
                new[]
                {
                    "-Version", "0.0.0-dirty-source-test",
                    "-OutputRoot", outputRoot.Path,
                },
                ScriptTimeout,
                externalWorkingDirectory);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("dirty Git checkout", result.CombinedOutput, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("> dotnet", result.CombinedOutput, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(probePath);
            Directory.Delete(externalWorkingDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData("scripts/publish-server.ps1", "-AllowDirty")]
    [InlineData("scripts/verify-server-release.ps1", "-AllowDirtySource")]
    public async Task ReleaseScript_WhenOutputRootTraversesJunction_RejectsBeforeUse(
        string scriptPath,
        string dirtySourceSwitch)
    {
        using var container = new TemporaryArtifactDirectory("junction");
        var outsideTarget = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"relaycove-packaging-junction-target-{Guid.NewGuid():N}");
        var junctionPath = System.IO.Path.Combine(container.Path, "outside-link");
        Directory.CreateDirectory(outsideTarget);
        var sentinelPath = System.IO.Path.Combine(outsideTarget, "keep.txt");
        await File.WriteAllTextAsync(sentinelPath, "junction target sentinel");

        try
        {
            CreateJunction(junctionPath, outsideTarget);
            var result = await PowerShellProcess.RunAsync(
                scriptPath,
                new[]
                {
                    "-Version", "0.0.0-junction-safety-test",
                    "-OutputRoot", junctionPath,
                    dirtySourceSwitch,
                },
                ScriptTimeout);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains(
                "reparse point",
                result.CombinedOutput,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal("junction target sentinel", await File.ReadAllTextAsync(sentinelPath));
        }
        finally
        {
            if (Directory.Exists(junctionPath))
            {
                Directory.Delete(junctionPath);
            }

            Assert.True(File.Exists(sentinelPath), "Cleaning the junction must not traverse into its target.");
            Directory.Delete(outsideTarget, recursive: true);
        }
    }

    private static void CreateJunction(string junctionPath, string targetPath)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(junctionPath);
        startInfo.ArgumentList.Add(targetPath);

        using var process = System.Diagnostics.Process.Start(startInfo);
        Assert.NotNull(process);
        process.WaitForExit();
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        Assert.True(process.ExitCode == 0, $"Failed to create test junction: {output}");
    }
}
