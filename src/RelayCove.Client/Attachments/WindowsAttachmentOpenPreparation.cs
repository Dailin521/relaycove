namespace RelayCove.Client.Attachments;

internal sealed class WindowsAttachmentOpenPreparation : IDisposable
{
    private readonly WindowsAttachmentOpenService.OpenJob? job;

    internal WindowsAttachmentOpenPreparation(
        WindowsAttachmentOpenStatus status,
        WindowsAttachmentOpenService.OpenJob? job,
        Task<WindowsAttachmentOpenResult> completion)
    {
        Status = status;
        this.job = job;
        Completion = completion;
    }

    public WindowsAttachmentOpenStatus Status { get; }

    public Task<WindowsAttachmentOpenResult> Completion { get; }

    public bool CanCommit => job is not null;

    public bool Commit() => job?.Commit() == true;

    public bool Abort() => job?.Abort() == true;

    public void Dispose() => Abort();
}
