using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RelayCove.Server.Data;
using RelayCove.Server.Data.Entities;
using RelayCove.Server.Services;
using RelayCove.Shared.Conversations;
using RelayCove.Shared.Messages;

namespace RelayCove.Server.Tests.Data;

public sealed class AttachmentStorageTests
{
    private static readonly DateTime CreatedAt = new(2026, 8, 4, 0, 45, 0, DateTimeKind.Utc);
    private readonly UserNameNormalizer userNameNormalizer = new();

    [Fact]
    public async Task Persistence_WhenRoundTripped_PreservesCanonicalMetadataAndRelations()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            var user = CreateUser();
            var attachmentId = Guid.Parse("B54DE111-F3BB-4C3D-B5B7-C01C5A93DA2E");
            await using (var context = CreateContext(databasePath))
            {
                await context.Database.MigrateAsync();
                context.Users.Add(user);
                context.Attachments.Add(CreateAttachment(attachmentId, user.Id));
                await context.SaveChangesAsync();
            }

            await using var verificationContext = CreateContext(databasePath);
            var stored = await verificationContext.Attachments
                .AsNoTracking()
                .Include(attachment => attachment.UploaderUser)
                .SingleAsync();
            Assert.Equal(attachmentId, stored.Id);
            Assert.Equal(user.Id, stored.UploaderUserId);
            Assert.Equal(user.UserName, stored.UploaderUser.UserName);
            Assert.Null(stored.MessageId);
            Assert.Equal(DateTimeKind.Utc, stored.CreatedAt.Kind);
            Assert.Equal(0, stored.CreatedAt.Ticks % TimeSpan.TicksPerMillisecond);

            await using var connection = new SqliteConnection(CreateConnectionString(databasePath));
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT Id, UploaderUserId, CreatedAt FROM Attachments;";
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(attachmentId.ToString("D").ToLowerInvariant(), reader.GetString(0));
            Assert.Equal(user.Id.ToString("D").ToLowerInvariant(), reader.GetString(1));
            Assert.Equal("2026-08-04T00:45:00.000Z", reader.GetString(2));
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task Constraints_WhenStoredMetadataOrRelationsAreInvalid_RejectRows()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            var user = CreateUser();
            await using var context = CreateContext(databasePath);
            await context.Database.MigrateAsync();
            context.Users.Add(user);
            await context.SaveChangesAsync();
            var id = Guid.NewGuid();
            var canonicalStoredName = $"{id:N}_{new string('a', 32)}";

            await AssertConstraintAsync(context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO Attachments (Id, MessageId, UploaderUserId, OriginalFileName, StoredFileName, ContentType, Size, Sha256, CreatedAt)
                VALUES ({id.ToString("D")}, NULL, {user.Id.ToString("D")}, {"file.bin"}, {$"{id:N}_{new string('_', 32)}"}, {"application/octet-stream"}, {1L}, {new string('b', 64)}, {"2026-08-04T00:45:00.000Z"});
                """), 275);
            await AssertConstraintAsync(context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO Attachments (Id, MessageId, UploaderUserId, OriginalFileName, StoredFileName, ContentType, Size, Sha256, CreatedAt)
                VALUES ({id.ToString("D")}, NULL, {user.Id.ToString("D")}, {"file.bin"}, {canonicalStoredName}, {"application/octet-stream"}, {0L}, {new string('b', 64)}, {"2026-08-04T00:45:00.000Z"});
                """), 275);
            await AssertConstraintAsync(context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO Attachments (Id, MessageId, UploaderUserId, OriginalFileName, StoredFileName, ContentType, Size, Sha256, CreatedAt)
                VALUES ({id.ToString("D")}, NULL, {user.Id.ToString("D")}, {"file.bin"}, {canonicalStoredName}, {"application/octet-stream"}, {1L}, {new string('G', 64)}, {"2026-08-04T00:45:00.000Z"});
                """), 275);
            await AssertConstraintAsync(context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO Attachments (Id, MessageId, UploaderUserId, OriginalFileName, StoredFileName, ContentType, Size, Sha256, CreatedAt)
                VALUES ({id.ToString("D")}, NULL, {Guid.NewGuid().ToString("D")}, {"file.bin"}, {canonicalStoredName}, {"application/octet-stream"}, {1L}, {new string('b', 64)}, {"2026-08-04T00:45:00.000Z"});
                """), 787);
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task ForeignKeys_WhenUploaderOrConversationIsDeleted_RestrictUploaderAndCascadeAttachedRow()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            var uploader = CreateUser();
            var conversation = Conversation.CreateChannel(
                Guid.NewGuid(), ConversationType.PublicChannel, "Attachments", uploader.Id, CreatedAt);
            await using var context = CreateContext(databasePath);
            await context.Database.MigrateAsync();
            context.AddRange(uploader, conversation);
            await context.SaveChangesAsync();
            var message = new Message(
                Guid.NewGuid(), conversation.Id, uploader.Id, MessageType.Text, "attachment owner", null, CreatedAt);
            context.Messages.Add(message);
            await context.SaveChangesAsync();
            var attachment = CreateAttachment(Guid.NewGuid(), uploader.Id);
            context.Attachments.Add(attachment);
            await context.SaveChangesAsync();
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE Attachments SET MessageId = {message.Id} WHERE Id = {attachment.Id.ToString("D")};");

            var uploaderDelete = await Assert.ThrowsAsync<SqliteException>(() =>
                context.Database.ExecuteSqlInterpolatedAsync(
                    $"DELETE FROM Users WHERE Id = {uploader.Id.ToString("D")};"));
            Assert.Equal(19, uploaderDelete.SqliteErrorCode);

            await context.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM Conversations WHERE Id = {conversation.Id.ToString("D")};");
            Assert.Empty(await context.Attachments.AsNoTracking().ToArrayAsync());
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    private User CreateUser() => new(
        Guid.NewGuid(),
        $"attachment-{Guid.NewGuid():N}",
        "Attachment User",
        "password-hash",
        isAdmin: false,
        isDisabled: false,
        CreatedAt,
        userNameNormalizer);

    private static Attachment CreateAttachment(Guid id, Guid uploaderUserId) => new(
        id,
        uploaderUserId,
        "file.bin",
        $"{id:N}_{new string('a', 32)}",
        "application/octet-stream",
        1,
        new string('b', Attachment.Sha256Length),
        CreatedAt);

    private static RelayCoveDbContext CreateContext(string databasePath) => new(
        new DbContextOptionsBuilder<RelayCoveDbContext>()
            .UseSqlite(CreateConnectionString(databasePath))
            .Options);

    private static string CreateConnectionString(string databasePath) => new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        ForeignKeys = true,
        DefaultTimeout = 5,
        Pooling = false,
    }.ToString();

    private static string CreateDatabasePath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RelayCove.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "relaycove-attachment-tests.db");
    }

    private static void DeleteDatabaseFiles(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath)!;
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task AssertConstraintAsync(Task operation, int extendedErrorCode)
    {
        var exception = await Assert.ThrowsAsync<SqliteException>(() => operation);
        Assert.Equal(19, exception.SqliteErrorCode);
        Assert.Equal(extendedErrorCode, exception.SqliteExtendedErrorCode);
    }
}
