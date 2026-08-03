using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using RelayCove.Server.Data;
using RelayCove.Server.Data.Entities;
using RelayCove.Server.Services;

namespace RelayCove.Server.Tests.Data;

public sealed class RelayCoveDbContextTests
{
    private static readonly DateTime CreatedAt = new(2026, 8, 3, 4, 0, 0, DateTimeKind.Utc);
    private readonly UserNameNormalizer userNameNormalizer = new();
    private readonly RefreshTokenHasher refreshTokenHasher = new();

    [Fact]
    public async Task Migration_WhenAppliedAndRolledBack_HasExpectedSchemaWithoutModelDrift()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            await using var context = CreateContext(databasePath);
            await context.Database.MigrateAsync();

            Assert.False(context.Database.HasPendingModelChanges());
            Assert.Equal(
                ["RefreshTokens", "Users"],
                await ReadStringsAsync(databasePath, "SELECT name FROM sqlite_master WHERE type = 'table' AND name IN ('Users', 'RefreshTokens') ORDER BY name;"));
            Assert.Equal(
                ["Id", "UserName", "NormalizedUserName", "DisplayName", "AvatarAttachmentId", "PasswordHash", "IsAdmin", "IsDisabled", "CreatedAt", "UpdatedAt", "LastLoginAt", "LastOnlineAt"],
                await ReadStringsAsync(databasePath, "SELECT name FROM pragma_table_info('Users') ORDER BY cid;"));
            Assert.Equal(
                ["Id", "UserId", "TokenHash", "DeviceName", "CreatedAt", "ExpiresAt", "RevokedAt"],
                await ReadStringsAsync(databasePath, "SELECT name FROM pragma_table_info('RefreshTokens') ORDER BY cid;"));

            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync(Migration.InitialDatabase);

            Assert.Empty(await ReadStringsAsync(
                databasePath,
                "SELECT name FROM sqlite_master WHERE type = 'table' AND name IN ('Users', 'RefreshTokens') ORDER BY name;"));
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task Persistence_WhenRoundTripped_PreservesLowerGuidUtcKindAndExpiryOrdering()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            var userId = Guid.Parse("83B65814-9460-4B8D-B59B-E7A5C3857D32");
            var expiredId = Guid.Parse("51AFC9F1-1DDD-44FD-B1DF-7904503AC048");
            var futureId = Guid.Parse("7507DD54-2A63-485F-867A-269583B18CA2");
            await using (var context = CreateContext(databasePath))
            {
                await context.Database.MigrateAsync();
                var user = CreateUser(userId, "Alice");
                context.Add(user);
                context.Add(CreateToken(expiredId, userId, 1, CreatedAt.AddHours(1)));
                context.Add(CreateToken(futureId, userId, 2, CreatedAt.AddDays(2)));
                await context.SaveChangesAsync();
            }

            Assert.Equal(
                userId.ToString("D").ToLowerInvariant(),
                Assert.Single(await ReadStringsAsync(databasePath, "SELECT Id FROM Users;")));
            Assert.Equal(
                "2026-08-03T04:00:00.000Z",
                Assert.Single(await ReadStringsAsync(databasePath, "SELECT CreatedAt FROM Users;")));

            await using var verificationContext = CreateContext(databasePath);
            var userRoundTrip = await verificationContext.Users.AsNoTracking().SingleAsync();
            var expiredTokenIds = await verificationContext.RefreshTokens
                .Where(token => token.ExpiresAt <= CreatedAt.AddHours(2))
                .Select(token => token.Id)
                .ToArrayAsync();

