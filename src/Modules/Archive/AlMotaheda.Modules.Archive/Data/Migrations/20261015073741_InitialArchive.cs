using System;
using AlMotaheda.Modules.Archive.Data.Configurations;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlMotaheda.Modules.Archive.Data.Migrations
{
    /// <inheritdoc />
    internal partial class InitialArchive : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "docs");

            migrationBuilder.CreateTable(
                name: "Document",
                schema: "docs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    Category = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    DocumentDate = table.Column<DateOnly>(type: "date", nullable: true),
                    FileName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    ContentType = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: false),
                    Kind = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    StorageKey = table.Column<string>(type: "varchar(200)", unicode: false, maxLength: 200, nullable: false),
                    UploadedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    UploadedBy = table.Column<int>(type: "int", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    DeletedBy = table.Column<int>(type: "int", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Document", x => x.Id);
                    table.CheckConstraint("CK_Document_Category", "[Category] IN ('PaymentCertificate', 'MeetingMinutes', 'SitePhoto', 'SiteVideo', 'ProgressReport', 'Contract', 'Drawing', 'Invoice', 'Receipt', 'Official', 'Other')");
                    table.CheckConstraint("CK_Document_Deleted", "([DeletedAt] IS NULL AND [DeletedBy] IS NULL) OR ([DeletedAt] IS NOT NULL AND [DeletedBy] IS NOT NULL)");
                    table.CheckConstraint("CK_Document_Kind", "[Kind] IN ('Pdf', 'Image', 'Video', 'Office', 'Drawing')");
                    table.CheckConstraint("CK_Document_Sha256", "LEN([Sha256]) = 64 AND [Sha256] COLLATE Latin1_General_BIN NOT LIKE '%[^0-9a-f]%'");
                    table.CheckConstraint("CK_Document_Size", "[SizeBytes] > 0");
                });

            migrationBuilder.CreateTable(
                name: "DocumentLink",
                schema: "docs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DocumentId = table.Column<int>(type: "int", nullable: false),
                    EntityType = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    EntityId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentLink", x => x.Id);
                    table.CheckConstraint("CK_DocumentLink_EntityId", "[EntityId] > 0");
                    table.CheckConstraint("CK_DocumentLink_EntityType", "[EntityType] IN ('Expense', 'SupplierPayment', 'DailyReport', 'ReportRequest', 'LaborSheet', 'SubContract', 'SubPayment', 'Equipment', 'EquipmentCost', 'Budget')");
                    table.ForeignKey(
                        name: "FK_DocumentLink_Document_DocumentId",
                        column: x => x.DocumentId,
                        principalSchema: "docs",
                        principalTable: "Document",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Document_ProjectId_Category_UploadedAt",
                schema: "docs",
                table: "Document",
                columns: new[] { "ProjectId", "Category", "UploadedAt" },
                filter: "[DeletedAt] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Document_ProjectId_UploadedAt",
                schema: "docs",
                table: "Document",
                columns: new[] { "ProjectId", "UploadedAt" },
                filter: "[DeletedAt] IS NULL");

            migrationBuilder.CreateIndex(
                name: "UX_Document_StorageKey",
                schema: "docs",
                table: "Document",
                column: "StorageKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DocumentLink_Entity",
                schema: "docs",
                table: "DocumentLink",
                columns: new[] { "EntityType", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "UX_DocumentLink_Document_Entity",
                schema: "docs",
                table: "DocumentLink",
                columns: new[] { "DocumentId", "EntityType", "EntityId" },
                unique: true);

            // Written by hand: projects.Project and access.User belong to other modules, so they aren't in this
            // module's model. Projects and users are never deleted, so the keys never block; they stop a document for
            // a project or a person that doesn't exist. The Access and Projects migrations must be applied first.
            migrationBuilder.AddForeignKey(
                name: DocumentConfiguration.ProjectForeignKey,
                schema: "docs",
                table: "Document",
                column: "ProjectId",
                principalSchema: "projects",
                principalTable: "Project",
                principalColumn: "Id",
                onDelete: ReferentialAction.NoAction);

            migrationBuilder.AddForeignKey(
                name: DocumentConfiguration.UploaderForeignKey,
                schema: "docs",
                table: "Document",
                column: "UploadedBy",
                principalSchema: "access",
                principalTable: "User",
                principalColumn: "Id",
                onDelete: ReferentialAction.NoAction);

            migrationBuilder.AddForeignKey(
                name: DocumentConfiguration.DeleterForeignKey,
                schema: "docs",
                table: "Document",
                column: "DeletedBy",
                principalSchema: "access",
                principalTable: "User",
                principalColumn: "Id",
                onDelete: ReferentialAction.NoAction);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: DocumentConfiguration.DeleterForeignKey,
                schema: "docs",
                table: "Document");

            migrationBuilder.DropForeignKey(
                name: DocumentConfiguration.UploaderForeignKey,
                schema: "docs",
                table: "Document");

            migrationBuilder.DropForeignKey(
                name: DocumentConfiguration.ProjectForeignKey,
                schema: "docs",
                table: "Document");

            migrationBuilder.DropTable(
                name: "DocumentLink",
                schema: "docs");

            migrationBuilder.DropTable(
                name: "Document",
                schema: "docs");
        }
    }
}
