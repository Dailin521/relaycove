using RelayCove.Client.Storage;

namespace RelayCove.Client.Sync;

internal enum WindowsAttachmentShellStatus
{
    Revealed = 1,
    Unavailable = 2,
}

internal interface IWindowsAttachmentShell
{
    WindowsAttachmentShellStatus Reveal(
        ClientAttachmentCacheStore.ValidatedFile file);
}
