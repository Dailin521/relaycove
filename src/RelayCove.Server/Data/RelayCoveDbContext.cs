using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RelayCove.Server.Data.Entities;
using RelayCove.Server.Services;

namespace RelayCove.Server.Data;

public sealed class RelayCoveDbContext(DbContextOptions<RelayCoveDbContext> options) : DbContext(options)
{
    private const string GuidGlobPattern = "[0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f]-[0-9a-f][0-9a-f][0-9a-f][0-9a-f]-[0-9a-f][0-9a-f][0-9a-f][0-9a-f]-[0-9a-f][0-9a-f][0-9a-f][0-9a-f]-[0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f]";
    private const string UtcGlobPattern = "[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]T[0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9][0-9][0-9]Z";

    public DbSet<User> Users => Set<User>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<Conversation> Conversations => Set<Conversation>();

    public DbSet<ConversationMember> ConversationMembers => Set<ConversationMember>();

    public DbSet<Message> Messages => Set<Message>();

    public DbSet<MessageMention> MessageMentions => Set<MessageMention>();

    public DbSet<Attachment> Attachments => Set<Attachment>();

    public DbSet<AppSetting> AppSettings => Set<AppSetting>();

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
        ConfigureConversation(modelBuilder.Entity<Conversation>());
        ConfigureConversationMember(modelBuilder.Entity<ConversationMember>());
        ConfigureMessage(modelBuilder.Entity<Message>());
        ConfigureMessageMention(modelBuilder.Entity<MessageMention>());
        ConfigureAttachment(modelBuilder.Entity<Attachment>());
        ConfigureAppSetting(modelBuilder.Entity<AppSetting>());
    }

    private static void ConfigureUser(EntityTypeBuilder<User> entity)
    {
        entity.ToTable("Users", table =>
        {
            table.HasCheckConstraint("CK_Users_Id_Format", GuidTextCheck("Id"));
            table.HasCheckConstraint("CK_Users_UserName_Format", $"length(\"UserName\") BETWEEN {UserNameNormalizer.MinimumLength} AND {UserNameNormalizer.MaximumLength} AND \"UserName\" NOT GLOB '*[^A-Za-z0-9._-]*' AND \"UserName\" GLOB '*[A-Za-z0-9]*'");
            table.HasCheckConstraint("CK_Users_NormalizedUserName_Format", $"length(\"NormalizedUserName\") BETWEEN {UserNameNormalizer.MinimumLength} AND {UserNameNormalizer.MaximumLength} AND \"NormalizedUserName\" NOT GLOB '*[^A-Z0-9._-]*' AND \"NormalizedUserName\" GLOB '*[A-Z0-9]*' AND upper(\"NormalizedUserName\") = \"NormalizedUserName\"");
            table.HasCheckConstraint("CK_Users_NameNormalization", "upper(\"UserName\") = \"NormalizedUserName\"");
            table.HasCheckConstraint("CK_Users_DisplayName_Length", "length(\"DisplayName\") BETWEEN 1 AND 100");
            table.HasCheckConstraint("CK_Users_PasswordHash_NotEmpty", "length(\"PasswordHash\") > 0");
            table.HasCheckConstraint("CK_Users_IsAdmin_Boolean", "\"IsAdmin\" IN (0, 1)");
            table.HasCheckConstraint("CK_Users_IsDisabled_Boolean", "\"IsDisabled\" IN (0, 1)");
            table.HasCheckConstraint("CK_Users_RetiredAt_Format", NullableUtcTextCheck("RetiredAt"));
            table.HasCheckConstraint("CK_Users_AccessTokenVersion_NonNegative", "\"AccessTokenVersion\" >= 0");
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
        entity.Property(user => user.RetiredAt).HasConversion(SqliteValueConverters.UtcDateTimeToString);
        entity.Property(user => user.AccessTokenVersion).HasDefaultValue(0L);
        entity.HasIndex(user => user.UserName).IsUnique();
        entity.HasIndex(user => user.NormalizedUserName).IsUnique();
    }

    private static void ConfigureRefreshToken(EntityTypeBuilder<RefreshToken> entity)
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

    private static void ConfigureConversation(EntityTypeBuilder<Conversation> entity)
    {
        var directKeyGuidPattern = GuidGlobPattern;
        entity.ToTable("Conversations", table =>
        {
            table.HasCheckConstraint("CK_Conversations_Id_Format", GuidTextCheck("Id"));
            table.HasCheckConstraint("CK_Conversations_Type_Value", "\"Type\" IN (1, 2, 3)");
            table.HasCheckConstraint(
                "CK_Conversations_Name_ByType",
                "(\"Type\" IN (1, 2) AND length(\"Name\") BETWEEN 1 AND 100 AND length(trim(\"Name\")) > 0) OR (\"Type\" = 3 AND \"Name\" = '')");
            table.HasCheckConstraint("CK_Conversations_AvatarAttachmentId_Format", NullableGuidTextCheck("AvatarAttachmentId"));
            table.HasCheckConstraint("CK_Conversations_CreatedByUserId_Format", GuidTextCheck("CreatedByUserId"));
            table.HasCheckConstraint("CK_Conversations_CreatedAt_Format", UtcTextCheck("CreatedAt"));
            table.HasCheckConstraint("CK_Conversations_UpdatedAt_Format", UtcTextCheck("UpdatedAt"));
            table.HasCheckConstraint("CK_Conversations_Update_Order", "\"UpdatedAt\" >= \"CreatedAt\"");
            table.HasCheckConstraint("CK_Conversations_IsDeleted_Boolean", "\"IsDeleted\" IN (0, 1)");
            table.HasCheckConstraint(
                "CK_Conversations_DirectParticipantKey_ByType",
                $"(\"Type\" IN (1, 2) AND \"DirectParticipantKey\" IS NULL) OR " +
                $"(\"Type\" = 3 AND \"DirectParticipantKey\" IS NOT NULL AND length(\"DirectParticipantKey\") = 73 AND substr(\"DirectParticipantKey\", 37, 1) = ':' AND " +
                $"substr(\"DirectParticipantKey\", 1, 36) GLOB '{directKeyGuidPattern}' AND " +
                $"substr(\"DirectParticipantKey\", 38, 36) GLOB '{directKeyGuidPattern}' AND " +
                "substr(\"DirectParticipantKey\", 1, 36) <> '00000000-0000-0000-0000-000000000000' AND " +
                "substr(\"DirectParticipantKey\", 38, 36) <> '00000000-0000-0000-0000-000000000000' AND " +
                "substr(\"DirectParticipantKey\", 1, 36) < substr(\"DirectParticipantKey\", 38, 36) AND " +
                "\"CreatedByUserId\" IN (substr(\"DirectParticipantKey\", 1, 36), substr(\"DirectParticipantKey\", 38, 36)))");
        });

        entity.HasKey(conversation => conversation.Id);
        entity.Property(conversation => conversation.Id)
            .HasConversion(SqliteValueConverters.GuidToString)
            .ValueGeneratedNever();
        entity.Property(conversation => conversation.Type).HasConversion<int>();
        entity.Property(conversation => conversation.Name).HasMaxLength(Conversation.MaximumNameLength).IsRequired();
        entity.Property(conversation => conversation.AvatarAttachmentId).HasConversion(SqliteValueConverters.GuidToString);
        entity.Property(conversation => conversation.CreatedByUserId).HasConversion(SqliteValueConverters.GuidToString);
        entity.Property(conversation => conversation.CreatedAt).HasConversion(SqliteValueConverters.UtcDateTimeToString);
        entity.Property(conversation => conversation.UpdatedAt).HasConversion(SqliteValueConverters.UtcDateTimeToString);
        entity.Property(conversation => conversation.DirectParticipantKey).HasMaxLength(73);
        entity.HasIndex(conversation => conversation.Type);
        entity.HasIndex(conversation => conversation.DirectParticipantKey).IsUnique();
        entity.HasOne(conversation => conversation.CreatedByUser)
            .WithMany(user => user.CreatedConversations)
            .HasForeignKey(conversation => conversation.CreatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureConversationMember(EntityTypeBuilder<ConversationMember> entity)
    {
        entity.ToTable("ConversationMembers", table =>
        {
            table.HasCheckConstraint("CK_ConversationMembers_ConversationId_Format", GuidTextCheck("ConversationId"));
            table.HasCheckConstraint("CK_ConversationMembers_UserId_Format", GuidTextCheck("UserId"));
            table.HasCheckConstraint("CK_ConversationMembers_Role_Value", "\"Role\" IN (1, 2)");
            table.HasCheckConstraint("CK_ConversationMembers_JoinedAt_Format", UtcTextCheck("JoinedAt"));
            table.HasCheckConstraint("CK_ConversationMembers_LastReadMessageId_NonNegative", "\"LastReadMessageId\" >= 0");
            table.HasCheckConstraint("CK_ConversationMembers_IsMuted_Boolean", "\"IsMuted\" IN (0, 1)");
        });

        entity.HasKey(member => new { member.ConversationId, member.UserId });
        entity.Property(member => member.ConversationId).HasConversion(SqliteValueConverters.GuidToString);
        entity.Property(member => member.UserId).HasConversion(SqliteValueConverters.GuidToString);
        entity.Property(member => member.Role).HasConversion<int>();
        entity.Property(member => member.JoinedAt).HasConversion(SqliteValueConverters.UtcDateTimeToString);
        entity.Property(member => member.LastReadMessageId).HasDefaultValue(0L);
        entity.Property(member => member.IsMuted).HasDefaultValue(false);
        entity.HasIndex(member => member.UserId);
        entity.HasOne(member => member.Conversation)
            .WithMany(conversation => conversation.Members)
            .HasForeignKey(member => member.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne(member => member.User)
            .WithMany(user => user.ConversationMemberships)
            .HasForeignKey(member => member.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureMessage(EntityTypeBuilder<Message> entity)
    {
        entity.ToTable("Messages", table =>
        {
            table.HasCheckConstraint("CK_Messages_Id_Positive", "\"Id\" > 0");
            table.HasCheckConstraint("CK_Messages_ClientMessageId_Format", GuidTextCheck("ClientMessageId"));
            table.HasCheckConstraint("CK_Messages_ConversationId_Format", GuidTextCheck("ConversationId"));
            table.HasCheckConstraint("CK_Messages_SenderId_Format", GuidTextCheck("SenderId"));
            table.HasCheckConstraint("CK_Messages_Type_Value", "\"Type\" IN (1, 2, 3, 4)");
            table.HasCheckConstraint(
                "CK_Messages_Content_ByType",
                "(\"Type\" IN (1, 4) AND \"Content\" IS NOT NULL AND length(\"Content\") BETWEEN 1 AND 4000 AND length(trim(\"Content\")) > 0) OR " +
                "(\"Type\" IN (2, 3) AND (\"Content\" IS NULL OR (length(\"Content\") BETWEEN 1 AND 4000 AND length(trim(\"Content\")) > 0)))");
            table.HasCheckConstraint(
                "CK_Messages_ReplyToMessageId_Positive",
                "\"ReplyToMessageId\" IS NULL OR \"ReplyToMessageId\" > 0");
            table.HasCheckConstraint("CK_Messages_CreatedAt_Format", UtcTextCheck("CreatedAt"));
        });

        entity.HasKey(message => message.Id);
        entity.Property(message => message.Id).UseAutoincrement();
        entity.Property(message => message.ClientMessageId).HasConversion(SqliteValueConverters.GuidToString);
        entity.Property(message => message.ConversationId).HasConversion(SqliteValueConverters.GuidToString);
        entity.Property(message => message.SenderId).HasConversion(SqliteValueConverters.GuidToString);
        entity.Property(message => message.Type).HasConversion<int>();
        entity.Property(message => message.Content).HasMaxLength(Message.MaximumContentLength);
        entity.Property(message => message.CreatedAt).HasConversion(SqliteValueConverters.UtcDateTimeToString);
        entity.HasIndex(message => new { message.ConversationId, message.Id });
        entity.HasIndex(message => new { message.SenderId, message.ClientMessageId }).IsUnique();
        entity.HasIndex(message => message.CreatedAt);
        entity.HasOne(message => message.Conversation)
            .WithMany(conversation => conversation.Messages)
            .HasForeignKey(message => message.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne(message => message.Sender)
            .WithMany(user => user.SentMessages)
            .HasForeignKey(message => message.SenderId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(message => message.ReplyToMessage)
            .WithMany(message => message.Replies)
            .HasForeignKey(message => message.ReplyToMessageId)
            .OnDelete(DeleteBehavior.NoAction);
    }

    private static void ConfigureMessageMention(EntityTypeBuilder<MessageMention> entity)
    {
        entity.ToTable("MessageMentions", table =>
        {
            table.HasCheckConstraint("CK_MessageMentions_MessageId_Positive", "\"MessageId\" > 0");
            table.HasCheckConstraint("CK_MessageMentions_MentionedUserId_Format", GuidTextCheck("MentionedUserId"));
        });

        entity.HasKey(mention => new { mention.MessageId, mention.MentionedUserId });
        entity.Property(mention => mention.MentionedUserId).HasConversion(SqliteValueConverters.GuidToString);
        entity.HasIndex(mention => mention.MentionedUserId);
        entity.HasOne(mention => mention.Message)
            .WithMany(message => message.Mentions)
            .HasForeignKey(mention => mention.MessageId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne(mention => mention.MentionedUser)
            .WithMany(user => user.MessageMentions)
            .HasForeignKey(mention => mention.MentionedUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureAttachment(EntityTypeBuilder<Attachment> entity)
    {
        entity.ToTable("Attachments", table =>
        {
            table.HasCheckConstraint("CK_Attachments_Id_Format", GuidTextCheck("Id"));
            table.HasCheckConstraint(
                "CK_Attachments_MessageId_Positive",
                "\"MessageId\" IS NULL OR \"MessageId\" > 0");
            table.HasCheckConstraint("CK_Attachments_UploaderUserId_Format", GuidTextCheck("UploaderUserId"));
            table.HasCheckConstraint(
                "CK_Attachments_OriginalFileName_Length",
                $"length(\"OriginalFileName\") BETWEEN 1 AND {Attachment.MaximumOriginalFileNameLength}");
            table.HasCheckConstraint(
                "CK_Attachments_StoredFileName_Format",
                $"length(\"StoredFileName\") = {Attachment.StoredFileNameLength} AND " +
                "substr(\"StoredFileName\", 1, 32) = replace(\"Id\", '-', '') AND " +
                "substr(\"StoredFileName\", 33, 1) = '_' AND " +
                "substr(\"StoredFileName\", 34) NOT GLOB '*[^0-9a-f]*'");
            table.HasCheckConstraint(
                "CK_Attachments_ContentType_Length",
                $"length(\"ContentType\") BETWEEN 1 AND {Attachment.MaximumContentTypeLength}");
            table.HasCheckConstraint(
                "CK_Attachments_Size_Range",
                $"\"Size\" BETWEEN 1 AND {Options.UploadOptions.AbsoluteMaximumFileBytes}");
            table.HasCheckConstraint(
                "CK_Attachments_Sha256_Format",
                $"length(\"Sha256\") = {Attachment.Sha256Length} AND \"Sha256\" NOT GLOB '*[^0-9a-f]*'");
            table.HasCheckConstraint("CK_Attachments_CreatedAt_Format", UtcTextCheck("CreatedAt"));
        });

        entity.HasKey(attachment => attachment.Id);
        entity.Property(attachment => attachment.Id)
            .HasConversion(SqliteValueConverters.GuidToString)
            .ValueGeneratedNever();
        entity.Property(attachment => attachment.UploaderUserId)
            .HasConversion(SqliteValueConverters.GuidToString);
        entity.Property(attachment => attachment.OriginalFileName)
            .HasMaxLength(Attachment.MaximumOriginalFileNameLength)
            .IsRequired();
        entity.Property(attachment => attachment.StoredFileName)
            .HasMaxLength(Attachment.StoredFileNameLength)
            .IsRequired();
        entity.Property(attachment => attachment.ContentType)
            .HasMaxLength(Attachment.MaximumContentTypeLength)
            .IsRequired();
        entity.Property(attachment => attachment.Sha256)
            .HasMaxLength(Attachment.Sha256Length)
            .IsRequired();
        entity.Property(attachment => attachment.CreatedAt)
            .HasConversion(SqliteValueConverters.UtcDateTimeToString);
        entity.HasIndex(attachment => attachment.MessageId);
        entity.HasIndex(attachment => attachment.OriginalFileName);
        entity.HasIndex(attachment => attachment.StoredFileName).IsUnique();
        entity.HasOne(attachment => attachment.Message)
            .WithMany(message => message.Attachments)
            .HasForeignKey(attachment => attachment.MessageId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne(attachment => attachment.UploaderUser)
            .WithMany(user => user.UploadedAttachments)
            .HasForeignKey(attachment => attachment.UploaderUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureAppSetting(EntityTypeBuilder<AppSetting> entity)
    {
        entity.ToTable("AppSettings", table =>
        {
            table.HasCheckConstraint("CK_AppSettings_Key_NotEmpty", "length(\"Key\") BETWEEN 1 AND 128");
            table.HasCheckConstraint("CK_AppSettings_Value_NotEmpty", "length(\"Value\") > 0");
            table.HasCheckConstraint("CK_AppSettings_UpdatedAt_Format", UtcTextCheck("UpdatedAt"));
        });

        entity.HasKey(setting => setting.Key);
        entity.Property(setting => setting.Key).HasMaxLength(128);
        entity.Property(setting => setting.Value).IsRequired();
        entity.Property(setting => setting.UpdatedAt)
            .HasConversion(SqliteValueConverters.UtcDateTimeToString);
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
                if (property.CurrentValue is DateTime preciseValue &&
                    preciseValue.Ticks % TimeSpan.TicksPerMillisecond != 0)
                {
                    throw new InvalidOperationException(
                        $"{entry.Metadata.ClrType.Name}.{property.Metadata.Name} must use millisecond precision.");
                }
            }
        }
    }

    private static string GuidTextCheck(string columnName) =>
        $"\"{columnName}\" GLOB '{GuidGlobPattern}' AND \"{columnName}\" <> '00000000-0000-0000-0000-000000000000'";

    private static string UtcTextCheck(string columnName) =>
        $"\"{columnName}\" GLOB '{UtcGlobPattern}'";

    private static string NullableUtcTextCheck(string columnName) =>
        $"\"{columnName}\" IS NULL OR ({UtcTextCheck(columnName)})";

    private static string NullableGuidTextCheck(string columnName) =>
        $"\"{columnName}\" IS NULL OR ({GuidTextCheck(columnName)})";
}
