using System.ComponentModel;
using System.Diagnostics;

namespace RelayCove.Client.Accounts;

internal static class ClientExternalLinkLauncher
{
    public static bool TryOpen(
        ClientMessageLinkPresentation link,
        Action<ProcessStartInfo> start)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(start);
        if (!ClientMessageLinkParser.TryNormalizeAbsoluteHttpLink(
                link.AbsoluteUri,
                out var absoluteUri))
        {
            return false;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = absoluteUri,
            Arguments = string.Empty,
            UseShellExecute = true,
            Verb = string.Empty,
            WorkingDirectory = string.Empty,
            ErrorDialog = false,
        };
        try
        {
            start(startInfo);
            return true;
        }
        catch (Exception exception) when (
            exception is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return false;
        }
    }
}
