using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlMotaheda.Modules.Archive.Data.Migrations
{
    /// <inheritdoc />
    internal partial class DocumentIdempotency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CapturedAt",
                schema: "docs",
                table: "Document",
                type: "datetime2(3)",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "IdempotencyKey",
                schema: "docs",
                table: "Document",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "UX_Document_UploadedBy_IdempotencyKey",
                schema: "docs",
                table: "Document",
                columns: new[] { "UploadedBy", "IdempotencyKey" },
                unique: true,
                filter: "[IdempotencyKey] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_Document_UploadedBy_IdempotencyKey",
                schema: "docs",
                table: "Document");

            migrationBuilder.DropColumn(
                name: "CapturedAt",
                schema: "docs",
                table: "Document");

            migrationBuilder.DropColumn(
                name: "IdempotencyKey",
                schema: "docs",
                table: "Document");
        }
    }
}
