using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlMotaheda.Modules.Reports.Data.Migrations
{
    /// <inheritdoc />
    internal partial class InitialReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "rpt");

            migrationBuilder.CreateTable(
                name: "ReportRun",
                schema: "rpt",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ReportCode = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Filters = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    Language = table.Column<string>(type: "varchar(2)", unicode: false, maxLength: 2, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    RowCount = table.Column<int>(type: "int", nullable: true),
                    FileName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Content = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    Error = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Claims = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RequestedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    RequestedBy = table.Column<int>(type: "int", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    LastDownloadedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportRun", x => x.Id);
                    table.CheckConstraint("CK_ReportRun_Kind", "[Kind] IN ('Excel', 'BackgroundExcel', 'Print')");
                    table.CheckConstraint("CK_ReportRun_Language", "[Language] IN ('ar', 'en')");
                    table.CheckConstraint("CK_ReportRun_Status", "[Status] IN ('Queued', 'Running', 'Ready', 'Failed')");
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReportRun_RequestedBy",
                schema: "rpt",
                table: "ReportRun",
                columns: new[] { "RequestedBy", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_ReportRun_Status",
                schema: "rpt",
                table: "ReportRun",
                columns: new[] { "Status", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReportRun",
                schema: "rpt");
        }
    }
}
