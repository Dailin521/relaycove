using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RelayCove.Server.Data;
using RelayCove.Server.Data.Entities;
using RelayCove.Server.Options;
using RelayCove.Server.Services;

namespace RelayCove.Server.Hosting;

public sealed class BootstrapAdminHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<BootstrapAdminOptions> bootstrapOptions,
    ILogger<BootstrapAdminHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var options = bootstrapOptions.Value;
        if (!options.Enabled)
        {
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<RelayCoveDbContext>();
        var userNameNormalizer = scope.ServiceProvider.GetRequiredService<UserNameNormalizer>();
        var passwordService = scope.ServiceProvider.GetRequiredService<PasswordService>();
        var clock = scope.ServiceProvider.GetRequiredService<ServerClock>();
        var now = clock.UtcNow;
        var admin = new User(
            Guid.NewGuid(),
            options.UserName,
            options.DisplayName,
            "pending-password-hash",
            isAdmin: true,
            isDisabled: false,
            now,
            userNameNormalizer);
        admin.SetPasswordHash(passwordService.HashPassword(admin, options.Password), now);

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        if (await dbContext.Users.AnyAsync(cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            logger.LogWarning("Bootstrap administrator was not created because the Users table is not empty.");
            return;
        }

        dbContext.Users.Add(admin);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("Bootstrap administrator {UserId} was created. Remove bootstrap credentials before restart.", admin.Id);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
