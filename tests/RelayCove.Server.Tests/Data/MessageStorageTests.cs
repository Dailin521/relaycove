using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RelayCove.Server.Data;
using RelayCove.Server.Data.Entities;
using RelayCove.Server.Services;
using RelayCove.Shared.Conversations;
using RelayCove.Shared.Messages;

namespace RelayCove.Server.Tests.Data;

public sealed class MessageStorageTests
{
    private static readonly DateTime CreatedAt = new(2026, 8, 3, 9, 0, 0, DateTimeKind.Utc);
    private readonly UserNameNormalizer userNameNormalizer = new();

    [Fact]
    public async Task MessagePersistence_WhenRoundTripped_PreservesReplyMentionUtcAndAutoincrement()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            long deletedMessageId;
            long replacementMessageId;
            var sender = CreateUser("sender");
            var mentioned = CreateUser("mentioned");
            var conversation = Conversation.CreateChannel(
                Guid.NewGuid(), ConversationType.PublicChannel, "General", sender.Id, CreatedAt);
            await using (var context = CreateContext(databasePath))
            {
                await context.Database.MigrateAsync();
                context.AddRange(sender, mentioned, conversation);
                await context.SaveChangesAsync();

                var first = new Message(
                    Guid.NewGuid(), conversation.Id, sender.Id, MessageType.Text, "  exact 🛰️  ", null, CreatedAt);
                first.AddMention(mentioned.Id);
                context.Messages.Add(first);
                await context.SaveChangesAsync();

                var reply = new Message(
                    Guid.NewGuid(), conversation.Id, mentioned.Id, MessageType.Text, "reply", first.Id,
                    CreatedAt.AddMinutes(1));
                context.Messages.Add(reply);
                await context.SaveChangesAsync();
                deletedMessageId = reply.Id;
                context.Messages.Remove(reply);
                await context.SaveChangesAsync();

                var replacement = new Message(
                    Guid.NewGuid(), conversation.Id, sender.Id, MessageType.Text, "replacement", first.Id,
                    CreatedAt.AddMinutes(2));
                context.Messages.Add(replacement);
                await context.SaveChangesAsync();
                replacementMessageId = replacement.Id;
            }

            Assert.True(replacementMessageId > deletedMessageId);
            await using var verificationContext = CreateContext(databasePath);
            var stored = await verificationContext.Messages
                .AsNoTracking()
                .Include(message => message.Mentions)
                .OrderBy(message => message.Id)
                .ToArrayAsync();
            Assert.Equal(2, stored.Length);
            Assert.Equal("  exact 🛰️  ", stored[0].Content);
            Assert.Equal(mentioned.Id, Assert.Single(stored[0].Mentions).MentionedUserId);
            Assert.Equal(stored[0].Id, stored[1].ReplyToMessageId);
            Assert.All(stored, message => Assert.Equal(DateTimeKind.Utc, message.CreatedAt.Kind));

