using Microsoft.EntityFrameworkCore;
using RelayCove.Server.Data.Entities;
using RelayCove.Server.Services;

namespace RelayCove.Server.Data;

public sealed class RelayCoveDbContext(DbContextOptions<RelayCoveDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ValidateUtcDateTimes();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        ValidateUtcDateTimes();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureUser(modelBuilder.Entity<User>());
        ConfigureRefreshToken(modelBuilder.Entity<RefreshToken>());
    }

    private static void ConfigureUser(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<User> entity)
    {
        entity.ToTable("Users", table =>
        {
            table.HasCheckConstraint("CK_Users_Id_Format", GuidTextCheck("Id"));
            table.HasCheckConstraint("CK_Users_UserName_Format", "length(\"UserName\") BETWEEN 3 AND 64 AND \"UserName\" NOT GLOB '*[^A-Za-z0-9._-]*'");
            table.HasCheckConstraint("CK_Users_NormalizedUserName_Format", "length(\"NormalizedUserName\") BETWEEN 3 AND 64 AND \"NormalizedUserName\" NOT GLOB '*[^A-Z0-9._-]*' AND upper(\"NormalizedUserName\") = \"NormalizedUserName\"");
            table.HasCheckConstraint("CK_Users_DisplayName_Length", "length(\"DisplayName\") BETWEEN 1 AND 100");
            table.HasCheckConstraint("CK_Users_PasswordHash_NotEmpty", "length(\"PasswordHash\") > 0");
            table.HasCheckConstraint("CK_Users_IsAdmin_Boolean", "\"IsAdmin\" IN (0, 1)");
            table.HasCheckConstraint("CK_Users_IsDisabled_Boolean", "\"IsDisabled\" IN (0, 1)");
            table.HasCheckConstraint("CK_Users_CreatedAt_Format", UtcTextCheck("CreatedAt"));
            table.HasCheckConstraint("CK_Users_UpdatedAt_Format", UtcTextCheck("UpdatedAt"));
            table.HasCheckConstraint("CK_Users_LastLoginAt_Format", NullableUtcTextCheck("LastLoginAt"));
            table.HasCheckConstraint("CK_Users_LastOnlineAt_Format", NullableUtcTextCheck("LastOnlineAt"));
        });

        entity.HasKey(user => user.Id);
        entity.Property(user => user.Id)
            .HasConversion(SqliteValueConverters.GuidToString)
            .ValueGeneratedNever();
        entity.Property(user => user.UserName).HasMaxLength(UserNameNormalizer.MaximumLength).IsRequired();
        entity.Property(user => user.NormalizedUserName).HasMaxLength(UserNameNormalizer.MaximumLength).IsRequired();
        entity.Property(user => user.DisplayName).HasMaxLength(100).IsRequired();
        entity.Property(user => user.AvatarAttachmentId).HasConversion(SqliteValueConverters.GuidToString);
        entity.Property(user => user.PasswordHash).IsRequired();
        entity.Property(user => user.CreatedAt).HasConversion(SqliteValueConverters.UtcDateTimeToString);
        entity.Property(user => user.UpdatedAt).HasConversion(SqliteValueConverters.UtcDateTimeToString);
        entity.Property(user => user.LastLoginAt).HasConversion(SqliteValueConverters.UtcDateTimeToString);
        entity.Property(user => user.LastOnlineAt).HasConversion(SqliteValueConverters.UtcDateTimeToString);
        entity.HasIndex(user => user.UserName).IsUnique();
        entity.HasIndex(user => user.NormalizedUserName).IsUnique();
    }

    private static void ConfigureRefreshToken(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<RefreshToken> entity)
    {
        entity.ToTable("RefreshTokens", table =>
        {
            table.HasCheckConstraint("CK_RefreshTokens_Id_Format", GuidTextCheck("Id"));
            table.HasCheckConstraint("CK_RefreshTokens_UserId_Format", GuidTextCheck("UserId"));
            table.HasCheckConstraint("CK_RefreshTokens_TokenHash_Format", "length(\"TokenHash\") = 43 AND \"TokenHash\" NOT GLOB '*[^A-Za-z0-9_-]*'");
            table.HasCheckConstraint("CK_RefreshTokens_DeviceName_Length", "length(\"DeviceName\") BETWEEN 1 AND 128");
            table.HasCheckConstraint("CK_RefreshTokens_CreatedAt_Format", UtcTextCheck("CreatedAt"));
            table.HasCheckConstraint("CK_RefreshTokens_ExpiresAt_Format", UtcTextCheck("ExpiresAt"));
            table.HasCheckConstraint("CK_RefreshTokens_RevokedAt_Format", NullableUtcTextCheck("RevokedAt"));
            table.HasCheckConstraint("CK_RefreshTokens_Expiry_Order", "\"ExpiresAt\" > \"CreatedAt\"");
        });

        entity.HasKey(token => token.Id);
        entity.Property(token => token.Id)
            .HasConversion(SqliteValueConverters.GuidToString)
            .ValueGeneratedNever();
        entity.Property(token => token.UserId).HasConversion(SqliteValueConverters.GuidToString);
        entity.Property(token => token.TokenHash).HasMaxLength(RefreshTokenHasher.EncodedHashLength).IsRequired();
        entity.Property(token => token.DeviceName).HasMaxLength(128).IsRequired();
        entity.Property(token => token.CreatedAt).HasConversion(SqliteValueConverters.UtcDateTimeToString);
        entity.Property(token => token.ExpiresAt).HasConversion(SqliteValueConverters.UtcDateTimeToString);
        entity.Property(token => token.RevokedAt).HasConversion(SqliteValueConverters.UtcDateTimeToString);
        entity.HasIndex(token => token.TokenHash).IsUnique();
        entity.HasOne(token => token.User)
            .WithMany(user => user.RefreshTokens)
            .HasForeignKey(token => token.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    private void ValidateUtcDateTimes()
    {
        foreach (var entry in ChangeTracker.Entries().Where(entry => entry.State is EntityState.Added or EntityState.Modified))
        {
            foreach (var property in entry.Properties)
            {
                if (property.CurrentValue is DateTime value && value.Kind != DateTimeKind.Utc)
                {
                    throw new InvalidOperationException(
                        $"{entry.Metadata.ClrType.Name}.{property.Metadata.Name} must use DateTimeKind.Utc.");
                }
            }
        }
    }

    private static string GuidTextCheck(string columnName) =>
        $"length(\"{columnName}\") = 36 AND lower(\"{columnName}\") = \"{columnName}\" AND " +
        $"substr(\"{columnName}\", 9, 1) = '-' AND substr(\"{columnName}\", 14, 1) = '-' AND " +
        $"substr(\"{columnName}\", 19, 1) = '-' AND substr(\"{columnName}\", 24, 1) = '-'";

    private static string UtcTextCheck(string columnName) =>
        $"length(\"{columnName}\") = 24 AND substr(\"{columnName}\", 5, 1) = '-' AND " +
        $"substr(\"{columnName}\", 8, 1) = '-' AND substr(\"{columnName}\", 11, 1) = 'T' AND " +
        $"substr(\"{columnName}\", 14, 1) = ':' AND substr(\"{columnName}\", 17, 1) = ':' AND " +
        $"substr(\"{columnName}\", 20, 1) = '.' AND substr(\"{columnName}\", 24, 1) = 'Z'";

    private static string NullableUtcTextCheck(string columnName) =>
        $"\"{columnName}\" IS NULL OR ({UtcTextCheck(columnName)})";
}