            Assert.Equal(DateTimeKind.Utc, userRoundTrip.CreatedAt.Kind);
            Assert.Equal([expiredId], expiredTokenIds);
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task SaveChanges_WhenUtcInvariantIsViolated_ThrowsBeforeWriting()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            await using var context = CreateContext(databasePath);
            await context.Database.MigrateAsync();
            var user = CreateUser(Guid.NewGuid(), "alice");
            context.Add(user);
            await context.SaveChangesAsync();
            context.Entry(user).Property(item => item.UpdatedAt).CurrentValue =
                DateTime.SpecifyKind(CreatedAt.AddMinutes(1), DateTimeKind.Local);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());

            Assert.Contains("User.UpdatedAt", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task Constraints_WhenIdentityOrTokenDataIsInvalid_RejectDuplicatesAndBadHashes()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            await using var context = CreateContext(databasePath);
            await context.Database.MigrateAsync();
            context.Add(CreateUser(Guid.NewGuid(), "Alice"));
            context.Add(CreateUser(Guid.NewGuid(), "alice"));

            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
            context.ChangeTracker.Clear();

            var persistedUser = CreateUser(Guid.NewGuid(), "bob");
            context.Add(persistedUser);
            await context.SaveChangesAsync();

            var exception = await Assert.ThrowsAsync<SqliteException>(() => context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO RefreshTokens (Id, UserId, TokenHash, DeviceName, CreatedAt, ExpiresAt, RevokedAt)
                VALUES ({Guid.NewGuid().ToString("D").ToLowerInvariant()}, {persistedUser.Id.ToString("D").ToLowerInvariant()}, {"short"}, {"device"}, {"2026-08-03T04:00:00.000Z"}, {"2026-08-04T04:00:00.000Z"}, NULL);
                """));

            Assert.Equal(19, exception.SqliteErrorCode);
            Assert.Equal(275, exception.SqliteExtendedErrorCode);
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task ForeignKey_WhenUserIsMissingOrDeleted_RejectsOrCascadesRefreshTokens()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            await using var context = CreateContext(databasePath);
            await context.Database.MigrateAsync();
            context.Add(CreateToken(Guid.NewGuid(), Guid.NewGuid(), 3, CreatedAt.AddDays(1)));

            var foreignKeyException = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
            var sqliteException = Assert.IsType<SqliteException>(foreignKeyException.InnerException);
            Assert.Equal(787, sqliteException.SqliteExtendedErrorCode);
            context.ChangeTracker.Clear();

            var user = CreateUser(Guid.NewGuid(), "carol");
            context.Add(user);
            context.Add(CreateToken(Guid.NewGuid(), user.Id, 4, CreatedAt.AddDays(1)));
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();

            var persistedUser = await context.Users.SingleAsync();
            context.Remove(persistedUser);
            await context.SaveChangesAsync();

            Assert.Empty(await context.RefreshTokens.AsNoTracking().ToArrayAsync());
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    private User CreateUser(Guid id, string userName) => new(
        id,
        userName,
        userName,
        "password-hash",
        isAdmin: false,
        isDisabled: false,
        CreatedAt,
        userNameNormalizer);

    private RefreshToken CreateToken(Guid id, Guid userId, byte seed, DateTime expiresAt)
    {
        var bytes = Enumerable.Repeat(seed, RefreshTokenHasher.RawTokenByteLength).ToArray();
        var rawToken = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(bytes);

        return new RefreshToken(id, userId, refreshTokenHasher.HashToken(rawToken), "workstation", CreatedAt, expiresAt);
    }

    private static RelayCoveDbContext CreateContext(string databasePath)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            ForeignKeys = true,
            DefaultTimeout = 5,
            Pooling = false,
        }.ToString();
        var options = new DbContextOptionsBuilder<RelayCoveDbContext>()
            .UseSqlite(connectionString)
            .Options;

        return new RelayCoveDbContext(options);
    }

    private static string CreateDatabasePath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RelayCove.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "relaycove-tests.db");
    }

    private static void DeleteDatabaseFiles(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath)!;
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<string[]> ReadStringsAsync(string databasePath, string commandText)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            ForeignKeys = true,
            DefaultTimeout = 5,
            Pooling = false,
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await using DbDataReader reader = await command.ExecuteReaderAsync();
        var values = new List<string>();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values.ToArray();
    }
}
