using RelayCove.Client.Storage;

namespace RelayCove.Client.Attachments;

internal interface IWindowsAttachmentOpenService : IAsyncDisposable
{
    ValueTask<WindowsAttachmentOpenPreparation> PrepareAsync(
        ClientAttachmentOpenLease managedOpenCopy,
        IntPtr ownerWindow,
        CancellationToken cancellationToken = default);
}
