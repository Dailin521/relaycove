using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using RelayCove.Server.Data;
using RelayCove.Server.Options;
using RelayCove.Server.Services;

namespace RelayCove.Server.Hosting;

public sealed class AttachmentStorageRecoveryHostedService(
    IServiceScopeFactory scopeFactory,
    AttachmentStoragePaths storagePaths,
    ServerClock clock,
    IOptions<UploadOptions> uploadOptions,
    ILogger<AttachmentStorageRecoveryHostedService> logger) : IHostedService
{
    private const int CleanupBatchSize = 500;
    private readonly SemaphoreSlim maintenanceGate = new(1, 1);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        storagePaths.Initialize();
        await CleanupExpiredUnboundAsync(cancellationToken);
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<RelayCoveDbContext>();
        var databaseCreator = dbContext.Database.GetService<IRelationalDatabaseCreator>();
        if (!await databaseCreator.ExistsAsync(cancellationToken))
        {
            logger.LogInformation("Attachment storage recovery skipped because the database does not exist.");
            return;
        }

        var storedFileNames = await dbContext.Attachments
            .AsNoTracking()
            .Select(attachment => attachment.StoredFileName)
            .ToHashSetAsync(StringComparer.Ordinal, cancellationToken);

        foreach (var storedFileName in storedFileNames)
        {
            var path = Path.Combine(storagePaths.UploadsRoot, storedFileName);
            if (!File.Exists(path))
            {
                throw new InvalidOperationException("An attachment metadata row has no physical file.");
            }
        }

        var recoveredCount = 0;
        foreach (var path in Directory.EnumerateFiles(storagePaths.UploadsRoot, "*", SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileName(path);
            var isStaging = storagePaths.IsManagedStagingFileName(fileName);
            var isUntrackedFinal = AttachmentStoragePaths.IsManagedStoredFileName(fileName) &&
                                   !storedFileNames.Contains(fileName);
            if (!isStaging && !isUntrackedFinal)
            {
                continue;
            }

            File.Delete(path);
            recoveredCount++;
        }

        logger.LogInformation(
            "Attachment storage recovery completed with {RecoveredArtifactCount} artifacts removed.",
            recoveredCount);
    }

    public async Task CleanupExpiredUnboundAsync(CancellationToken cancellationToken)
    {
        await maintenanceGate.WaitAsync(cancellationToken);
        try
        {
            await CleanupExpiredUnboundCoreAsync(cancellationToken);
        }
        finally
        {
            maintenanceGate.Release();
        }
    }

    private async Task CleanupExpiredUnboundCoreAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<RelayCoveDbContext>();
        var databaseCreator = dbContext.Database.GetService<IRelationalDatabaseCreator>();
        if (!await databaseCreator.ExistsAsync(cancellationToken))
        {
            return;
        }

        var cutoff = clock.UtcNow.AddHours(-uploadOptions.Value.UnboundRetentionHours);
        var deletedRowCount = 0;
        var deletedFileCount = 0;
        while (true)
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                System.Data.IsolationLevel.Serializable,
                cancellationToken);
            var expired = await dbContext.Attachments
                .AsNoTracking()
                .Where(attachment => attachment.MessageId == null && attachment.CreatedAt < cutoff)
                .OrderBy(attachment => attachment.Id)
                .Take(CleanupBatchSize)
                .Select(attachment => new ExpiredAttachment(attachment.Id, attachment.StoredFileName))
                .ToArrayAsync(cancellationToken);
            if (expired.Length == 0)
            {
                await transaction.CommitAsync(CancellationToken.None);
                break;
            }

            var ids = expired.Select(attachment => attachment.Id).ToArray();
            await dbContext.Attachments
                .Where(attachment =>
                    ids.Contains(attachment.Id) &&
                    attachment.MessageId == null &&
                    attachment.CreatedAt < cutoff)
                .ExecuteDeleteAsync(cancellationToken);
            var remainingIds = await dbContext.Attachments
                .AsNoTracking()
                .Where(attachment => ids.Contains(attachment.Id))
                .Select(attachment => attachment.Id)
                .ToHashSetAsync(cancellationToken);
            var deleted = expired
                .Where(attachment => !remainingIds.Contains(attachment.Id))
                .ToArray();
            await transaction.CommitAsync(CancellationToken.None);
            deletedRowCount += deleted.Length;

            foreach (var attachment in deleted)
            {
                try
                {
                    File.Delete(storagePaths.GetStoredFilePath(attachment.StoredFileName));
                    deletedFileCount++;
                }
                catch (Exception exception)
                {
                    logger.LogWarning(
                        "Failed to remove an expired unbound attachment artifact. Error type: {ErrorType}.",
                        exception.GetType().Name);
                }
            }
        }

        if (deletedRowCount > 0)
        {
            logger.LogInformation(
                "Expired unbound attachment cleanup removed {DeletedRowCount} rows and {DeletedFileCount} files.",
                deletedRowCount,
                deletedFileCount);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private sealed record ExpiredAttachment(Guid Id, string StoredFileName);
}
