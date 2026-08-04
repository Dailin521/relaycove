using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RelayCove.Server.Data.Migrations;

/// <inheritdoc />
public partial class AddAttachmentStorage : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Attachments",
            columns: table => new
            {
                Id = table.Column<string>(type: "TEXT", nullable: false),
                MessageId = table.Column<long>(type: "INTEGER", nullable: true),
                UploaderUserId = table.Column<string>(type: "TEXT", nullable: false),
                OriginalFileName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                StoredFileName = table.Column<string>(type: "TEXT", maxLength: 65, nullable: false),
                ContentType = table.Column<string>(type: "TEXT", maxLength: 127, nullable: false),
                Size = table.Column<long>(type: "INTEGER", nullable: false),
                Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                CreatedAt = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Attachments", x => x.Id);
                table.CheckConstraint("CK_Attachments_ContentType_Length", "length(\"ContentType\") BETWEEN 1 AND 127");
                table.CheckConstraint("CK_Attachments_CreatedAt_Format", "\"CreatedAt\" GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]T[0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9][0-9][0-9]Z'");
                table.CheckConstraint("CK_Attachments_Id_Format", "\"Id\" GLOB '[0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f]-[0-9a-f][0-9a-f][0-9a-f][0-9a-f]-[0-9a-f][0-9a-f][0-9a-f][0-9a-f]-[0-9a-f][0-9a-f][0-9a-f][0-9a-f]-[0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f]' AND \"Id\" <> '00000000-0000-0000-0000-000000000000'");
                table.CheckConstraint("CK_Attachments_MessageId_Positive", "\"MessageId\" IS NULL OR \"MessageId\" > 0");
                table.CheckConstraint("CK_Attachments_OriginalFileName_Length", "length(\"OriginalFileName\") BETWEEN 1 AND 255");
                table.CheckConstraint("CK_Attachments_Sha256_Format", "length(\"Sha256\") = 64 AND \"Sha256\" NOT GLOB '*[^0-9a-f]*'");
                table.CheckConstraint("CK_Attachments_Size_Range", "\"Size\" BETWEEN 1 AND 104857600");
                table.CheckConstraint("CK_Attachments_StoredFileName_Format", "length(\"StoredFileName\") = 65 AND substr(\"StoredFileName\", 1, 32) = replace(\"Id\", '-', '') AND substr(\"StoredFileName\", 33, 1) = '_' AND substr(\"StoredFileName\", 34) NOT GLOB '*[^0-9a-f]*'");
                table.CheckConstraint("CK_Attachments_UploaderUserId_Format", "\"UploaderUserId\" GLOB '[0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f]-[0-9a-f][0-9a-f][0-9a-f][0-9a-f]-[0-9a-f][0-9a-f][0-9a-f][0-9a-f]-[0-9a-f][0-9a-f][0-9a-f][0-9a-f]-[0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f]' AND \"UploaderUserId\" <> '00000000-0000-0000-0000-000000000000'");
                table.ForeignKey(
                    name: "FK_Attachments_Messages_MessageId",
                    column: x => x.MessageId,
                    principalTable: "Messages",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_Attachments_Users_UploaderUserId",
                    column: x => x.UploaderUserId,
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Attachments_MessageId",
            table: "Attachments",
            column: "MessageId");

        migrationBuilder.CreateIndex(
            name: "IX_Attachments_OriginalFileName",
            table: "Attachments",
            column: "OriginalFileName");

        migrationBuilder.CreateIndex(
            name: "IX_Attachments_StoredFileName",
            table: "Attachments",
            column: "StoredFileName",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Attachments_UploaderUserId",
            table: "Attachments",
            column: "UploaderUserId");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "Attachments");
    }
}
