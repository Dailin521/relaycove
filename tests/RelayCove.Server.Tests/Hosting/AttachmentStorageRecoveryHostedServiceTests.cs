using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RelayCove.Server.Data;
using RelayCove.Server.Data.Entities;
using RelayCove.Server.Hosting;
using RelayCove.Server.Services;
using RelayCove.Server.Tests.Infrastructure;
using RelayCove.Shared.Conversations;
using RelayCove.Shared.Messages;

namespace RelayCove.Server.Tests.Hosting;

public sealed class AttachmentStorageRecoveryHostedServiceTests
{
    private const string ExistingPassword = "a secure attachment recovery phrase";

    [Fact]
    public async Task Recovery_WhenManagedArtifactsHaveNoRows_DeletesOnlyManagedArtifacts()
    {
        using var factory = new RelayCoveWebApplicationFactory();
        await factory.InitializeDatabaseAsync();
        var managedName = $"{Guid.NewGuid():N}_{new string('a', 32)}";
        var managedFinal = Path.Combine(factory.UploadsPath, managedName);
        var managedStaging = Path.Combine(factory.UploadsPath, $".upload_{managedName}.tmp");
        var unknown = Path.Combine(factory.UploadsPath, "operator-note.txt");
        await File.WriteAllBytesAsync(managedFinal, [1]);
        await File.WriteAllBytesAsync(managedStaging, [2]);
        await File.WriteAllBytesAsync(unknown, [3]);
        var hostedService = factory.Services.GetServices<IHostedService>()
            .OfType<AttachmentStorageRecoveryHostedService>()
            .Single();

        await hostedService.StartAsync(CancellationToken.None);

        Assert.False(File.Exists(managedFinal));
        Assert.False(File.Exists(managedStaging));
        Assert.True(File.Exists(unknown));
    }

    [Fact]
    public async Task Recovery_WhenMetadataAndPhysicalFileAgree_PreservesTrackedFile()
    {
        using var factory = new RelayCoveWebApplicationFactory();
        await factory.InitializeDatabaseAsync();
        var userName = $"attachment-recovery-{Guid.NewGuid():N}";
        var userId = await factory.CreateUserAsync(userName, ExistingPassword);
        var attachmentId = Guid.NewGuid();
        var storedFileName = $"{attachmentId:N}_{new string('a', 32)}";
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<RelayCoveDbContext>();
            dbContext.Attachments.Add(CreateAttachment(
                attachmentId,
                userId,
                storedFileName,
                factory.Clock.GetUtcNow().UtcDateTime));
            await dbContext.SaveChangesAsync();
        }

        var physicalPath = Path.Combine(factory.UploadsPath, storedFileName);
        await File.WriteAllBytesAsync(physicalPath, [42]);
        var hostedService = factory.Services.GetServices<IHostedService>()
            .OfType<AttachmentStorageRecoveryHostedService>()
            .Single();

        await hostedService.StartAsync(CancellationToken.None);

