using System.Diagnostics;

namespace RelayCove.Server.Tests.Packaging;

public sealed class ArtifactCleanupSafetyTests
{
    [Fact]
    public async Task TemporaryArtifactDirectory_WhenTargetBecomesJunction_RefusesRecursiveCleanup()
    {
        var ownedDirectory = new TemporaryArtifactDirectory("cleanup-junction");
        var outsideTarget = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"relaycove-cleanup-junction-target-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outsideTarget);
        var sentinelPath = System.IO.Path.Combine(outsideTarget, "keep.txt");
        await File.WriteAllTextAsync(sentinelPath, "cleanup target sentinel");
        Directory.Delete(ownedDirectory.Path);

        try
        {
            CreateJunction(ownedDirectory.Path, outsideTarget);

            var exception = Assert.Throws<InvalidOperationException>(ownedDirectory.Dispose);

            Assert.Contains("reparse point", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("cleanup target sentinel", await File.ReadAllTextAsync(sentinelPath));
        }
        finally
        {
            if (Directory.Exists(ownedDirectory.Path))
            {
                Directory.Delete(ownedDirectory.Path);
            }

            Assert.True(File.Exists(sentinelPath), "Junction cleanup must not traverse into its target.");
            Directory.Delete(outsideTarget, recursive: true);
            ownedDirectory.Dispose();
        }
    }

    private static void CreateJunction(string junctionPath, string targetPath)
    {
        var startInfo = new ProcessStartInfo
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

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        process.WaitForExit();
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        Assert.True(process.ExitCode == 0, $"Failed to create test junction: {output}");
    }
}
