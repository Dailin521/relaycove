namespace RelayCove.Preview.NativeTests;

internal static class ProbeLog
{
    private static readonly object Gate = new();
    internal static string DirectoryPath { get; private set; } = null!;
    internal static string Mode => Environment.GetEnvironmentVariable("PREVIEW_PROBE_MODE") ?? "old";
    internal static void Initialize()
    {
        DirectoryPath = Environment.GetEnvironmentVariable("PREVIEW_PROBE_OUTPUT")
            ?? throw new InvalidOperationException("PREVIEW_PROBE_OUTPUT must name an isolated fixture directory.");
        Directory.CreateDirectory(DirectoryPath);
        Write("start", $"pid={Environment.ProcessId} mode={Mode}");
    }
    internal static void Write(string phase, string? detail = null)
    {
        lock (Gate) File.AppendAllText(Path.Combine(DirectoryPath, "phases.log"),
            $"{DateTimeOffset.UtcNow:O} {phase} {detail}{Environment.NewLine}");
    }
}
