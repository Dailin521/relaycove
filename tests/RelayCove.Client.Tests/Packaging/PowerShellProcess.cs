using System.Diagnostics;
using System.Text;

namespace RelayCove.Client.Tests.Packaging;

internal static class PowerShellProcess
{
    public static async Task<PackagingProcessResult> RunAsync(
        string scriptRelativePath,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        string? workingDirectory = null)
    {
        var scriptPath = PackagingTestPaths.GetRepositoryPath(
            scriptRelativePath.Split('/', StringSplitOptions.RemoveEmptyEntries));
        Assert.True(File.Exists(scriptPath), $"PowerShell script is missing: {scriptPath}");

        var startInfo = new ProcessStartInfo
        {
            FileName = "pwsh",
            WorkingDirectory = workingDirectory ?? PackagingTestPaths.RepositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        var standardOutput = new StringBuilder();
        var standardError = new StringBuilder();
        process.OutputDataReceived += (_, eventArgs) => AppendLine(standardOutput, eventArgs.Data);
        process.ErrorDataReceived += (_, eventArgs) => AppendLine(standardError, eventArgs.Data);

        Assert.True(process.Start(), $"Failed to start PowerShell script: {scriptPath}");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException($"Script exceeded {timeout}: {scriptPath}");
        }

        return new PackagingProcessResult(process.ExitCode, standardOutput.ToString(), standardError.ToString());
    }

    private static void AppendLine(StringBuilder builder, string? value)
    {
        if (value is not null)
        {
            builder.AppendLine(value);
        }
    }
}

internal sealed record PackagingProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public string CombinedOutput => $"{StandardOutput}{Environment.NewLine}{StandardError}";
}
