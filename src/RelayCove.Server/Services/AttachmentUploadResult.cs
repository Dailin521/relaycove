using RelayCove.Shared.Messages;

namespace RelayCove.Server.Services;

public sealed record AttachmentUploadResult(
    AttachmentUploadStatus Status,
    AttachmentDto? Attachment)
{
    public override string ToString() =>
        $"{nameof(AttachmentUploadResult)} {{ Status = {Status}, Attachment = [REDACTED] }}";
}
