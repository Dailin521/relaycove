using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RelayCove.Server.Data;
using RelayCove.Server.Data.Entities;
using RelayCove.Shared.Admin;

namespace RelayCove.Server.Services;

public sealed class AdminUserService(
    RelayCoveDbContext dbContext,
    UserNameNormalizer userNameNormalizer,
    PasswordService passwordService,
    ServerClock clock,
    ILogger<AdminUserService> logger)
{
    public async Task<AdminUserCreationResult> CreateUserAsync(
        Guid actorUserId,
        CreateUserRequest request,
        CancellationToken cancellationToken)
    {
        var normalizedUserName = userNameNormalizer.Normalize(request.UserName);
        var now = clock.UtcNow;
        var user = new User(
            Guid.NewGuid(),
            request.UserName,
            request.DisplayName,
            "pending-password-hash",
            request.IsAdmin,
            isDisabled: false,
            now,
            userNameNormalizer);
        user.SetPasswordHash(passwordService.HashPassword(user, request.Password), now);

        try
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);
            var actorIsAdministrator = await dbContext.Users.AnyAsync(
                actor => actor.Id == actorUserId && !actor.IsDisabled && actor.IsAdmin,
                cancellationToken);
            if (!actorIsAdministrator)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new AdminUserCreationResult(AdminUserCreationStatus.ActorNotAdministrator);
            }

            if (await dbContext.Users.AnyAsync(
                    existing => existing.NormalizedUserName == normalizedUserName,
                    cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                logger.LogInformation(
                    "Administrator {ActorUserId} attempted to create an existing normalized user name.",
                    actorUserId);
                return new AdminUserCreationResult(AdminUserCreationStatus.UserNameAlreadyExists);
            }

            dbContext.Users.Add(user);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueConstraintViolation(exception))
        {
            logger.LogInformation(
                "Administrator {ActorUserId} lost a concurrent user-name creation race.",
                actorUserId);
            return new AdminUserCreationResult(AdminUserCreationStatus.UserNameAlreadyExists);
        }

        logger.LogInformation(
            "Administrator {ActorUserId} created user {CreatedUserId}; administrator={IsAdmin}.",
            actorUserId,
            user.Id,
            user.IsAdmin);
        return new AdminUserCreationResult(
            AdminUserCreationStatus.Created,
            new AdminUserResponse(
                user.Id,
                user.UserName,
                user.DisplayName,
                user.IsAdmin,
                user.IsDisabled,
                new DateTimeOffset(user.CreatedAt)));
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException exception) =>
        exception.InnerException is SqliteException
        {
            SqliteErrorCode: 19,
            SqliteExtendedErrorCode: 2067,
        };
}
