using RelayCove.App.Platforms.Windows;

namespace RelayCove.App.Controls;

internal static class ImagePreviewDiagnostics
{
    // The isolated native host redirects this sink to its own test artifact directory.
    internal static Action<string>? Sink { get; set; }

    internal static void Record(string stage, Exception? exception = null)
    {
        var entry = "image-preview:" + stage;
        if (exception is not null) entry += $":{exception.GetType().Name}:0x{exception.HResult:X8}";
        if (Sink is { } sink) sink(entry);
        else WindowsLifecycleDiagnostics.Write(entry);
    }
}
