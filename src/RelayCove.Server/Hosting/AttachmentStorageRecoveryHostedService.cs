using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using RelayCove.Server.Data;
using RelayCove.Server.Services;

namespace RelayCove.Server.Hosting;

public sealed class AttachmentStorageRecoveryHostedService(
    IServiceScopeFactory scopeFactory,
    AttachmentStoragePaths storagePaths,
    ILogger<AttachmentStorageRecoveryHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        storagePaths.Initialize();
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

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
