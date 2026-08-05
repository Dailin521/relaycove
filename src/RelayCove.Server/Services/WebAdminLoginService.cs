using System.Data;
using Microsoft.EntityFrameworkCore;
using RelayCove.Server.Data;

namespace RelayCove.Server.Services;

public sealed class WebAdminLoginService(
    RelayCoveDbContext dbContext,
    UserNameNormalizer userNameNormalizer,
    PasswordService passwordService,
    ServerClock clock)
{
    public async Task<WebAdminLoginResult?> LoginAsync(
        string? userName,
        string? password,
        CancellationToken cancellationToken)
    {
        if (!userNameNormalizer.TryNormalize(userName, out var normalizedUserName) ||
            string.IsNullOrEmpty(password) ||
            password.Length > 1_024)
        {
            passwordService.VerifyDummyPassword(string.Empty);
            return null;
        }

        var user = await dbContext.Users.SingleOrDefaultAsync(
            candidate => candidate.NormalizedUserName == normalizedUserName,
            cancellationToken);
        if (user is null)
        {
            passwordService.VerifyDummyPassword(password);
            return null;
        }

        var verifiedHash = user.PasswordHash;
        var verification = passwordService.VerifyPassword(user, verifiedHash, password);
        if (verification is PasswordVerificationOutcome.Failed || !IsActiveAdministrator(user))
        {
            return null;
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        await dbContext.Entry(user).ReloadAsync(cancellationToken);
        if (dbContext.Entry(user).State is EntityState.Detached || !IsActiveAdministrator(user))
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        if (!string.Equals(verifiedHash, user.PasswordHash, StringComparison.Ordinal))
        {
            verification = passwordService.VerifyPassword(user, user.PasswordHash, password);
            if (verification is PasswordVerificationOutcome.Failed)
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }
        }

        var now = clock.UtcNow;
        user.RecordLogin(now);
        if (verification is PasswordVerificationOutcome.SuccessRehashNeeded)
        {
            user.SetPasswordHash(passwordService.HashPassword(user, password), now);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new WebAdminLoginResult(user.Id, user.DisplayName, user.AccessTokenVersion);
    }

    private static bool IsActiveAdministrator(Data.Entities.User user) =>
        user.IsAdmin && !user.IsDisabled && user.RetiredAt is null;
}
