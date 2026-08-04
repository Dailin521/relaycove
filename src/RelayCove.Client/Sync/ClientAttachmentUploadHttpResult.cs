using RelayCove.Shared.Messages;

namespace RelayCove.Client.Sync;

internal sealed record ClientAttachmentUploadHttpResult(
    ClientAttachmentUploadHttpStatus Status,
    AttachmentDto? Attachment)
{
    public static ClientAttachmentUploadHttpResult Success(AttachmentDto attachment) =>
        new(ClientAttachmentUploadHttpStatus.Success, attachment);

    public static ClientAttachmentUploadHttpResult Failure(
        ClientAttachmentUploadHttpStatus status) =>
        new(status, Attachment: null);

    public override string ToString() =>
        $"{nameof(ClientAttachmentUploadHttpResult)} {{ Status = {Status}, " +
        "Attachment = [REDACTED] }";
}
