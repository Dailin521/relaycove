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
    PasswordPolicy passwordPolicy,
    ServerClock clock,
    ILogger<AdminUserService> logger)
{
    public async Task<IReadOnlyList<AdminUserResponse>> ListUsersAsync(CancellationToken cancellationToken)
    {
        var users = await dbContext.Users
            .AsNoTracking()
            .OrderBy(user => user.NormalizedUserName)
            .ToArrayAsync(cancellationToken);
        return users.Select(ToResponse).ToArray();
    }

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
            if (!await IsActiveAdministratorAsync(actorUserId, cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                logger.LogWarning(
                    "User {ActorUserId} failed the in-transaction administrator recheck.",
                    actorUserId);
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
        return new AdminUserCreationResult(AdminUserCreationStatus.Created, ToResponse(user));
    }

    public async Task<AdminUserMutationResult> UpdateDisabledAsync(
        Guid actorUserId,
        Guid targetUserId,
        bool isDisabled,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        if (!await IsActiveAdministratorAsync(actorUserId, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new AdminUserMutationResult(AdminUserMutationStatus.ActorNotAdministrator);
        }

        var target = await dbContext.Users.SingleOrDefaultAsync(
            user => user.Id == targetUserId,
            cancellationToken);
        if (target is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new AdminUserMutationResult(AdminUserMutationStatus.UserNotFound);
        }

        if (target.RetiredAt is not null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new AdminUserMutationResult(AdminUserMutationStatus.UserRetired, ToResponse(target));
        }

        if (isDisabled && target.Id == actorUserId)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new AdminUserMutationResult(AdminUserMutationStatus.SelfActionForbidden);
        }

        if (isDisabled && target.IsAdmin && !target.IsDisabled &&
            !await HasAnotherActiveAdministratorAsync(target.Id, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new AdminUserMutationResult(AdminUserMutationStatus.LastActiveAdministrator);
        }

        if (!target.SetDisabled(isDisabled, clock.UtcNow))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new AdminUserMutationResult(AdminUserMutationStatus.Unchanged, ToResponse(target));
        }

        await RevokeRefreshTokensAsync(target.Id, clock.UtcNow, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation(
            "Administrator {ActorUserId} changed disabled state for target {TargetUserId}; disabled={IsDisabled}.",
            actorUserId,
            target.Id,
            target.IsDisabled);
        return new AdminUserMutationResult(
            AdminUserMutationStatus.Updated,
            ToResponse(target),
            MinimumAccessTokenVersion: target.AccessTokenVersion);
    }

    public async Task<AdminUserMutationResult> ResetPasswordAsync(
        Guid actorUserId,
        Guid targetUserId,
        string? password,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        if (!await IsActiveAdministratorAsync(actorUserId, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new AdminUserMutationResult(AdminUserMutationStatus.ActorNotAdministrator);
        }

        var target = await dbContext.Users.SingleOrDefaultAsync(
            user => user.Id == targetUserId,
            cancellationToken);
        if (target is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new AdminUserMutationResult(AdminUserMutationStatus.UserNotFound);
        }

        if (target.RetiredAt is not null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new AdminUserMutationResult(AdminUserMutationStatus.UserRetired, ToResponse(target));
        }

        var validationErrors = passwordPolicy.Validate(password, target.UserName, target.DisplayName);
        if (validationErrors.Length > 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new AdminUserMutationResult(
                AdminUserMutationStatus.ValidationFailed,
                ValidationErrors: new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["password"] = validationErrors,
                });
        }

        target.ResetPasswordHash(passwordService.HashPassword(target, password!), clock.UtcNow);
        await RevokeRefreshTokensAsync(target.Id, clock.UtcNow, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation(
            "Administrator {ActorUserId} reset the password for target {TargetUserId}.",
            actorUserId,
            target.Id);
        return new AdminUserMutationResult(
            AdminUserMutationStatus.PasswordReset,
            ToResponse(target),
            MinimumAccessTokenVersion: target.AccessTokenVersion);
    }

    public async Task<AdminUserMutationResult> RetireAsync(
        Guid actorUserId,
        Guid targetUserId,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        if (!await IsActiveAdministratorAsync(actorUserId, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new AdminUserMutationResult(AdminUserMutationStatus.ActorNotAdministrator);
        }

        var target = await dbContext.Users.SingleOrDefaultAsync(
            user => user.Id == targetUserId,
            cancellationToken);
        if (target is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new AdminUserMutationResult(AdminUserMutationStatus.UserNotFound);
        }

        if (target.Id == actorUserId)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new AdminUserMutationResult(AdminUserMutationStatus.SelfActionForbidden);
        }

        if (target.RetiredAt is not null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new AdminUserMutationResult(AdminUserMutationStatus.UserRetired, ToResponse(target));
        }

        if (target.IsAdmin && !target.IsDisabled &&
            !await HasAnotherActiveAdministratorAsync(target.Id, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new AdminUserMutationResult(AdminUserMutationStatus.LastActiveAdministrator);
        }

        target.Retire(clock.UtcNow);
        await RevokeRefreshTokensAsync(target.Id, clock.UtcNow, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation(
            "Administrator {ActorUserId} retired target {TargetUserId}.",
            actorUserId,
            target.Id);
        return new AdminUserMutationResult(
            AdminUserMutationStatus.Retired,
            ToResponse(target),
            MinimumAccessTokenVersion: target.AccessTokenVersion);
    }

    private Task<bool> IsActiveAdministratorAsync(Guid userId, CancellationToken cancellationToken) =>
        dbContext.Users.AnyAsync(
            user => user.Id == userId && user.IsAdmin && !user.IsDisabled && user.RetiredAt == null,
            cancellationToken);

    private Task<bool> HasAnotherActiveAdministratorAsync(Guid targetUserId, CancellationToken cancellationToken) =>
        dbContext.Users.AnyAsync(
            user => user.Id != targetUserId && user.IsAdmin && !user.IsDisabled && user.RetiredAt == null,
            cancellationToken);

    private Task<int> RevokeRefreshTokensAsync(Guid userId, DateTime revokedAt, CancellationToken cancellationToken) =>
        dbContext.RefreshTokens
            .Where(token => token.UserId == userId && token.RevokedAt == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(token => token.RevokedAt, (DateTime?)revokedAt),
                cancellationToken);

    private static AdminUserResponse ToResponse(User user) => new(
        user.Id,
        user.UserName,
        user.DisplayName,
        user.IsAdmin,
        user.IsDisabled,
        new DateTimeOffset(user.CreatedAt),
        user.RetiredAt is DateTime retiredAt ? new DateTimeOffset(retiredAt) : null);

    private static bool IsUniqueConstraintViolation(DbUpdateException exception) =>
        exception.InnerException is SqliteException
        {
            SqliteErrorCode: 19,
            SqliteExtendedErrorCode: 2067,
        };
}
