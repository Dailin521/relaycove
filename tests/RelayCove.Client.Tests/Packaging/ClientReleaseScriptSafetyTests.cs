namespace RelayCove.Client.Tests.Packaging;

public sealed class ClientReleaseScriptSafetyTests
{
    private static readonly TimeSpan ScriptTimeout = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData("scripts/publish-client.ps1", "-AllowDirty")]
    [InlineData("scripts/verify-client-release.ps1", "-AllowDirtySource")]
    public async Task ReleaseScript_WhenOutputRootIsOutsideArtifacts_RejectsBeforeWriting(
        string scriptPath,
        string dirtySwitch)
    {
        var outsideRoot = Path.Combine(Path.GetTempPath(), $"relaycove-client-outside-{Guid.NewGuid():N}");

        var result = await PowerShellProcess.RunAsync(
            scriptPath,
            ["-Version", "0.0.0-client-path-safety", "-OutputRoot", outsideRoot, dirtySwitch],
            ScriptTimeout);

        Assert.NotEqual(0, result.ExitCode);
        Assert.False(Directory.Exists(outsideRoot), $"Rejected output root was created: {outsideRoot}");
    }

    [Theory]
    [InlineData("scripts/publish-client.ps1", "-AllowDirty")]
    [InlineData("scripts/verify-client-release.ps1", "-AllowDirtySource")]
    public async Task ReleaseScript_WhenVersionTraversesDirectories_RejectsWithoutTouchingSibling(
        string scriptPath,
        string dirtySwitch)
    {
        using var outputRoot = new TemporaryArtifactDirectory("traversal");
        var sentinelDirectory = Path.Combine(outputRoot.Path, "sentinel");
        Directory.CreateDirectory(sentinelDirectory);
        var sentinel = Path.Combine(sentinelDirectory, "keep.txt");
        await File.WriteAllTextAsync(sentinel, "client packaging sentinel");

        var result = await PowerShellProcess.RunAsync(
            scriptPath,
            ["-Version", "../sentinel", "-OutputRoot", outputRoot.Path, dirtySwitch],
            ScriptTimeout);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal("client packaging sentinel", await File.ReadAllTextAsync(sentinel));
    }

    [Fact]
    public async Task Publisher_WhenRepositoryIsDirty_RejectsBeforePublish()
    {
        using var outputRoot = new TemporaryArtifactDirectory("dirty");
        var probe = PackagingTestPaths.GetRepositoryPath(
            "tests", "RelayCove.Client.Tests", "Packaging", $".dirty-probe-{Guid.NewGuid():N}");

        try
        {
            await File.WriteAllTextAsync(probe, "untracked dirty-source probe");
            var result = await PowerShellProcess.RunAsync(
                "scripts/publish-client.ps1",
                ["-Version", "0.0.0-client-dirty", "-OutputRoot", outputRoot.Path],
                ScriptTimeout,
                Path.GetTempPath());

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("dirty Git checkout", result.CombinedOutput, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("> dotnet", result.CombinedOutput, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(probe);
        }
    }

    [Theory]
    [InlineData("scripts/publish-client.ps1", "-AllowDirty")]
    [InlineData("scripts/verify-client-release.ps1", "-AllowDirtySource")]
    public async Task ReleaseScript_WhenOutputRootTraversesJunction_RejectsWithoutFollowingLink(
        string scriptPath,
        string dirtySwitch)
    {
        using var container = new TemporaryArtifactDirectory("junction");
        var target = Path.Combine(Path.GetTempPath(), $"relaycove-client-junction-{Guid.NewGuid():N}");
        var junction = Path.Combine(container.Path, "outside-link");
        Directory.CreateDirectory(target);
        var sentinel = Path.Combine(target, "keep.txt");
        await File.WriteAllTextAsync(sentinel, "junction target sentinel");

        try
        {
            CreateJunction(junction, target);
            var result = await PowerShellProcess.RunAsync(
                scriptPath,
                ["-Version", "0.0.0-client-junction", "-OutputRoot", junction, dirtySwitch],
                ScriptTimeout);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("reparse point", result.CombinedOutput, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("junction target sentinel", await File.ReadAllTextAsync(sentinel));
        }
        finally
        {
            if (Directory.Exists(junction))
            {
                Directory.Delete(junction);
            }

            Assert.True(File.Exists(sentinel));
            Directory.Delete(target, recursive: true);
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
