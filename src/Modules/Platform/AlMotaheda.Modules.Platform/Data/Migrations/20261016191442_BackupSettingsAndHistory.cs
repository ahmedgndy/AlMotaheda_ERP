using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlMotaheda.Modules.Platform.Data.Migrations
{
    /// <inheritdoc />
    internal partial class BackupSettingsAndHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BackupRun",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Trigger = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    RequestedBy = table.Column<int>(type: "int", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Database = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false),
                    FilePath = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    Verified = table.Column<bool>(type: "bit", nullable: false),
                    Compressed = table.Column<bool>(type: "bit", nullable: false),
                    DocumentsFilePath = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    DocumentsSizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    Error = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    FilesDeletedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackupRun", x => x.Id);
                    table.CheckConstraint("CK_BackupRun_Status", "[Status] IN ('Running', 'Succeeded', 'Failed')");
                    table.CheckConstraint("CK_BackupRun_Trigger", "[Trigger] IN ('Scheduled', 'Manual')");
                });

            migrationBuilder.CreateTable(
                name: "BackupSettings",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    DailyAt = table.Column<TimeOnly>(type: "time", nullable: false),
                    Folder = table.Column<string>(type: "nvarchar(260)", maxLength: 260, nullable: false),
                    RetentionCount = table.Column<int>(type: "int", nullable: false),
                    IncludeDocuments = table.Column<bool>(type: "bit", nullable: false),
                    RunRequestedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    RunRequestedBy = table.Column<int>(type: "int", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    UpdatedBy = table.Column<int>(type: "int", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackupSettings", x => x.Id);
                    table.CheckConstraint("CK_BackupSettings_Retention", "[RetentionCount] BETWEEN 1 AND 365");
                    table.CheckConstraint("CK_BackupSettings_Single", "[Id] = 1");
                });

            migrationBuilder.InsertData(
                schema: "platform",
                table: "BackupSettings",
                columns: new[] { "Id", "DailyAt", "Enabled", "Folder", "IncludeDocuments", "RetentionCount", "RunRequestedAt", "RunRequestedBy", "UpdatedAt", "UpdatedBy" },
                values: new object[] { 1, new TimeOnly(2, 0, 0), true, "C:\\AlMotaheda\\Backups", true, 3, null, null, null, null });

            migrationBuilder.CreateIndex(
                name: "IX_BackupRun_Trigger_StartedAt",
                schema: "platform",
                table: "BackupRun",
                columns: new[] { "Trigger", "StartedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BackupRun",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "BackupSettings",
                schema: "platform");
        }
    }
}
