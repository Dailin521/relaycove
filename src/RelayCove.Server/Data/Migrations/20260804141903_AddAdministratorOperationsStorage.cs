using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RelayCove.Server.Data.Migrations;

/// <inheritdoc />
public partial class AddAdministratorOperationsStorage : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(
            name: "AccessTokenVersion",
            table: "Users",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0L);

        migrationBuilder.AddColumn<string>(
            name: "RetiredAt",
            table: "Users",
            type: "TEXT",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "AppSettings",
            columns: table => new
            {
                Key = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                Value = table.Column<string>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AppSettings", x => x.Key);
                table.CheckConstraint("CK_AppSettings_Key_NotEmpty", "length(\"Key\") BETWEEN 1 AND 128");
                table.CheckConstraint("CK_AppSettings_UpdatedAt_Format", "\"UpdatedAt\" GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]T[0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9][0-9][0-9]Z'");
                table.CheckConstraint("CK_AppSettings_Value_NotEmpty", "length(\"Value\") > 0");
            });

        migrationBuilder.AddCheckConstraint(
            name: "CK_Users_AccessTokenVersion_NonNegative",
            table: "Users",
            sql: "\"AccessTokenVersion\" >= 0");

        migrationBuilder.AddCheckConstraint(
            name: "CK_Users_RetiredAt_Format",
            table: "Users",
            sql: "\"RetiredAt\" IS NULL OR (\"RetiredAt\" GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]T[0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9][0-9][0-9]Z')");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "AppSettings");

        migrationBuilder.DropCheckConstraint(
            name: "CK_Users_AccessTokenVersion_NonNegative",
            table: "Users");

        migrationBuilder.DropCheckConstraint(
            name: "CK_Users_RetiredAt_Format",
            table: "Users");

        migrationBuilder.DropColumn(
            name: "AccessTokenVersion",
            table: "Users");

        migrationBuilder.DropColumn(
            name: "RetiredAt",
            table: "Users");
    }
}
