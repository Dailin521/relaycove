using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RelayCove.Server.Data;
using RelayCove.Server.Data.Entities;
using RelayCove.Server.Options;
using RelayCove.Shared.Auth;

namespace RelayCove.Server.Services;

public sealed class AuthenticationSessionService(
    RelayCoveDbContext dbContext,
    UserNameNormalizer userNameNormalizer,
    PasswordService passwordService,
    RefreshTokenHasher refreshTokenHasher,
    AccessTokenService accessTokenService,
    ServerClock clock,
    IOptions<AuthenticationOptions> authenticationOptions)
{
    private readonly AuthenticationOptions options = authenticationOptions.Value;

    public async Task<LoginResponse?> LoginAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        if (!userNameNormalizer.TryNormalize(request.UserName, out var normalizedUserName))
        {
            passwordService.VerifyDummyPassword(request.Password);
            return null;
        }

        var user = await dbContext.Users.SingleOrDefaultAsync(
            candidate => candidate.NormalizedUserName == normalizedUserName,
            cancellationToken);
        if (user is null)
        {
            passwordService.VerifyDummyPassword(request.Password);
            return null;
        }

        var verifiedHash = user.PasswordHash;
        var verification = passwordService.VerifyPassword(user, verifiedHash, request.Password);
        if (verification is PasswordVerificationOutcome.Failed || user.IsDisabled)
        {
            return null;
        }

        try
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);
            await dbContext.Entry(user).ReloadAsync(cancellationToken);
            if (dbContext.Entry(user).State is EntityState.Detached || user.IsDisabled)
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }

            if (!string.Equals(verifiedHash, user.PasswordHash, StringComparison.Ordinal))
            {
                verification = passwordService.VerifyPassword(user, user.PasswordHash, request.Password);
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
                user.SetPasswordHash(passwordService.HashPassword(user, request.Password), now);
            }

            var response = CreateSession(user, request.DeviceName, now);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return response;
        }
        catch (Exception exception) when (IsBusy(exception))
        {
            throw new AuthenticationStorageUnavailableException(exception);
        }
    }

    public async Task<LoginResponse?> RefreshAsync(string? rawToken, CancellationToken cancellationToken)
    {
        if (!refreshTokenHasher.TryHashToken(rawToken, out var tokenHash))
        {
            return null;
        }

        try
        {
            var now = clock.UtcNow;
            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);
            var affectedRows = await dbContext.RefreshTokens
                .Where(token =>
                    token.TokenHash == tokenHash.Value &&
                    token.RevokedAt == null &&
                    token.ExpiresAt > now)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(token => token.RevokedAt, (DateTime?)now),
                    cancellationToken);
            if (affectedRows != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }

            var existingToken = await dbContext.RefreshTokens
                .Include(token => token.User)
                .SingleAsync(token => token.TokenHash == tokenHash.Value, cancellationToken);
            if (existingToken.User.IsDisabled)
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }

            existingToken.User.RecordActivity(now);
            var response = CreateSession(existingToken.User, existingToken.DeviceName, now);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return response;
        }
        catch (Exception exception) when (IsBusy(exception))
        {
            throw new AuthenticationStorageUnavailableException(exception);
        }
    }

    public async Task LogoutAsync(string? rawToken, CancellationToken cancellationToken)
    {
        if (!refreshTokenHasher.TryHashToken(rawToken, out var tokenHash))
        {
            return;
        }

        try
        {
            var now = clock.UtcNow;
            await dbContext.RefreshTokens
                .Where(token => token.TokenHash == tokenHash.Value && token.RevokedAt == null)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(token => token.RevokedAt, (DateTime?)now),
                    cancellationToken);
        }
        catch (Exception exception) when (IsBusy(exception))
        {
            throw new AuthenticationStorageUnavailableException(exception);
        }
    }

    private LoginResponse CreateSession(User user, string deviceName, DateTime now)
    {
        var rawRefreshToken = refreshTokenHasher.GenerateToken();
        var refreshTokenHash = refreshTokenHasher.HashToken(rawRefreshToken);
        dbContext.RefreshTokens.Add(new RefreshToken(
            Guid.NewGuid(),
            user.Id,
            refreshTokenHash,
            deviceName,
            now,
            now.AddDays(options.RefreshTokenDays)));
        var accessToken = accessTokenService.CreateToken(user);

        return new LoginResponse(
            user.Id,
            user.DisplayName,
            accessToken.Token,
            rawRefreshToken.Reveal(),
            new DateTimeOffset(accessToken.ExpiresAt),
            options.ServerVersion,
            options.MinimumSupportedClientVersion,
            user.AccessTokenVersion);
    }

    private static bool IsBusy(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException!)
        {
            if (current is SqliteException { SqliteErrorCode: 5 or 6 })
            {
                return true;
            }
        }

        return false;
    }
}
