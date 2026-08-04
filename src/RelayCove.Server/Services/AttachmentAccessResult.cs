namespace RelayCove.Server.Services;

public sealed record AttachmentAccessResult(
    AttachmentAccessStatus Status,
    AuthorizedAttachment? Value = null)
{
    public override string ToString() =>
        $"{nameof(AttachmentAccessResult)} {{ Status = {Status}, Value = [REDACTED] }}";
}
