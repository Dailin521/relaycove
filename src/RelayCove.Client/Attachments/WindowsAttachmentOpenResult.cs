namespace RelayCove.Client.Attachments;

internal sealed record WindowsAttachmentOpenResult(WindowsAttachmentOpenStatus Status)
{
    public bool WasExecuted => Status == WindowsAttachmentOpenStatus.Executed;
}
