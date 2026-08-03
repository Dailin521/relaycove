using System.Runtime.InteropServices;

namespace RelayCove.Client.Accounts;

internal static class ClientClipboardWriter
{
    public static bool TryWrite(string content, Action<string> write)
    {
        ArgumentException.ThrowIfNullOrEmpty(content);
        ArgumentNullException.ThrowIfNull(write);
        try
        {
            write(content);
            return true;
        }
        catch (ExternalException)
        {
            return false;
        }
    }
}
