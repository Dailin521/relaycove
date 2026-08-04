namespace RelayCove.Server.Services;

public sealed record AttachmentUploadReadResult(
    AttachmentUploadReadStatus Status,
    AttachmentStagedUpload? Upload)
{
    public static AttachmentUploadReadResult Success(AttachmentStagedUpload upload) =>
        new(AttachmentUploadReadStatus.Success, upload);

    public static AttachmentUploadReadResult InvalidRequest() =>
        new(AttachmentUploadReadStatus.InvalidRequest, null);

    public static AttachmentUploadReadResult TooLarge() =>
        new(AttachmentUploadReadStatus.TooLarge, null);

    public override string ToString() =>
        $"{nameof(AttachmentUploadReadResult)} {{ Status = {Status}, Upload = [REDACTED] }}";
}
