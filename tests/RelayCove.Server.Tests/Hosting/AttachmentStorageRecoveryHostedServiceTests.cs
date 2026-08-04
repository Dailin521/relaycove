using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RelayCove.Server.Data;
using RelayCove.Server.Data.Entities;
using RelayCove.Server.Hosting;
using RelayCove.Server.Tests.Infrastructure;

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
            dbContext.Attachments.Add(CreateAttachment(attachmentId, userId, storedFileName));
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
            dbContext.Attachments.Add(CreateAttachment(attachmentId, userId, storedFileName));
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

    private static Attachment CreateAttachment(Guid id, Guid uploaderUserId, string storedFileName) => new(
        id,
        uploaderUserId,
        "recovery.bin",
        storedFileName,
        "application/octet-stream",
        1,
        new string('b', Attachment.Sha256Length),
        new DateTime(2026, 8, 4, 1, 0, 0, DateTimeKind.Utc));
}
