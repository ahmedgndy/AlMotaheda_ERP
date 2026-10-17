using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlMotaheda.Modules.Platform.Data.Migrations
{
    /// <inheritdoc />
    internal partial class DeviceTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DeviceToken",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    Platform = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    Token = table.Column<string>(type: "varchar(400)", unicode: false, maxLength: 400, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    DisabledAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceToken", x => x.Id);
                    table.CheckConstraint("CK_DeviceToken_Platform", "[Platform] IN ('Android')");
                });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceToken_User",
                schema: "platform",
                table: "DeviceToken",
                column: "UserId",
                filter: "[DisabledAt] IS NULL");

            migrationBuilder.CreateIndex(
                name: "UX_DeviceToken_Token",
                schema: "platform",
                table: "DeviceToken",
                column: "Token",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeviceToken",
                schema: "platform");
        }
    }
}
