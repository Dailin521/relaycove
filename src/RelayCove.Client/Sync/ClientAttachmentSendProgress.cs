namespace RelayCove.Client.Sync;

internal sealed record ClientAttachmentSendProgress
{
    public ClientAttachmentSendProgress(
        ClientAttachmentSendProgressStage stage,
        int attachmentIndex,
        int attachmentCount,
        long bytesCopied,
        long totalBytes,
        int percent)
    {
        if (!Enum.IsDefined(stage) ||
            attachmentCount is < 1 or > 10 ||
            attachmentIndex < 1 || attachmentIndex > attachmentCount ||
            totalBytes <= 0 ||
            bytesCopied < 0 || bytesCopied > totalBytes ||
            percent is < 0 or > 100 ||
            percent != (int)((bytesCopied * 100) / totalBytes) ||
            (stage == ClientAttachmentSendProgressStage.Finalizing &&
             (bytesCopied != totalBytes || percent != 100)))
        {
            throw new ArgumentOutOfRangeException(nameof(bytesCopied));
        }

        Stage = stage;
        AttachmentIndex = attachmentIndex;
        AttachmentCount = attachmentCount;
        BytesCopied = bytesCopied;
        TotalBytes = totalBytes;
        Percent = percent;
    }

    public ClientAttachmentSendProgressStage Stage { get; }

    public int AttachmentIndex { get; }

    public int AttachmentCount { get; }

    public long BytesCopied { get; }

    public long TotalBytes { get; }

    public int Percent { get; }

    public override string ToString() =>
        $"{nameof(ClientAttachmentSendProgress)} {{ Stage = {Stage}, " +
        $"AttachmentIndex = {AttachmentIndex}, AttachmentCount = {AttachmentCount}, " +
        $"Percent = {Percent} }}";
}
