namespace RelayCove.Client.Attachments;

internal sealed record ClientAttachmentDownloadContext
{
    public ClientAttachmentDownloadContext(
        Guid conversationId,
        Guid messageClientId,
        Guid attachmentId,
        long contextVersion)
    {
        if (conversationId == Guid.Empty)
        {
            throw new ArgumentException("A conversation ID is required.", nameof(conversationId));
        }

        if (messageClientId == Guid.Empty)
        {
            throw new ArgumentException("A message client ID is required.", nameof(messageClientId));
        }

        if (attachmentId == Guid.Empty)
        {
            throw new ArgumentException("An attachment ID is required.", nameof(attachmentId));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(contextVersion);

        ConversationId = conversationId;
        MessageClientId = messageClientId;
        AttachmentId = attachmentId;
        ContextVersion = contextVersion;
    }

    public Guid ConversationId { get; }

    public Guid MessageClientId { get; }

    public Guid AttachmentId { get; }

    public long ContextVersion { get; }

    public override string ToString() =>
        $"{nameof(ClientAttachmentDownloadContext)} {{ " +
        "ConversationId = [REDACTED], MessageClientId = [REDACTED], " +
        "AttachmentId = [REDACTED], ContextVersion = [REDACTED] }";
}
