using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RelayCove.Server.Data;
using RelayCove.Server.Services;
using RelayCove.Server.Tests.Infrastructure;

namespace RelayCove.Server.Tests.Services;

public sealed class AttachmentCommandServiceTests
{
    [Fact]
    public async Task CommitAsync_WhenFinalTargetExists_RollsBackMetadataAndCleansStagingWithoutOverwrite()
    {
        using var factory = new RelayCoveWebApplicationFactory();
        await factory.InitializeDatabaseAsync();
        var userId = await factory.CreateUserAsync(
            $"attachment-conflict-{Guid.NewGuid():N}",
            "a secure attachment conflict phrase");
        await using var commandScope = factory.Services.CreateAsyncScope();
        var storagePaths = commandScope.ServiceProvider.GetRequiredService<AttachmentStoragePaths>();
        await using var staged = storagePaths.CreateStagedUpload(
            "conflict.bin",
            "application/octet-stream",
            NullLogger.Instance);
        await File.WriteAllBytesAsync(staged.StagingPath, [1]);
        staged.Complete(1, new string('a', 64));
        await File.WriteAllBytesAsync(staged.FinalPath, [2]);
        var commandService = commandScope.ServiceProvider.GetRequiredService<AttachmentCommandService>();

        await Assert.ThrowsAsync<IOException>(() =>
            commandService.CommitAsync(userId, staged, CancellationToken.None));
        await staged.DisposeAsync();

        await using var verificationScope = factory.Services.CreateAsyncScope();
        var dbContext = verificationScope.ServiceProvider.GetRequiredService<RelayCoveDbContext>();
        Assert.False(await dbContext.Attachments.AnyAsync(attachment => attachment.Id == staged.Id));
        Assert.False(File.Exists(staged.StagingPath));
        Assert.Equal([2], await File.ReadAllBytesAsync(staged.FinalPath));
    }
}
