using RelayCove.Shared.Messages;

namespace RelayCove.Client.Storage;

internal sealed record LocalAttachmentDownloadRecord(
    Guid ConversationId,
    AttachmentDto Attachment,
    LocalAttachmentDownloadState State,
    string? LocalPath)
{
    public override string ToString() =>
        $"{nameof(LocalAttachmentDownloadRecord)} {{ ConversationId = [REDACTED], " +
        "Attachment = [REDACTED], " +
        $"State = {State}, LocalPath = [REDACTED] }}";
}
