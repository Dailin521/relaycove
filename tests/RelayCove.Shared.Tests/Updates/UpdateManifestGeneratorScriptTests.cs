using System.Diagnostics;
using System.Text.RegularExpressions;

namespace RelayCove.Shared.Tests.Updates;

public sealed class UpdateManifestGeneratorScriptTests
{
    [Fact]
    public void Script_WhenReviewed_KeepsTrustedCommitAndVerificationContracts()
    {
        var script = File.ReadAllText(GetScriptPath());

        Assert.Contains("[string] $ExpectedCommit", script, StringComparison.Ordinal);
        Assert.Contains("git -C $repositoryRoot rev-parse --verify HEAD", script, StringComparison.Ordinal);
        Assert.Contains("ExpectedCommit must be exactly 40 lowercase hexadecimal characters", script, StringComparison.Ordinal);
        Assert.Contains(@"\A[0-9a-f]{40}\z", script, StringComparison.Ordinal);
        Assert.Contains("$ExpectedCommit -cnotmatch", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Get-ReleaseCommit", script, StringComparison.Ordinal);
        Assert.DoesNotContain("ZipFile", script, StringComparison.Ordinal);
        Assert.DoesNotContain(".GetEntry(", script, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(script, "\\$LASTEXITCODE", RegexOptions.CultureInvariant));
        Assert.Contains("$verifySucceeded = $?", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Script_WhenReviewed_ChecksArchiveSizeBeforeVerifierOrHashing()
    {
        var script = File.ReadAllText(GetScriptPath());
        var sizeLookup = script.IndexOf("$archiveInfo = Get-Item -LiteralPath $archivePath", StringComparison.Ordinal);
        var verifierCall = script.IndexOf("& $verifyScript @verifyArguments", StringComparison.Ordinal);
        var hashCall = script.IndexOf("Get-FileHash -LiteralPath $archivePath", StringComparison.Ordinal);

        Assert.True(sizeLookup >= 0);
        Assert.True(verifierCall > sizeLookup);
        Assert.True(hashCall > sizeLookup);
    }

    [Fact]
    public async Task Script_WhenExpectedCommitIsMalformed_FailsBeforeReadingArchive()
    {
        var startInfo = new ProcessStartInfo("pwsh")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(GetScriptPath());
        startInfo.ArgumentList.Add("-Version");
        startInfo.ArgumentList.Add("1.0.0-rc.1");
        startInfo.ArgumentList.Add("-MinimumSupportedVersion");
        startInfo.ArgumentList.Add("0.9.0");
        startInfo.ArgumentList.Add("-DownloadUrl");
        startInfo.ArgumentList.Add("https://updates.example.test/release.zip");
        startInfo.ArgumentList.Add("-ExpectedCommit");
        startInfo.ArgumentList.Add(new string('A', 40));

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start pwsh.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await standardOutput + await standardError;

        Assert.NotEqual(0, process.ExitCode);
        Assert.Contains("ExpectedCommit must be exactly 40 lowercase hexadecimal characters", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Client release archive was not found", output, StringComparison.Ordinal);
    }

    private static string GetScriptPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "scripts", "generate-update-manifest.ps1");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found from the test output directory.");
    }
}
