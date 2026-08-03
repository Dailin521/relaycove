using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RelayCove.Server.Data.Migrations;

/// <inheritdoc />
public partial class AddConversationStorage : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Conversations",
            columns: table => new
            {
                Id = table.Column<string>(type: "TEXT", nullable: false),
                Type = table.Column<int>(type: "INTEGER", nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                AvatarAttachmentId = table.Column<string>(type: "TEXT", nullable: true),
                CreatedByUserId = table.Column<string>(type: "TEXT", nullable: false),
                CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<string>(type: "TEXT", nullable: false),
                IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false),
                DirectParticipantKey = table.Column<string>(type: "TEXT", maxLength: 73, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Conversations", x => x.Id);
                table.CheckConstraint("CK_Conversations_AvatarAttachmentId_Format", "\"AvatarAttachmentId\" IS NULL OR (\"AvatarAttachmentId\" GLOB '[0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f]-[0-9a-f][0-9a-f][0-9a-f][0-9a-f]-[0-9a-f][0-9a-f][0-9a-f][0-9a-f]-[0-9a-f][0-9a-f][0-9a-f][0-9a-f]-[0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f]' AND \"AvatarAttachmentId\" <> '00000000-0000-0000-0000-000000000000')");
                table.CheckConstraint("CK_Conversations_CreatedAt_Format", "\"CreatedAt\" GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]T[0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9][0-9][0-9]Z'");
                table.CheckConstraint("CK_Conversations_CreatedByUserId_Format", "\"CreatedByUserId\" GLOB '[0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f]-[0-9a-f][0-9a-f][0-9a-f][0-9a-f]-[0-9a-f][0-9a-f][0-9a-f][0-9a-f]-[0-9a-f][0-9a-f][0-9a-f][0-9a-f]-[0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f]' AND \"CreatedByUserId\" <> '00000000-0000-0000-0000-000000000000'");
                table.CheckConstraint("CK_Conversations_DirectParticipantKey_ByType", "(\"Type\" IN (1, 2) AND \"DirectParticipantKey\" IS NULL) OR (\"Type\" = 3 AND \"DirectParticipantKey\" IS NOT NULL AND length(\"DirectParticipantKey\") = 73 AND substr(\"DirectParticipantKey\", 37, 1) = ':' AND substr(\"DirectParticipantKey\", 1, 36) GLOB '[0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f]-[0-9a-f][0-9a-f][0-9a-f][0-9a-f]-[0-9a-f][0-9a-f][0-9a-f][0-9a-f]-[0-9a-f][0-9a-f][0-9a-f][0-9a-f]-[0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f]' AND substr(\"DirectParticipantKey\", 38, 36) GLOB '[0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f]-[0-9a-f][0-9a-f][0-9a-f][0-9a-f]-[0-9a-f][0-9a-f][0-9a-f][0-9a-f]-[0-9a-f][0-9a-f][0-9a-f][0-9a-f]-[0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f]' AND substr(\"DirectParticipantKey\", 1, 36) <> '00000000-0000-0000-0000-000000000000' AND substr(\"DirectParticipantKey\", 38, 36) <> '00000000-0000-0000-0000-000000000000' AND substr(\"DirectParticipantKey\", 1, 36) < substr(\"DirectParticipantKey\", 38, 36) AND \"CreatedByUserId\" IN (substr(\"DirectParticipantKey\", 1, 36), substr(\"DirectParticipantKey\", 38, 36)))");
                table.CheckConstraint("CK_Conversations_Id_Format", "\"Id\" GLOB '[0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f]-[0-9a-f][0-9a-f][0-9a-f][0-9a-f]-[0-9a-f][0-9a-f][0-9a-f][0-9a-f]-[0-9a-f][0-9a-f][0-9a-f][0-9a-f]-[0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f]' AND \"Id\" <> '00000000-0000-0000-0000-000000000000'");
                table.CheckConstraint("CK_Conversations_IsDeleted_Boolean", "\"IsDeleted\" IN (0, 1)");
                table.CheckConstraint("CK_Conversations_Name_ByType", "(\"Type\" IN (1, 2) AND length(\"Name\") BETWEEN 1 AND 100 AND length(trim(\"Name\")) > 0) OR (\"Type\" = 3 AND \"Name\" = '')");
                table.CheckConstraint("CK_Conversations_Type_Value", "\"Type\" IN (1, 2, 3)");
                table.CheckConstraint("CK_Conversations_Update_Order", "\"UpdatedAt\" >= \"CreatedAt\"");
                table.CheckConstraint("CK_Conversations_UpdatedAt_Format", "\"UpdatedAt\" GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]T[0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9][0-9][0-9]Z'");
                table.ForeignKey(
                    name: "FK_Conversations_Users_CreatedByUserId",
                    column: x => x.CreatedByUserId,
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "ConversationMembers",
            columns: table => new
            {
                ConversationId = table.Column<string>(type: "TEXT", nullable: false),
                UserId = table.Column<string>(type: "TEXT", nullable: false),
                Role = table.Column<int>(type: "INTEGER", nullable: false),
                JoinedAt = table.Column<string>(type: "TEXT", nullable: false),
                LastReadMessageId = table.Column<long>(type: "INTEGER", nullable: false, defaultValue: 0L),
                IsMuted = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ConversationMembers", x => new { x.ConversationId, x.UserId });
                table.CheckConstraint("CK_ConversationMembers_ConversationId_Format", "\"ConversationId\" GLOB '[0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f]-[0-9a-f][0-9a-f][0-9a-f][0-9a-f]-[0-9a-f][0-9a-f][0-9a-f][0-9a-f]-[0-9a-f][0-9a-f][0-9a-f][0-9a-f]-[0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f]' AND \"ConversationId\" <> '00000000-0000-0000-0000-000000000000'");
                table.CheckConstraint("CK_ConversationMembers_IsMuted_Boolean", "\"IsMuted\" IN (0, 1)");
                table.CheckConstraint("CK_ConversationMembers_JoinedAt_Format", "\"JoinedAt\" GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]T[0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9][0-9][0-9]Z'");
                table.CheckConstraint("CK_ConversationMembers_LastReadMessageId_NonNegative", "\"LastReadMessageId\" >= 0");
                table.CheckConstraint("CK_ConversationMembers_Role_Value", "\"Role\" IN (1, 2)");
                table.CheckConstraint("CK_ConversationMembers_UserId_Format", "\"UserId\" GLOB '[0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f]-[0-9a-f][0-9a-f][0-9a-f][0-9a-f]-[0-9a-f][0-9a-f][0-9a-f][0-9a-f]-[0-9a-f][0-9a-f][0-9a-f][0-9a-f]-[0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f]' AND \"UserId\" <> '00000000-0000-0000-0000-000000000000'");
                table.ForeignKey(
                    name: "FK_ConversationMembers_Conversations_ConversationId",
                    column: x => x.ConversationId,
                    principalTable: "Conversations",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_ConversationMembers_Users_UserId",
                    column: x => x.UserId,
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_ConversationMembers_UserId",
            table: "ConversationMembers",
            column: "UserId");

        migrationBuilder.CreateIndex(
            name: "IX_Conversations_CreatedByUserId",
            table: "Conversations",
            column: "CreatedByUserId");

        migrationBuilder.CreateIndex(
            name: "IX_Conversations_DirectParticipantKey",
            table: "Conversations",
            column: "DirectParticipantKey",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Conversations_Type",
            table: "Conversations",
            column: "Type");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "ConversationMembers");

        migrationBuilder.DropTable(
            name: "Conversations");
    }
}
