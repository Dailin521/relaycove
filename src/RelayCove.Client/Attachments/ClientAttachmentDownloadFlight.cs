namespace RelayCove.Client.Attachments;

internal sealed class ClientAttachmentDownloadFlight
{
    internal ClientAttachmentDownloadFlight(ClientAttachmentDownloadContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        Context = context;
    }

    public ClientAttachmentDownloadContext Context { get; }

    public override string ToString() =>
        $"{nameof(ClientAttachmentDownloadFlight)} {{ Context = [REDACTED] }}";
}
