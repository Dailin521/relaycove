using Microsoft.EntityFrameworkCore;
using RelayCove.Server.Data;

namespace RelayCove.Server.Services;

public sealed class AttachmentQueryService(
    RelayCoveDbContext dbContext,
    AttachmentStoragePaths storagePaths)
{
    public async Task<AttachmentAccessResult> GetAsync(
        Guid actorUserId,
        Guid attachmentId,
        CancellationToken cancellationToken)
    {
        if (actorUserId == Guid.Empty || attachmentId == Guid.Empty)
        {
            return new AttachmentAccessResult(AttachmentAccessStatus.AccessRevoked);
        }

        var row = await (
                from attachment in dbContext.Attachments.AsNoTracking()
                where attachment.Id == attachmentId && attachment.MessageId != null
                join message in dbContext.Messages.AsNoTracking()
                    on attachment.MessageId equals message.Id
                join conversation in ConversationAccessQuery.VisibleTo(dbContext, actorUserId)
                    on message.ConversationId equals conversation.Id
                select new
                {
                    attachment.Id,
                    attachment.OriginalFileName,
                    attachment.StoredFileName,
                    attachment.ContentType,
                    attachment.Size,
                    attachment.Sha256,
                })
            .SingleOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return new AttachmentAccessResult(AttachmentAccessStatus.AccessRevoked);
        }

        var storedPath = storagePaths.GetStoredFilePath(row.StoredFileName);
        if (!File.Exists(storedPath))
        {
            throw new InvalidOperationException("An authorized attachment has no physical file.");
        }

        return new AttachmentAccessResult(
            AttachmentAccessStatus.Success,
            new AuthorizedAttachment(
                AttachmentDtoFactory.Create(
                    row.Id,
                    row.OriginalFileName,
                    row.ContentType,
                    row.Size),
                row.OriginalFileName,
                row.ContentType,
                storedPath,
                row.Sha256));
    }
}
