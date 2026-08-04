using RelayCove.Shared.Messages;

namespace RelayCove.Server.Services;

public sealed record AuthorizedAttachment(
    AttachmentDto Attachment,
    string OriginalFileName,
    string ContentType,
    string StoredPath,
    string Sha256)
{
    public override string ToString() =>
        $"{nameof(AuthorizedAttachment)} {{ Attachment = [REDACTED], OriginalFileName = [REDACTED], " +
        "ContentType = [REDACTED], StoredPath = [REDACTED], Sha256 = [REDACTED] }";
}