            await using var connection = new SqliteConnection(CreateConnectionString(databasePath));
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'Messages';";
            var createSql = Assert.IsType<string>(await command.ExecuteScalarAsync());
            Assert.Contains("AUTOINCREMENT", createSql, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task MessageConstraints_WhenKeysTypesContentOrRelationsAreInvalid_RejectRows()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            var sender = CreateUser("sender");
            var conversation = Conversation.CreateChannel(
                Guid.NewGuid(), ConversationType.PublicChannel, "General", sender.Id, CreatedAt);
            await using var context = CreateContext(databasePath);
            await context.Database.MigrateAsync();
            context.AddRange(sender, conversation);
            await context.SaveChangesAsync();

            var clientMessageId = Guid.NewGuid();
            context.Messages.Add(new Message(
                clientMessageId, conversation.Id, sender.Id, MessageType.Text, "first", null, CreatedAt));
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();

            context.Messages.Add(new Message(
                clientMessageId, conversation.Id, sender.Id, MessageType.Text, "duplicate", null,
                CreatedAt.AddMinutes(1)));
            var duplicate = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
            Assert.Equal(2067, Assert.IsType<SqliteException>(duplicate.InnerException).SqliteExtendedErrorCode);
            context.ChangeTracker.Clear();

            await AssertSqliteConstraintAsync(context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO Messages (ClientMessageId, ConversationId, SenderId, Type, Content, ReplyToMessageId, CreatedAt)
                VALUES ({Guid.NewGuid().ToString("D")}, {conversation.Id.ToString("D")}, {sender.Id.ToString("D")}, {99}, {"bad type"}, NULL, {"2026-08-03T09:00:00.000Z"});
                """), 275);
            await AssertSqliteConstraintAsync(context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO Messages (ClientMessageId, ConversationId, SenderId, Type, Content, ReplyToMessageId, CreatedAt)
                VALUES ({Guid.NewGuid().ToString("D")}, {conversation.Id.ToString("D")}, {sender.Id.ToString("D")}, {1}, {"   "}, NULL, {"2026-08-03T09:00:00.000Z"});
                """), 275);
            await AssertSqliteConstraintAsync(context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO Messages (ClientMessageId, ConversationId, SenderId, Type, Content, ReplyToMessageId, CreatedAt)
                VALUES ({Guid.NewGuid().ToString("D")}, {conversation.Id.ToString("D")}, {Guid.NewGuid().ToString("D")}, {1}, {"missing sender"}, NULL, {"2026-08-03T09:00:00.000Z"});
                """), 787);
            await AssertSqliteConstraintAsync(context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO Messages (ClientMessageId, ConversationId, SenderId, Type, Content, ReplyToMessageId, CreatedAt)
                VALUES ({Guid.NewGuid().ToString("D")}, {conversation.Id.ToString("D")}, {sender.Id.ToString("D")}, {1}, {"missing reply"}, {999999L}, {"2026-08-03T09:00:00.000Z"});
                """), 787);
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task MessageForeignKeys_WhenConversationOrUsersAreDeleted_CascadeOwnedRowsAndRestrictUsers()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            var sender = CreateUser("sender");
            var mentioned = CreateUser("mentioned");
            var conversation = Conversation.CreateChannel(
                Guid.NewGuid(), ConversationType.PublicChannel, "General", sender.Id, CreatedAt);
            await using var context = CreateContext(databasePath);
            await context.Database.MigrateAsync();
            context.AddRange(sender, mentioned, conversation);
            await context.SaveChangesAsync();
            var root = new Message(
                Guid.NewGuid(), conversation.Id, sender.Id, MessageType.Text, "root", null, CreatedAt);
            root.AddMention(mentioned.Id);
            context.Messages.Add(root);
            await context.SaveChangesAsync();
            context.Messages.Add(new Message(
                Guid.NewGuid(), conversation.Id, mentioned.Id, MessageType.Text, "reply", root.Id,
                CreatedAt.AddMinutes(1)));
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();

            var senderDelete = await Assert.ThrowsAsync<SqliteException>(() =>
                context.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM Users WHERE Id = {sender.Id.ToString("D")};"));
            Assert.Equal(19, senderDelete.SqliteErrorCode);
            var mentionedDelete = await Assert.ThrowsAsync<SqliteException>(() =>
                context.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM Users WHERE Id = {mentioned.Id.ToString("D")};"));
            Assert.Equal(19, mentionedDelete.SqliteErrorCode);

            await context.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM Conversations WHERE Id = {conversation.Id.ToString("D")};");
            Assert.Empty(await context.Messages.AsNoTracking().ToArrayAsync());
            Assert.Empty(await context.MessageMentions.AsNoTracking().ToArrayAsync());
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    private User CreateUser(string userName) => new(
        Guid.NewGuid(),
        userName,
        userName,
        "password-hash",
        isAdmin: false,
        isDisabled: false,
        CreatedAt,
        userNameNormalizer);

    private static RelayCoveDbContext CreateContext(string databasePath)
    {
        var options = new DbContextOptionsBuilder<RelayCoveDbContext>()
            .UseSqlite(CreateConnectionString(databasePath))
            .Options;
        return new RelayCoveDbContext(options);
    }

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
        return Path.Combine(directory, "relaycove-message-tests.db");
    }

    private static void DeleteDatabaseFiles(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath)!;
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task AssertSqliteConstraintAsync(Task operation, int extendedErrorCode)
    {
        var exception = await Assert.ThrowsAsync<SqliteException>(() => operation);
        Assert.Equal(19, exception.SqliteErrorCode);
        Assert.Equal(extendedErrorCode, exception.SqliteExtendedErrorCode);
    }
}
