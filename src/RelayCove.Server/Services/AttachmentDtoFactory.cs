using RelayCove.Server.Data.Entities;
using RelayCove.Shared.Messages;

namespace RelayCove.Server.Services;

internal static class AttachmentDtoFactory
{
    public static AttachmentDto Create(Attachment attachment) =>
        Create(
            attachment.Id,
            attachment.OriginalFileName,
            attachment.ContentType,
            attachment.Size);

    public static AttachmentDto Create(
        Guid id,
        string originalFileName,
        string contentType,
        long size) =>
        new(
            id,
            originalFileName,
            contentType,
            size,
            $"/api/attachments/{id:D}/download",
            null);
}
