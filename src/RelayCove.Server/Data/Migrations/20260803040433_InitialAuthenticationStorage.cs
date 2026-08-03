using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RelayCove.Server.Data.Migrations;

/// <inheritdoc />
public partial class InitialAuthenticationStorage : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Users",
            columns: table => new
            {
                Id = table.Column<string>(type: "TEXT", nullable: false),
                UserName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                NormalizedUserName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                DisplayName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                AvatarAttachmentId = table.Column<string>(type: "TEXT", nullable: true),
                PasswordHash = table.Column<string>(type: "TEXT", nullable: false),
                IsAdmin = table.Column<bool>(type: "INTEGER", nullable: false),
                IsDisabled = table.Column<bool>(type: "INTEGER", nullable: false),
                CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<string>(type: "TEXT", nullable: false),
                LastLoginAt = table.Column<string>(type: "TEXT", nullable: true),
                LastOnlineAt = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Users", x => x.Id);
                table.CheckConstraint("CK_Users_CreatedAt_Format", "length(\"CreatedAt\") = 24 AND substr(\"CreatedAt\", 5, 1) = '-' AND substr(\"CreatedAt\", 8, 1) = '-' AND substr(\"CreatedAt\", 11, 1) = 'T' AND substr(\"CreatedAt\", 14, 1) = ':' AND substr(\"CreatedAt\", 17, 1) = ':' AND substr(\"CreatedAt\", 20, 1) = '.' AND substr(\"CreatedAt\", 24, 1) = 'Z'");
                table.CheckConstraint("CK_Users_DisplayName_Length", "length(\"DisplayName\") BETWEEN 1 AND 100");
                table.CheckConstraint("CK_Users_Id_Format", "length(\"Id\") = 36 AND lower(\"Id\") = \"Id\" AND substr(\"Id\", 9, 1) = '-' AND substr(\"Id\", 14, 1) = '-' AND substr(\"Id\", 19, 1) = '-' AND substr(\"Id\", 24, 1) = '-'");
                table.CheckConstraint("CK_Users_IsAdmin_Boolean", "\"IsAdmin\" IN (0, 1)");
                table.CheckConstraint("CK_Users_IsDisabled_Boolean", "\"IsDisabled\" IN (0, 1)");
                table.CheckConstraint("CK_Users_LastLoginAt_Format", "\"LastLoginAt\" IS NULL OR (length(\"LastLoginAt\") = 24 AND substr(\"LastLoginAt\", 5, 1) = '-' AND substr(\"LastLoginAt\", 8, 1) = '-' AND substr(\"LastLoginAt\", 11, 1) = 'T' AND substr(\"LastLoginAt\", 14, 1) = ':' AND substr(\"LastLoginAt\", 17, 1) = ':' AND substr(\"LastLoginAt\", 20, 1) = '.' AND substr(\"LastLoginAt\", 24, 1) = 'Z')");
                table.CheckConstraint("CK_Users_LastOnlineAt_Format", "\"LastOnlineAt\" IS NULL OR (length(\"LastOnlineAt\") = 24 AND substr(\"LastOnlineAt\", 5, 1) = '-' AND substr(\"LastOnlineAt\", 8, 1) = '-' AND substr(\"LastOnlineAt\", 11, 1) = 'T' AND substr(\"LastOnlineAt\", 14, 1) = ':' AND substr(\"LastOnlineAt\", 17, 1) = ':' AND substr(\"LastOnlineAt\", 20, 1) = '.' AND substr(\"LastOnlineAt\", 24, 1) = 'Z')");
                table.CheckConstraint("CK_Users_NormalizedUserName_Format", "length(\"NormalizedUserName\") BETWEEN 3 AND 64 AND \"NormalizedUserName\" NOT GLOB '*[^A-Z0-9._-]*' AND upper(\"NormalizedUserName\") = \"NormalizedUserName\"");
                table.CheckConstraint("CK_Users_PasswordHash_NotEmpty", "length(\"PasswordHash\") > 0");
                table.CheckConstraint("CK_Users_UpdatedAt_Format", "length(\"UpdatedAt\") = 24 AND substr(\"UpdatedAt\", 5, 1) = '-' AND substr(\"UpdatedAt\", 8, 1) = '-' AND substr(\"UpdatedAt\", 11, 1) = 'T' AND substr(\"UpdatedAt\", 14, 1) = ':' AND substr(\"UpdatedAt\", 17, 1) = ':' AND substr(\"UpdatedAt\", 20, 1) = '.' AND substr(\"UpdatedAt\", 24, 1) = 'Z'");
                table.CheckConstraint("CK_Users_UserName_Format", "length(\"UserName\") BETWEEN 3 AND 64 AND \"UserName\" NOT GLOB '*[^A-Za-z0-9._-]*'");
            });

        migrationBuilder.CreateTable(
            name: "RefreshTokens",
            columns: table => new
            {
                Id = table.Column<string>(type: "TEXT", nullable: false),
                UserId = table.Column<string>(type: "TEXT", nullable: false),
                TokenHash = table.Column<string>(type: "TEXT", maxLength: 43, nullable: false),
                DeviceName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                ExpiresAt = table.Column<string>(type: "TEXT", nullable: false),
                RevokedAt = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_RefreshTokens", x => x.Id);
                table.CheckConstraint("CK_RefreshTokens_CreatedAt_Format", "length(\"CreatedAt\") = 24 AND substr(\"CreatedAt\", 5, 1) = '-' AND substr(\"CreatedAt\", 8, 1) = '-' AND substr(\"CreatedAt\", 11, 1) = 'T' AND substr(\"CreatedAt\", 14, 1) = ':' AND substr(\"CreatedAt\", 17, 1) = ':' AND substr(\"CreatedAt\", 20, 1) = '.' AND substr(\"CreatedAt\", 24, 1) = 'Z'");
                table.CheckConstraint("CK_RefreshTokens_DeviceName_Length", "length(\"DeviceName\") BETWEEN 1 AND 128");
                table.CheckConstraint("CK_RefreshTokens_ExpiresAt_Format", "length(\"ExpiresAt\") = 24 AND substr(\"ExpiresAt\", 5, 1) = '-' AND substr(\"ExpiresAt\", 8, 1) = '-' AND substr(\"ExpiresAt\", 11, 1) = 'T' AND substr(\"ExpiresAt\", 14, 1) = ':' AND substr(\"ExpiresAt\", 17, 1) = ':' AND substr(\"ExpiresAt\", 20, 1) = '.' AND substr(\"ExpiresAt\", 24, 1) = 'Z'");
                table.CheckConstraint("CK_RefreshTokens_Expiry_Order", "\"ExpiresAt\" > \"CreatedAt\"");
                table.CheckConstraint("CK_RefreshTokens_Id_Format", "length(\"Id\") = 36 AND lower(\"Id\") = \"Id\" AND substr(\"Id\", 9, 1) = '-' AND substr(\"Id\", 14, 1) = '-' AND substr(\"Id\", 19, 1) = '-' AND substr(\"Id\", 24, 1) = '-'");
                table.CheckConstraint("CK_RefreshTokens_RevokedAt_Format", "\"RevokedAt\" IS NULL OR (length(\"RevokedAt\") = 24 AND substr(\"RevokedAt\", 5, 1) = '-' AND substr(\"RevokedAt\", 8, 1) = '-' AND substr(\"RevokedAt\", 11, 1) = 'T' AND substr(\"RevokedAt\", 14, 1) = ':' AND substr(\"RevokedAt\", 17, 1) = ':' AND substr(\"RevokedAt\", 20, 1) = '.' AND substr(\"RevokedAt\", 24, 1) = 'Z')");
                table.CheckConstraint("CK_RefreshTokens_TokenHash_Format", "length(\"TokenHash\") = 43 AND \"TokenHash\" NOT GLOB '*[^A-Za-z0-9_-]*'");
                table.CheckConstraint("CK_RefreshTokens_UserId_Format", "length(\"UserId\") = 36 AND lower(\"UserId\") = \"UserId\" AND substr(\"UserId\", 9, 1) = '-' AND substr(\"UserId\", 14, 1) = '-' AND substr(\"UserId\", 19, 1) = '-' AND substr(\"UserId\", 24, 1) = '-'");
                table.ForeignKey(
                    name: "FK_RefreshTokens_Users_UserId",
                    column: x => x.UserId,
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_RefreshTokens_TokenHash",
            table: "RefreshTokens",
            column: "TokenHash",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_RefreshTokens_UserId",
            table: "RefreshTokens",
            column: "UserId");

        migrationBuilder.CreateIndex(
            name: "IX_Users_NormalizedUserName",
            table: "Users",
            column: "NormalizedUserName",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Users_UserName",
            table: "Users",
            column: "UserName",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "RefreshTokens");

        migrationBuilder.DropTable(
            name: "Users");
    }
}
