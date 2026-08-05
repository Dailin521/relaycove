using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using RelayCove.Server.Data;
using RelayCove.Server.Data.Entities;
using RelayCove.Server.Services;
using RelayCove.Shared.Conversations;

namespace RelayCove.Server.Tests.Data;

public sealed class RelayCoveDbContextTests
{
    private static readonly DateTime CreatedAt = new(2026, 8, 3, 4, 0, 0, DateTimeKind.Utc);
    private readonly UserNameNormalizer userNameNormalizer = new();
    private readonly RefreshTokenHasher refreshTokenHasher = new();

    [Fact]
    public async Task Migration_WhenTwoCharacterUserNamesAreEnabled_PreservesUsersAndEnforcesNewMinimum()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            await using var context = CreateContext(databasePath);
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync("20260804141903_AddAdministratorOperationsStorage");
            var retainedUser = CreateUser(Guid.NewGuid(), "sam");
            context.Add(retainedUser);
            await context.SaveChangesAsync();

            await context.Database.MigrateAsync();
            context.ChangeTracker.Clear();

            Assert.Equal("sam", (await context.Users.AsNoTracking().SingleAsync()).UserName);

            context.Add(CreateUser(Guid.NewGuid(), "lq"));
            await context.SaveChangesAsync();

            var oneCharacterException = await Assert.ThrowsAsync<SqliteException>(() =>
                context.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO Users (Id, UserName, NormalizedUserName, DisplayName, AvatarAttachmentId, PasswordHash, IsAdmin, IsDisabled, CreatedAt, UpdatedAt, LastLoginAt, LastOnlineAt)
                    VALUES ({Guid.NewGuid().ToString("D").ToLowerInvariant()}, {"a"}, {"A"}, {"a"}, NULL, {"password-hash"}, 0, 0, {"2026-08-03T04:00:00.000Z"}, {"2026-08-03T04:00:00.000Z"}, NULL, NULL);
                    """));

            Assert.Equal(19, oneCharacterException.SqliteErrorCode);
            Assert.Equal(275, oneCharacterException.SqliteExtendedErrorCode);
            Assert.Equal(["lq", "sam"], await context.Users.AsNoTracking().OrderBy(user => user.UserName).Select(user => user.UserName).ToArrayAsync());
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task Migration_WhenAppliedAndRolledBack_HasExpectedSchemaWithoutModelDrift()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            await using var context = CreateContext(databasePath);
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync("20260804002739_AddAttachmentStorage");
            var retainedUserId = Guid.Parse("67370864-a515-46ee-8554-df49a21902e6");
            var retainedConversationId = Guid.Parse("95f3fb21-f5c6-48a8-a6ab-b51077681a2a");
            var retainedAttachmentId = Guid.Parse("1ba10a2d-47ba-45f0-b6e8-63af3d640037");
            const string createdAt = "2026-08-03T04:00:00.000Z";
            const string messageCreatedAt = "2026-08-03T04:01:00.000Z";
            const string attachmentCreatedAt = "2026-08-03T04:02:00.000Z";
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO Users (Id, UserName, NormalizedUserName, DisplayName, AvatarAttachmentId, PasswordHash, IsAdmin, IsDisabled, CreatedAt, UpdatedAt, LastLoginAt, LastOnlineAt)
                VALUES ({retainedUserId.ToString("D")}, {"retained-user"}, {"RETAINED-USER"}, {"retained-user"}, NULL, {"password-hash"}, {1}, {0}, {createdAt}, {createdAt}, NULL, NULL);
                INSERT INTO RefreshTokens (Id, UserId, TokenHash, DeviceName, CreatedAt, ExpiresAt, RevokedAt)
                VALUES ({"bc8dc9ff-21d8-4cb8-a327-f714fac9c0e1"}, {retainedUserId.ToString("D")}, {new string('a', RefreshTokenHasher.EncodedHashLength)}, {"retained-device"}, {createdAt}, {"2026-08-04T04:00:00.000Z"}, NULL);
                INSERT INTO Conversations (Id, Type, Name, AvatarAttachmentId, CreatedByUserId, CreatedAt, UpdatedAt, IsDeleted, DirectParticipantKey)
                VALUES ({retainedConversationId.ToString("D")}, {2}, {"Retained conversation"}, NULL, {retainedUserId.ToString("D")}, {createdAt}, {createdAt}, {0}, NULL);
                INSERT INTO ConversationMembers (ConversationId, UserId, Role, JoinedAt, LastReadMessageId, IsMuted)
                VALUES ({retainedConversationId.ToString("D")}, {retainedUserId.ToString("D")}, {2}, {createdAt}, {0}, {0});
                INSERT INTO Messages (ClientMessageId, ConversationId, SenderId, Type, Content, ReplyToMessageId, CreatedAt)
                VALUES ({"d9af7c2c-7ef7-4e17-8848-b4e2960fce6a"}, {retainedConversationId.ToString("D")}, {retainedUserId.ToString("D")}, {1}, {"Retained message"}, NULL, {messageCreatedAt});
                """);
            var retainedMessageId = long.Parse(
                Assert.Single(await ReadStringsAsync(databasePath, "SELECT CAST(Id AS TEXT) FROM Messages;")),
                System.Globalization.CultureInfo.InvariantCulture);
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO MessageMentions (MessageId, MentionedUserId)
                VALUES ({retainedMessageId}, {retainedUserId.ToString("D")});
                INSERT INTO Attachments (Id, MessageId, UploaderUserId, OriginalFileName, StoredFileName, ContentType, Size, Sha256, CreatedAt)
                VALUES ({retainedAttachmentId.ToString("D")}, {retainedMessageId}, {retainedUserId.ToString("D")}, {"retained.bin"}, {$"{retainedAttachmentId:N}_{new string('a', 32)}"}, {"application/octet-stream"}, {42}, {new string('b', Attachment.Sha256Length)}, {attachmentCreatedAt});
                """);

            await context.Database.MigrateAsync();

            Assert.False(context.Database.HasPendingModelChanges());
            Assert.Equal(
                ["AppSettings", "Attachments", "ConversationMembers", "Conversations", "MessageMentions", "Messages", "RefreshTokens", "Users"],
                await ReadStringsAsync(databasePath, "SELECT name FROM sqlite_master WHERE type = 'table' AND name IN ('Users', 'RefreshTokens', 'Conversations', 'ConversationMembers', 'Messages', 'MessageMentions', 'Attachments', 'AppSettings') ORDER BY name;"));
            Assert.Equal(
                new[] { "Id", "UserName", "NormalizedUserName", "DisplayName", "AvatarAttachmentId", "PasswordHash", "IsAdmin", "IsDisabled", "CreatedAt", "UpdatedAt", "LastLoginAt", "LastOnlineAt", "AccessTokenVersion", "RetiredAt" }.Order(),
                (await ReadStringsAsync(databasePath, "SELECT name FROM pragma_table_info('Users') ORDER BY cid;")).Order());
            Assert.Equal(
                ["Key", "Value", "UpdatedAt"],
                await ReadStringsAsync(databasePath, "SELECT name FROM pragma_table_info('AppSettings') ORDER BY cid;"));
            Assert.Equal(
                ["0"],
                await ReadStringsAsync(databasePath, "SELECT CAST(AccessTokenVersion AS TEXT) FROM Users;"));
            Assert.Equal(
                ["<null>"],
                await ReadStringsAsync(databasePath, "SELECT COALESCE(RetiredAt, '<null>') FROM Users;"));
            await AssertRetainedMigrationGraphAsync(
                databasePath,
                retainedUserId,
                retainedConversationId,
                retainedMessageId,
                retainedAttachmentId);

            await context.Database.ExecuteSqlRawAsync(
                "INSERT INTO AppSettings (Key, Value, UpdatedAt) VALUES ('Uploads.MaximumFileBytes', '1048576', '2026-08-04T12:00:00.000Z');");
            await migrator.MigrateAsync("20260804002739_AddAttachmentStorage");

            Assert.Equal(
                new[] { "Id", "UserName", "NormalizedUserName", "DisplayName", "AvatarAttachmentId", "PasswordHash", "IsAdmin", "IsDisabled", "CreatedAt", "UpdatedAt", "LastLoginAt", "LastOnlineAt" }.Order(),
                (await ReadStringsAsync(databasePath, "SELECT name FROM pragma_table_info('Users') ORDER BY cid;")).Order());
            Assert.Empty(await ReadStringsAsync(databasePath, "SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'AppSettings';"));
            await AssertRetainedMigrationGraphAsync(
                databasePath,
                retainedUserId,
                retainedConversationId,
                retainedMessageId,
                retainedAttachmentId);

            await migrator.MigrateAsync(Migration.InitialDatabase);

            Assert.Empty(await ReadStringsAsync(
                databasePath,
                "SELECT name FROM sqlite_master WHERE type = 'table' AND name IN ('Users', 'RefreshTokens', 'Conversations', 'ConversationMembers', 'Messages', 'MessageMentions', 'Attachments', 'AppSettings') ORDER BY name;"));
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
                var revokedToken = CreateToken(expiredId, userId, 1, CreatedAt.AddHours(1));
                revokedToken.Revoke(CreatedAt.AddMinutes(30).AddTicks(9876));
                context.Add(revokedToken);
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
            var revokedAt = (await verificationContext.RefreshTokens.AsNoTracking().SingleAsync(token => token.Id == expiredId)).RevokedAt;
            Assert.NotNull(revokedAt);
            Assert.Equal(DateTimeKind.Utc, revokedAt.Value.Kind);
            Assert.Equal(0, revokedAt.Value.Ticks % TimeSpan.TicksPerMillisecond);
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
    public async Task SaveChanges_WhenTimestampExceedsMillisecondPrecision_ThrowsBeforeWriting()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            await using var context = CreateContext(databasePath);
            await context.Database.MigrateAsync();
            var user = CreateUser(Guid.NewGuid(), "alice");
            context.Add(user);
            await context.SaveChangesAsync();
            context.Entry(user).Property(item => item.UpdatedAt).CurrentValue = CreatedAt.AddTicks(1);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());

            Assert.Contains("millisecond precision", exception.Message, StringComparison.Ordinal);
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

            var normalizationException = await Assert.ThrowsAsync<SqliteException>(() => context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO Users (Id, UserName, NormalizedUserName, DisplayName, AvatarAttachmentId, PasswordHash, IsAdmin, IsDisabled, CreatedAt, UpdatedAt, LastLoginAt, LastOnlineAt)
                VALUES ({Guid.NewGuid().ToString("D").ToLowerInvariant()}, {"dave"}, {"EVE"}, {"Dave"}, NULL, {"password-hash"}, 0, 0, {"2026-08-03T04:00:00.000Z"}, {"2026-08-03T04:00:00.000Z"}, NULL, NULL);
                """));

            Assert.Equal(19, normalizationException.SqliteErrorCode);
            Assert.Equal(275, normalizationException.SqliteExtendedErrorCode);

            var tokenHashException = await Assert.ThrowsAsync<SqliteException>(() => context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO RefreshTokens (Id, UserId, TokenHash, DeviceName, CreatedAt, ExpiresAt, RevokedAt)
                VALUES ({Guid.NewGuid().ToString("D").ToLowerInvariant()}, {persistedUser.Id.ToString("D").ToLowerInvariant()}, {"short"}, {"device"}, {"2026-08-03T04:00:00.000Z"}, {"2026-08-04T04:00:00.000Z"}, NULL);
                """));

            Assert.Equal(19, tokenHashException.SqliteErrorCode);
            Assert.Equal(275, tokenHashException.SqliteExtendedErrorCode);
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

    [Fact]
    public async Task ConversationPersistence_WhenRoundTripped_PreservesTypesKeysUtcAndMembershipState()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            var firstUser = CreateUser(Guid.Parse("6e7f28cf-9471-4f78-b540-fe98457d96ce"), "alice");
            var secondUser = CreateUser(Guid.Parse("2305c845-d79c-4c37-a26e-91f5b56f5cb2"), "bob");
            var conversationId = Guid.Parse("62f59380-b206-4515-87d2-92f4871baf94");
            await using (var context = CreateContext(databasePath))
            {
                await context.Database.MigrateAsync();
                context.AddRange(firstUser, secondUser);
                var conversation = Conversation.CreateDirect(
                    conversationId, firstUser.Id, secondUser.Id, firstUser.Id, CreatedAt.AddTicks(8888));
                conversation.SetAvatarAttachment(
                    Guid.Parse("8744b2bc-f83f-4e24-90a2-f5b76c34c5e8"),
                    CreatedAt.AddMinutes(1).AddTicks(1234));
                context.Add(conversation);
                context.Add(new ConversationMember(
                    conversation.Id,
                    firstUser.Id,
                    ConversationMemberRole.Member,
                    CreatedAt.AddSeconds(1).AddTicks(4321),
                    lastReadMessageId: 42,
                    isMuted: true));
                await context.SaveChangesAsync();
            }

            Assert.Equal(
                conversationId.ToString("D"),
                Assert.Single(await ReadStringsAsync(databasePath, "SELECT Id FROM Conversations;")));
            Assert.Equal(
                "2305c845-d79c-4c37-a26e-91f5b56f5cb2:6e7f28cf-9471-4f78-b540-fe98457d96ce",
                Assert.Single(await ReadStringsAsync(databasePath, "SELECT DirectParticipantKey FROM Conversations;")));

            await using var verificationContext = CreateContext(databasePath);
            var storedConversation = await verificationContext.Conversations.AsNoTracking().SingleAsync();
            var storedMember = await verificationContext.ConversationMembers.AsNoTracking().SingleAsync();

            Assert.Equal(ConversationType.Direct, storedConversation.Type);
            Assert.Empty(storedConversation.Name);
            Assert.Equal(DateTimeKind.Utc, storedConversation.CreatedAt.Kind);
            Assert.Equal(CreatedAt, storedConversation.CreatedAt);
            Assert.Equal(CreatedAt.AddMinutes(1), storedConversation.UpdatedAt);
            Assert.Equal(ConversationMemberRole.Member, storedMember.Role);
            Assert.Equal(DateTimeKind.Utc, storedMember.JoinedAt.Kind);
            Assert.Equal(42, storedMember.LastReadMessageId);
            Assert.True(storedMember.IsMuted);
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task ConversationConstraints_WhenRowsAreInvalid_RejectTypeNameKeyRoleAndReadBoundary()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            var user = CreateUser(Guid.Parse("36346aa9-448c-4394-922e-123ee3571e34"), "alice");
            var conversation = Conversation.CreateChannel(
                Guid.Parse("c9416526-30e6-4874-9a05-9624bca2f47f"),
                ConversationType.PublicChannel,
                "General",
                user.Id,
                CreatedAt);
            await using var context = CreateContext(databasePath);
            await context.Database.MigrateAsync();
            context.AddRange(user, conversation);
            await context.SaveChangesAsync();

            await AssertCheckConstraintAsync(context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO Conversations (Id, Type, Name, AvatarAttachmentId, CreatedByUserId, CreatedAt, UpdatedAt, IsDeleted, DirectParticipantKey)
                VALUES ({Guid.NewGuid().ToString("D")}, {9}, {"Invalid"}, NULL, {user.Id.ToString("D")}, {"2026-08-03T04:00:00.000Z"}, {"2026-08-03T04:00:00.000Z"}, {0}, NULL);
                """));
            await AssertCheckConstraintAsync(context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO Conversations (Id, Type, Name, AvatarAttachmentId, CreatedByUserId, CreatedAt, UpdatedAt, IsDeleted, DirectParticipantKey)
                VALUES ({Guid.NewGuid().ToString("D")}, {3}, {"not-empty"}, NULL, {user.Id.ToString("D")}, {"2026-08-03T04:00:00.000Z"}, {"2026-08-03T04:00:00.000Z"}, {0}, {"invalid-key"});
                """));
            await AssertCheckConstraintAsync(context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO Conversations (Id, Type, Name, AvatarAttachmentId, CreatedByUserId, CreatedAt, UpdatedAt, IsDeleted, DirectParticipantKey)
                VALUES ({Guid.NewGuid().ToString("D")}, {3}, {string.Empty}, NULL, {user.Id.ToString("D")}, {"2026-08-03T04:00:00.000Z"}, {"2026-08-03T04:00:00.000Z"}, {0}, NULL);
                """));
            await AssertCheckConstraintAsync(context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO Conversations (Id, Type, Name, AvatarAttachmentId, CreatedByUserId, CreatedAt, UpdatedAt, IsDeleted, DirectParticipantKey)
                VALUES ({Guid.NewGuid().ToString("D")}, {3}, {string.Empty}, NULL, {user.Id.ToString("D")}, {"2026-08-03T04:00:00.000Z"}, {"2026-08-03T04:00:00.000Z"}, {0}, {"00000000-0000-0000-0000-000000000001:00000000-0000-0000-0000-000000000002"});
                """));
            await AssertCheckConstraintAsync(context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO ConversationMembers (ConversationId, UserId, Role, JoinedAt, LastReadMessageId, IsMuted)
                VALUES ({conversation.Id.ToString("D")}, {user.Id.ToString("D")}, {9}, {"2026-08-03T04:00:00.000Z"}, {0}, {0});
                """));
            await AssertCheckConstraintAsync(context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO ConversationMembers (ConversationId, UserId, Role, JoinedAt, LastReadMessageId, IsMuted)
                VALUES ({conversation.Id.ToString("D")}, {user.Id.ToString("D")}, {1}, {"2026-08-03T04:00:00.000Z"}, {-1}, {0});
                """));
            await AssertCheckConstraintAsync(context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO Conversations (Id, Type, Name, AvatarAttachmentId, CreatedByUserId, CreatedAt, UpdatedAt, IsDeleted, DirectParticipantKey)
                VALUES ({Guid.NewGuid().ToString("D")}, {1}, {"Invalid boolean"}, NULL, {user.Id.ToString("D")}, {"2026-08-03T04:00:00.000Z"}, {"2026-08-03T04:00:00.000Z"}, {2}, NULL);
                """));
            await AssertCheckConstraintAsync(context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO ConversationMembers (ConversationId, UserId, Role, JoinedAt, LastReadMessageId, IsMuted)
                VALUES ({conversation.Id.ToString("D")}, {user.Id.ToString("D")}, {1}, {"not-a-utc-timestamp"}, {0}, {0});
                """));
            await AssertCheckConstraintAsync(context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO ConversationMembers (ConversationId, UserId, Role, JoinedAt, LastReadMessageId, IsMuted)
                VALUES ({conversation.Id.ToString("D")}, {user.Id.ToString("D")}, {1}, {"2026-08-03T04:00:00.000Z"}, {0}, {2});
                """));
            await AssertSqliteConstraintAsync(context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO Conversations (Id, Type, Name, AvatarAttachmentId, CreatedByUserId, CreatedAt, UpdatedAt, IsDeleted, DirectParticipantKey)
                VALUES ({Guid.NewGuid().ToString("D")}, {1}, {"Missing creator"}, NULL, {Guid.NewGuid().ToString("D")}, {"2026-08-03T04:00:00.000Z"}, {"2026-08-03T04:00:00.000Z"}, {0}, NULL);
                """), expectedExtendedErrorCode: 787);
            await AssertSqliteConstraintAsync(context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO ConversationMembers (ConversationId, UserId, Role, JoinedAt, LastReadMessageId, IsMuted)
                VALUES ({Guid.NewGuid().ToString("D")}, {user.Id.ToString("D")}, {1}, {"2026-08-03T04:00:00.000Z"}, {0}, {0});
                """), expectedExtendedErrorCode: 787);

            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO ConversationMembers (ConversationId, UserId, Role, JoinedAt, LastReadMessageId, IsMuted)
                VALUES ({conversation.Id.ToString("D")}, {user.Id.ToString("D")}, {1}, {"2026-08-03T04:00:00.000Z"}, {0}, {0});
                """);
            await AssertSqliteConstraintAsync(context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO ConversationMembers (ConversationId, UserId, Role, JoinedAt, LastReadMessageId, IsMuted)
                VALUES ({conversation.Id.ToString("D")}, {user.Id.ToString("D")}, {1}, {"2026-08-03T04:00:00.000Z"}, {0}, {0});
                """), expectedExtendedErrorCode: 1555);
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task DirectParticipantKey_WhenOrderIsReversedOrConversationIsDeleted_RemainsUnique()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            var firstUser = CreateUser(Guid.Parse("51d160eb-f0bb-497a-bfa6-2730ec9665bb"), "alice");
            var secondUser = CreateUser(Guid.Parse("eb239dfd-e66b-4bc9-880e-3e7d310d3640"), "bob");
            await using var context = CreateContext(databasePath);
            await context.Database.MigrateAsync();
            context.AddRange(firstUser, secondUser);
            var first = Conversation.CreateDirect(
                Guid.NewGuid(), firstUser.Id, secondUser.Id, firstUser.Id, CreatedAt);
            first.MarkDeleted(CreatedAt.AddMinutes(1));
            context.Add(first);
            await context.SaveChangesAsync();

            context.Add(Conversation.CreateDirect(
                Guid.NewGuid(), secondUser.Id, firstUser.Id, secondUser.Id, CreatedAt.AddMinutes(2)));

            var exception = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
            var sqliteException = Assert.IsType<SqliteException>(exception.InnerException);
            Assert.Equal(2067, sqliteException.SqliteExtendedErrorCode);
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task ConversationForeignKeys_WhenPrincipalsAreDeleted_CascadeMembersButRestrictCreator()
    {
        var databasePath = CreateDatabasePath();
        try
        {
            var creator = CreateUser(Guid.Parse("8233b5d0-76c4-408e-aee3-8cb3e5566c26"), "alice");
            var memberUser = CreateUser(Guid.Parse("aa5fcc84-e4a1-43a9-a8bb-4ed542611c96"), "bob");
            var firstConversation = Conversation.CreateChannel(
                Guid.Parse("659a76a2-09b3-412a-95fe-f117eb8a9ffc"),
                ConversationType.PublicChannel,
                "General",
                creator.Id,
                CreatedAt);
            var secondConversation = Conversation.CreateChannel(
                Guid.Parse("b84a3b5b-fde8-47d1-bc2a-0f33a4369761"),
                ConversationType.PrivateChannel,
                "Private",
                creator.Id,
                CreatedAt);
            await using var context = CreateContext(databasePath);
            await context.Database.MigrateAsync();
            context.AddRange(creator, memberUser, firstConversation, secondConversation);
            context.AddRange(
                new ConversationMember(firstConversation.Id, memberUser.Id, ConversationMemberRole.Member, CreatedAt),
                new ConversationMember(secondConversation.Id, memberUser.Id, ConversationMemberRole.Member, CreatedAt));
            await context.SaveChangesAsync();

            await context.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM Conversations WHERE Id = {firstConversation.Id.ToString("D")};");
            Assert.Single(await context.ConversationMembers.AsNoTracking().ToArrayAsync());

            await context.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM Users WHERE Id = {memberUser.Id.ToString("D")};");
            Assert.Empty(await context.ConversationMembers.AsNoTracking().ToArrayAsync());

            var creatorDeleteException = await Assert.ThrowsAsync<SqliteException>(() =>
                context.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM Users WHERE Id = {creator.Id.ToString("D")};"));
            Assert.Equal(19, creatorDeleteException.SqliteErrorCode);
            Assert.Equal(1811, creatorDeleteException.SqliteExtendedErrorCode);
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

        Assert.True(refreshTokenHasher.TryHashToken(rawToken, out var tokenHash));
        return new RefreshToken(id, userId, tokenHash, "workstation", CreatedAt, expiresAt);
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

    private static async Task AssertRetainedMigrationGraphAsync(
        string databasePath,
        Guid userId,
        Guid conversationId,
        long messageId,
        Guid attachmentId)
    {
        var userIdText = userId.ToString("D");
        var conversationIdText = conversationId.ToString("D");
        var messageIdText = messageId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var attachmentIdText = attachmentId.ToString("D");

        Assert.Equal([userIdText], await ReadStringsAsync(databasePath, "SELECT Id FROM Users;"));
        Assert.Equal([userIdText], await ReadStringsAsync(databasePath, "SELECT UserId FROM RefreshTokens;"));
        Assert.Equal([userIdText], await ReadStringsAsync(databasePath, "SELECT CreatedByUserId FROM Conversations;"));
        Assert.Equal([conversationIdText], await ReadStringsAsync(databasePath, "SELECT ConversationId FROM ConversationMembers;"));
        Assert.Equal([userIdText], await ReadStringsAsync(databasePath, "SELECT UserId FROM ConversationMembers;"));
        Assert.Equal([conversationIdText], await ReadStringsAsync(databasePath, "SELECT ConversationId FROM Messages;"));
        Assert.Equal([userIdText], await ReadStringsAsync(databasePath, "SELECT SenderId FROM Messages;"));
        Assert.Equal([messageIdText], await ReadStringsAsync(databasePath, "SELECT MessageId FROM MessageMentions;"));
        Assert.Equal([userIdText], await ReadStringsAsync(databasePath, "SELECT MentionedUserId FROM MessageMentions;"));
        Assert.Equal([attachmentIdText], await ReadStringsAsync(databasePath, "SELECT Id FROM Attachments;"));
        Assert.Equal([messageIdText], await ReadStringsAsync(databasePath, "SELECT MessageId FROM Attachments;"));
        Assert.Equal([userIdText], await ReadStringsAsync(databasePath, "SELECT UploaderUserId FROM Attachments;"));
        Assert.Empty(await ReadStringsAsync(databasePath, "PRAGMA foreign_key_check;"));
        Assert.Equal(
            ["IX_Users_NormalizedUserName", "IX_Users_UserName"],
            await ReadStringsAsync(databasePath, "SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = 'Users' AND name LIKE 'IX_Users_%' ORDER BY name;"));
        Assert.Equal(
            ["IX_Attachments_MessageId", "IX_Attachments_OriginalFileName", "IX_Attachments_StoredFileName", "IX_Attachments_UploaderUserId"],
            await ReadStringsAsync(databasePath, "SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = 'Attachments' AND name LIKE 'IX_Attachments_%' ORDER BY name;"));
        Assert.Equal(
            ["IX_ConversationMembers_UserId", "IX_Conversations_CreatedByUserId", "IX_Conversations_DirectParticipantKey", "IX_Conversations_Type"],
            await ReadStringsAsync(databasePath, "SELECT name FROM sqlite_master WHERE type = 'index' AND name LIKE 'IX_Conversation%' ORDER BY name;"));
        Assert.Equal(
            ["IX_MessageMentions_MentionedUserId", "IX_Messages_ConversationId_Id", "IX_Messages_CreatedAt", "IX_Messages_ReplyToMessageId", "IX_Messages_SenderId_ClientMessageId"],
            await ReadStringsAsync(databasePath, "SELECT name FROM sqlite_master WHERE type = 'index' AND name LIKE 'IX_Message%' ORDER BY name;"));
    }

    private static async Task AssertCheckConstraintAsync(Task operation)
    {
        await AssertSqliteConstraintAsync(operation, expectedExtendedErrorCode: 275);
    }

    private static async Task AssertSqliteConstraintAsync(Task operation, int expectedExtendedErrorCode)
    {
        var exception = await Assert.ThrowsAsync<SqliteException>(() => operation);
        Assert.Equal(19, exception.SqliteErrorCode);
        Assert.Equal(expectedExtendedErrorCode, exception.SqliteExtendedErrorCode);
    }
}
