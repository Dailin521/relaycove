using System.Diagnostics;
using System.Reflection;

namespace RelayCove.App.Platforms.Windows;

internal static class WindowsLifecycleDiagnostics
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RichChat",
        "lifecycle.log");

    internal static void Write(string stage)
    {
        if (string.IsNullOrWhiteSpace(stage)) return;
        var entry = FormatEntry(stage);
        Debug.WriteLine(entry);
        _ = Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath, entry + Environment.NewLine);
            }
            catch (Exception)
            {
                // Lifecycle diagnostics must never delay shutdown.
            }
        });
    }

    internal static string FormatEntry(string stage)
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
        return $"{DateTimeOffset.UtcNow:O} version={version} windows-build={Environment.OSVersion.Version.Build} pid={Environment.ProcessId} stage={stage}";
    }
}
