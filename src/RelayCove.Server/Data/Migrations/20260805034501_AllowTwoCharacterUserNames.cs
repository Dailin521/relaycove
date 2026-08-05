using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RelayCove.Server.Data.Migrations;

/// <inheritdoc />
public partial class AllowTwoCharacterUserNames : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "CK_Users_NormalizedUserName_Format",
            table: "Users");

        migrationBuilder.DropCheckConstraint(
            name: "CK_Users_UserName_Format",
            table: "Users");

        migrationBuilder.AddCheckConstraint(
            name: "CK_Users_NormalizedUserName_Format",
            table: "Users",
            sql: "length(\"NormalizedUserName\") BETWEEN 2 AND 64 AND \"NormalizedUserName\" NOT GLOB '*[^A-Z0-9._-]*' AND \"NormalizedUserName\" GLOB '*[A-Z0-9]*' AND upper(\"NormalizedUserName\") = \"NormalizedUserName\"");

        migrationBuilder.AddCheckConstraint(
            name: "CK_Users_UserName_Format",
            table: "Users",
            sql: "length(\"UserName\") BETWEEN 2 AND 64 AND \"UserName\" NOT GLOB '*[^A-Za-z0-9._-]*' AND \"UserName\" GLOB '*[A-Za-z0-9]*'");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "CK_Users_NormalizedUserName_Format",
            table: "Users");

        migrationBuilder.DropCheckConstraint(
            name: "CK_Users_UserName_Format",
            table: "Users");

        migrationBuilder.AddCheckConstraint(
            name: "CK_Users_NormalizedUserName_Format",
            table: "Users",
            sql: "length(\"NormalizedUserName\") BETWEEN 3 AND 64 AND \"NormalizedUserName\" NOT GLOB '*[^A-Z0-9._-]*' AND \"NormalizedUserName\" GLOB '*[A-Z0-9]*' AND upper(\"NormalizedUserName\") = \"NormalizedUserName\"");

        migrationBuilder.AddCheckConstraint(
            name: "CK_Users_UserName_Format",
            table: "Users",
            sql: "length(\"UserName\") BETWEEN 3 AND 64 AND \"UserName\" NOT GLOB '*[^A-Za-z0-9._-]*' AND \"UserName\" GLOB '*[A-Za-z0-9]*'");
    }
}
