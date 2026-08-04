using System.Data;
using Microsoft.EntityFrameworkCore;
using RelayCove.Server.Data;
using RelayCove.Server.Data.Entities;
using RelayCove.Shared.Messages;

namespace RelayCove.Server.Services;

public sealed class AttachmentCommandService(
    RelayCoveDbContext dbContext,
    ServerClock clock,
    ILogger<AttachmentCommandService> logger)
{
    public async Task<AttachmentUploadResult> CommitAsync(
        Guid actorUserId,
        AttachmentStagedUpload stagedUpload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stagedUpload);
        if (actorUserId == Guid.Empty)
        {
            return new AttachmentUploadResult(AttachmentUploadStatus.ActorUnavailable, null);
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var actorIsActive = await dbContext.Users.AnyAsync(
            user => user.Id == actorUserId && !user.IsDisabled,
            cancellationToken);
        if (!actorIsActive)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return new AttachmentUploadResult(AttachmentUploadStatus.ActorUnavailable, null);
        }

        var attachment = new Attachment(
            stagedUpload.Id,
            actorUserId,
            stagedUpload.OriginalFileName,
            stagedUpload.StoredFileName,
            stagedUpload.ContentType,
            stagedUpload.Size,
            stagedUpload.Sha256,
            clock.UtcNow);
        dbContext.Attachments.Add(attachment);
        await dbContext.SaveChangesAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        stagedUpload.Publish();

        // Once the final file exists, preserving it is safer than deleting it after an
        // ambiguous local commit failure. Startup recovery removes it if no row committed.
        stagedUpload.PreservePublishedFile();
        await transaction.CommitAsync(CancellationToken.None);
        stagedUpload.Accept();

        logger.LogInformation(
            "User {ActorUserId} uploaded attachment {AttachmentId}.",
            actorUserId,
            attachment.Id);
        return new AttachmentUploadResult(
            AttachmentUploadStatus.Created,
            AttachmentDtoFactory.Create(attachment));
    }
}