        Assert.True(File.Exists(physicalPath));
        Assert.Equal([42], await File.ReadAllBytesAsync(physicalPath));
    }

    [Fact]
    public async Task Recovery_WhenMetadataFileIsMissing_FailsClosed()
    {
        using var factory = new RelayCoveWebApplicationFactory();
        await factory.InitializeDatabaseAsync();
        var userName = $"attachment-missing-{Guid.NewGuid():N}";
        var userId = await factory.CreateUserAsync(userName, ExistingPassword);
        var attachmentId = Guid.NewGuid();
        var storedFileName = $"{attachmentId:N}_{new string('a', 32)}";
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<RelayCoveDbContext>();
            dbContext.Attachments.Add(CreateAttachment(
                attachmentId,
                userId,
                storedFileName,
                factory.Clock.GetUtcNow().UtcDateTime));
            await dbContext.SaveChangesAsync();
        }

        var hostedService = factory.Services.GetServices<IHostedService>()
            .OfType<AttachmentStorageRecoveryHostedService>()
            .Single();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            hostedService.StartAsync(CancellationToken.None));

        Assert.Contains("no physical file", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Startup_WhenUploadsPathIsAFile_FailsBeforeServing()
    {
        using var factory = new RelayCoveWebApplicationFactory();
        Directory.CreateDirectory(Path.GetDirectoryName(factory.UploadsPath)!);
        await File.WriteAllTextAsync(factory.UploadsPath, "not a directory");

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => factory.InitializeDatabaseAsync());

        Assert.Contains("uploads path is a file", exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CleanupExpiredUnbound_WhenLeaseElapsed_DeletesOnlyExpiredUnboundRowsAndFiles()
    {
        using var factory = new RelayCoveWebApplicationFactory(
            1_000,
            1_000,
            configurationOverrides: new Dictionary<string, string?>
            {
                ["Uploads:UnboundRetentionHours"] = "1",
            });
        await factory.InitializeDatabaseAsync();
        var userId = await factory.CreateUserAsync(
            $"attachment-lease-{Guid.NewGuid():N}",
            ExistingPassword);
        var now = factory.Services.GetRequiredService<ServerClock>().UtcNow;
        var expiredId = Guid.NewGuid();
        var freshId = Guid.NewGuid();
        var boundId = Guid.NewGuid();
        var expired = CreateAttachment(expiredId, userId, CreateStoredFileName(expiredId), now.AddHours(-2));
        var fresh = CreateAttachment(freshId, userId, CreateStoredFileName(freshId), now.AddMinutes(-30));
        var bound = CreateAttachment(boundId, userId, CreateStoredFileName(boundId), now.AddHours(-2));
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<RelayCoveDbContext>();
            var conversation = Conversation.CreateChannel(
                Guid.NewGuid(),
                ConversationType.PublicChannel,
                "attachment cleanup",
                userId,
                now);
            var message = new Message(
                Guid.NewGuid(),
                conversation.Id,
                userId,
                MessageType.File,
                content: null,
                replyToMessageId: null,
                now);
            dbContext.AddRange(conversation, message, expired, fresh, bound);
            await dbContext.SaveChangesAsync();
            dbContext.Entry(bound).Property(attachment => attachment.MessageId).CurrentValue = message.Id;
            await dbContext.SaveChangesAsync();
        }

        foreach (var attachment in new[] { expired, fresh, bound })
        {
            await File.WriteAllBytesAsync(
                Path.Combine(factory.UploadsPath, attachment.StoredFileName),
                [42]);
        }

        var unknown = Path.Combine(factory.UploadsPath, "operator-note.txt");
        await File.WriteAllBytesAsync(unknown, [7]);
        var hostedService = factory.Services.GetServices<IHostedService>()
            .OfType<AttachmentStorageRecoveryHostedService>()
            .Single();

        await hostedService.CleanupExpiredUnboundAsync(CancellationToken.None);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<RelayCoveDbContext>();
            var remaining = await dbContext.Attachments
                .AsNoTracking()
                .Select(attachment => attachment.Id)
                .ToArrayAsync();
            Assert.DoesNotContain(expired.Id, remaining);
            Assert.Contains(fresh.Id, remaining);
            Assert.Contains(bound.Id, remaining);
        }

        Assert.False(File.Exists(Path.Combine(factory.UploadsPath, expired.StoredFileName)));
        Assert.True(File.Exists(Path.Combine(factory.UploadsPath, fresh.StoredFileName)));
        Assert.True(File.Exists(Path.Combine(factory.UploadsPath, bound.StoredFileName)));
        Assert.True(File.Exists(unknown));
        Assert.Single(factory.Services.GetServices<IHostedService>()
            .OfType<AttachmentStorageMaintenanceHostedService>());
    }

    [Fact]
    public async Task CleanupExpiredUnbound_WhenFileDeletionFails_StartupRecoveryRemovesArtifactWithoutLeakingName()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var factory = new RelayCoveWebApplicationFactory(
            1_000,
            1_000,
            configurationOverrides: new Dictionary<string, string?>
            {
                ["Uploads:UnboundRetentionHours"] = "1",
            });
        await factory.InitializeDatabaseAsync();
        var userId = await factory.CreateUserAsync(
            $"attachment-delete-retry-{Guid.NewGuid():N}",
            ExistingPassword);
        var now = factory.Services.GetRequiredService<ServerClock>().UtcNow;
        var attachmentId = Guid.NewGuid();
        var attachment = CreateAttachment(
            attachmentId,
            userId,
            CreateStoredFileName(attachmentId),
            now.AddHours(-2));
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<RelayCoveDbContext>();
            dbContext.Attachments.Add(attachment);
            await dbContext.SaveChangesAsync();
        }

        var path = Path.Combine(factory.UploadsPath, attachment.StoredFileName);
        await File.WriteAllBytesAsync(path, [42]);
        var hostedService = factory.Services.GetServices<IHostedService>()
            .OfType<AttachmentStorageRecoveryHostedService>()
            .Single();
        await using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await hostedService.CleanupExpiredUnboundAsync(CancellationToken.None);
            Assert.True(File.Exists(path));
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<RelayCoveDbContext>();
            Assert.False(await dbContext.Attachments.AnyAsync(candidate => candidate.Id == attachment.Id));
        }

        var failureLog = Assert.Single(factory.LogMessages, message =>
            message.StartsWith(
                "Failed to remove an expired unbound attachment artifact.",
                StringComparison.Ordinal));
        Assert.DoesNotContain(attachment.StoredFileName, failureLog, StringComparison.Ordinal);
        Assert.Contains(nameof(IOException), failureLog, StringComparison.Ordinal);
        await hostedService.StartAsync(CancellationToken.None);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task CleanupExpiredUnbound_WhenCanceledBeforeDatabaseWork_PreservesRowAndFile()
    {
        using var factory = new RelayCoveWebApplicationFactory(
            1_000,
            1_000,
            configurationOverrides: new Dictionary<string, string?>
            {
                ["Uploads:UnboundRetentionHours"] = "1",
            });
        await factory.InitializeDatabaseAsync();
        var userId = await factory.CreateUserAsync(
            $"attachment-cancel-{Guid.NewGuid():N}",
            ExistingPassword);
        var now = factory.Services.GetRequiredService<ServerClock>().UtcNow;
        var attachmentId = Guid.NewGuid();
        var attachment = CreateAttachment(
            attachmentId,
            userId,
            CreateStoredFileName(attachmentId),
            now.AddHours(-2));
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<RelayCoveDbContext>();
            dbContext.Attachments.Add(attachment);
            await dbContext.SaveChangesAsync();
        }

        var path = Path.Combine(factory.UploadsPath, attachment.StoredFileName);
        await File.WriteAllBytesAsync(path, [42]);
        var hostedService = factory.Services.GetServices<IHostedService>()
            .OfType<AttachmentStorageRecoveryHostedService>()
            .Single();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            hostedService.CleanupExpiredUnboundAsync(cancellation.Token));

        await using var verificationScope = factory.Services.CreateAsyncScope();
        var verificationContext = verificationScope.ServiceProvider.GetRequiredService<RelayCoveDbContext>();
        Assert.True(await verificationContext.Attachments.AnyAsync(candidate =>
            candidate.Id == attachment.Id));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task CleanupExpiredUnbound_WhenDatabaseIsLocked_PreservesRowAndFile()
    {
        using var factory = new RelayCoveWebApplicationFactory(
            1_000,
            1_000,
            databaseTimeoutSeconds: 1,
            configurationOverrides: new Dictionary<string, string?>
            {
                ["Uploads:UnboundRetentionHours"] = "1",
            });
        await factory.InitializeDatabaseAsync();
        var userId = await factory.CreateUserAsync(
            $"attachment-db-failure-{Guid.NewGuid():N}",
            ExistingPassword);
        var now = factory.Services.GetRequiredService<ServerClock>().UtcNow;
        var attachmentId = Guid.NewGuid();
        var attachment = CreateAttachment(
            attachmentId,
            userId,
            CreateStoredFileName(attachmentId),
            now.AddHours(-2));
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<RelayCoveDbContext>();
            dbContext.Attachments.Add(attachment);
            await dbContext.SaveChangesAsync();
        }

        var path = Path.Combine(factory.UploadsPath, attachment.StoredFileName);
        await File.WriteAllBytesAsync(path, [42]);
        var hostedService = factory.Services.GetServices<IHostedService>()
            .OfType<AttachmentStorageRecoveryHostedService>()
            .Single();
        await using var locker = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = factory.DatabasePath,
            DefaultTimeout = 1,
            ForeignKeys = true,
            Pooling = false,
        }.ToString());
        await locker.OpenAsync();
        await using (var begin = locker.CreateCommand())
        {
            begin.CommandText = "BEGIN EXCLUSIVE;";
            await begin.ExecuteNonQueryAsync();
        }

        SqliteException? failure = null;
        try
        {
            failure = await Assert.ThrowsAsync<SqliteException>(() =>
                hostedService.CleanupExpiredUnboundAsync(CancellationToken.None));
        }
        finally
        {
            await using var rollback = locker.CreateCommand();
            rollback.CommandText = "ROLLBACK;";
            await rollback.ExecuteNonQueryAsync();
        }

        Assert.Contains(failure.SqliteErrorCode, new[] { 5, 6 });
        await using var verificationScope = factory.Services.CreateAsyncScope();
        var verificationContext = verificationScope.ServiceProvider.GetRequiredService<RelayCoveDbContext>();
        Assert.True(await verificationContext.Attachments.AnyAsync(candidate =>
            candidate.Id == attachment.Id));
        Assert.True(File.Exists(path));
    }

    private static Attachment CreateAttachment(
        Guid id,
        Guid uploaderUserId,
        string storedFileName,
        DateTime createdAt) => new(
        id,
        uploaderUserId,
        "recovery.bin",
        storedFileName,
        "application/octet-stream",
        1,
        new string('b', Attachment.Sha256Length),
        createdAt);

    private static string CreateStoredFileName(Guid attachmentId) =>
        $"{attachmentId:N}_{new string('a', 32)}";
}
