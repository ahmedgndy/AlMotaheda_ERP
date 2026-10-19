using System;
using AlMotaheda.Modules.Projects.Data.Configurations;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlMotaheda.Modules.Projects.Data.Migrations
{
    /// <summary>
    /// Project cost management (docs/project-workspace/08-cost-management.md): main items and sub-items, the company
    /// price list with reference ranges, the price check copied onto supplies, the save key of a supply, and the cost
    /// alerts. Only adds: no row is changed or removed. Existing items all become main items (no parent), and their
    /// names were unique per project, so the new "unique among siblings" indexes hold for the existing rows.
    /// </summary>
    /// <remarks>
    /// Down restores the old "unique per project" item names; it fails if two sub-items of different main items
    /// were given the same name since, which must then be renamed first.
    /// </remarks>
    internal partial class CostManagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_ProjectItem_NameAr",
                schema: "projects",
                table: "ProjectItem");

            migrationBuilder.DropIndex(
                name: "UX_ProjectItem_NameEn",
                schema: "projects",
                table: "ProjectItem");

            migrationBuilder.AddColumn<int>(
                name: "ParentItemId",
                schema: "projects",
                table: "ProjectItem",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "IdempotencyKey",
                schema: "projects",
                table: "ProjectExpense",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PriceCheck",
                schema: "projects",
                table: "ProjectExpense",
                type: "varchar(30)",
                unicode: false,
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PriceItemId",
                schema: "projects",
                table: "ProjectExpense",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PriceJustification",
                schema: "projects",
                table: "ProjectExpense",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PriceRangeId",
                schema: "projects",
                table: "ProjectExpense",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ReferenceMaxPrice",
                schema: "projects",
                table: "ProjectExpense",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ReferenceMinPrice",
                schema: "projects",
                table: "ProjectExpense",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_ProjectItem_Project_Id",
                schema: "projects",
                table: "ProjectItem",
                columns: new[] { "ProjectId", "Id" });

            migrationBuilder.CreateTable(
                name: "CostAlert",
                schema: "projects",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    ExpenseId = table.Column<int>(type: "int", nullable: false),
                    RecipientUserId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    Kind = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CostAlert", x => x.Id);
                    table.CheckConstraint("CK_CostAlert_Kind", "[Kind] IN ('PriceAboveRange', 'CostRecorded')");
                    table.ForeignKey(
                        name: "FK_CostAlert_ProjectExpense_ExpenseId",
                        column: x => x.ExpenseId,
                        principalSchema: "projects",
                        principalTable: "ProjectExpense",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_CostAlert_Project_ProjectId",
                        column: x => x.ProjectId,
                        principalSchema: "projects",
                        principalTable: "Project",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "PriceItem",
                schema: "projects",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    NameAr = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    NameEn = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Classification = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    Unit = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Status = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    UpdatedBy = table.Column<int>(type: "int", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PriceItem", x => x.Id);
                    table.CheckConstraint("CK_PriceItem_Classification", "[Classification] IN ('CementAggregates', 'ReadyMix', 'Steel', 'Masonry', 'Finishing', 'Waterproofing', 'Piling', 'Formwork', 'Consumables', 'Services', 'Other')");
                    table.CheckConstraint("CK_PriceItem_Status", "[Status] IN ('Active', 'Inactive')");
                    table.CheckConstraint("CK_PriceItem_Unit", "[Unit] IN ('Bag', 'Ton', 'M3', 'M2', 'M', 'Piece', 'Trip', 'Roll', 'Lot', 'Job', 'Day', 'Visit', 'Brick', 'Liter', 'Kg', 'Other')");
                });

            migrationBuilder.CreateTable(
                name: "PriceRange",
                schema: "projects",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PriceItemId = table.Column<int>(type: "int", nullable: false),
                    SupplierId = table.Column<int>(type: "int", nullable: true),
                    MinPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    MaxPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "varchar(3)", unicode: false, maxLength: 3, nullable: false),
                    EffectiveFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    EffectiveTo = table.Column<DateOnly>(type: "date", nullable: true),
                    Conditions = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Status = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RequestedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    RequestedBy = table.Column<int>(type: "int", nullable: false),
                    DecidedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    DecidedBy = table.Column<int>(type: "int", nullable: true),
                    DecisionNote = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PriceRange", x => x.Id);
                    table.CheckConstraint("CK_PriceRange_Currency", "[Currency] = 'EGP'");
                    table.CheckConstraint("CK_PriceRange_Dates", "[EffectiveTo] IS NULL OR [EffectiveTo] >= [EffectiveFrom]");
                    table.CheckConstraint("CK_PriceRange_Decision", "([Status] = 'Pending' AND [DecidedAt] IS NULL AND [DecidedBy] IS NULL) OR ([Status] <> 'Pending' AND [DecidedAt] IS NOT NULL AND [DecidedBy] IS NOT NULL)");
                    table.CheckConstraint("CK_PriceRange_Prices", "[MinPrice] > 0 AND [MaxPrice] >= [MinPrice]");
                    table.CheckConstraint("CK_PriceRange_Status", "[Status] IN ('Pending', 'Approved', 'Rejected')");
                    table.ForeignKey(
                        name: "FK_PriceRange_PriceItem_PriceItemId",
                        column: x => x.PriceItemId,
                        principalSchema: "projects",
                        principalTable: "PriceItem",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_PriceRange_Supplier_SupplierId",
                        column: x => x.SupplierId,
                        principalSchema: "projects",
                        principalTable: "Supplier",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "UX_ProjectItem_Parent_NameAr",
                schema: "projects",
                table: "ProjectItem",
                columns: new[] { "ProjectId", "ParentItemId", "NameAr" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_ProjectItem_Parent_NameEn",
                schema: "projects",
                table: "ProjectItem",
                columns: new[] { "ProjectId", "ParentItemId", "NameEn" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_ProjectItem_Parent",
                schema: "projects",
                table: "ProjectItem",
                sql: "[ParentItemId] IS NULL OR [ParentItemId] <> [Id]");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectExpense_PriceItemId",
                schema: "projects",
                table: "ProjectExpense",
                column: "PriceItemId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectExpense_PriceRangeId",
                schema: "projects",
                table: "ProjectExpense",
                column: "PriceRangeId");

            migrationBuilder.CreateIndex(
                name: "UX_ProjectExpense_IdempotencyKey",
                schema: "projects",
                table: "ProjectExpense",
                column: "IdempotencyKey",
                unique: true,
                filter: "[IdempotencyKey] IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ProjectExpense_PriceCheck",
                schema: "projects",
                table: "ProjectExpense",
                sql: "([PriceCheck] IS NULL AND [PriceItemId] IS NULL AND [PriceRangeId] IS NULL AND [ReferenceMinPrice] IS NULL AND [ReferenceMaxPrice] IS NULL) OR ([Kind] = 'Supply' AND [PriceItemId] IS NOT NULL AND [PriceCheck] IN ('NoReference', 'WithinRange', 'AboveRange', 'BelowRange') AND (([PriceCheck] = 'NoReference' AND [PriceRangeId] IS NULL AND [ReferenceMinPrice] IS NULL AND [ReferenceMaxPrice] IS NULL) OR ([PriceCheck] <> 'NoReference' AND [PriceRangeId] IS NOT NULL AND [ReferenceMinPrice] IS NOT NULL AND [ReferenceMaxPrice] IS NOT NULL)))");

            migrationBuilder.CreateIndex(
                name: "IX_CostAlert_Project_CreatedAt",
                schema: "projects",
                table: "CostAlert",
                columns: new[] { "ProjectId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "UX_CostAlert_Expense_Kind_Recipient",
                schema: "projects",
                table: "CostAlert",
                columns: new[] { "ExpenseId", "Kind", "RecipientUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_PriceItem_NameAr",
                schema: "projects",
                table: "PriceItem",
                column: "NameAr",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_PriceItem_NameEn",
                schema: "projects",
                table: "PriceItem",
                column: "NameEn",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PriceRange_Status",
                schema: "projects",
                table: "PriceRange",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_PriceRange_SupplierId",
                schema: "projects",
                table: "PriceRange",
                column: "SupplierId");

            migrationBuilder.CreateIndex(
                name: "UX_PriceRange_ApprovedFrom",
                schema: "projects",
                table: "PriceRange",
                columns: new[] { "PriceItemId", "SupplierId", "EffectiveFrom" },
                unique: true,
                filter: "[Status] = 'Approved'");

            migrationBuilder.CreateIndex(
                name: "UX_PriceRange_Pending",
                schema: "projects",
                table: "PriceRange",
                columns: new[] { "PriceItemId", "SupplierId" },
                unique: true,
                filter: "[Status] = 'Pending'");

            migrationBuilder.AddForeignKey(
                name: "FK_ProjectExpense_PriceItem_PriceItemId",
                schema: "projects",
                table: "ProjectExpense",
                column: "PriceItemId",
                principalSchema: "projects",
                principalTable: "PriceItem",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_ProjectExpense_PriceRange_PriceRangeId",
                schema: "projects",
                table: "ProjectExpense",
                column: "PriceRangeId",
                principalSchema: "projects",
                principalTable: "PriceRange",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_ProjectItem_ProjectItem_Parent",
                schema: "projects",
                table: "ProjectItem",
                columns: new[] { "ProjectId", "ParentItemId" },
                principalSchema: "projects",
                principalTable: "ProjectItem",
                principalColumns: new[] { "ProjectId", "Id" });

            // The recipient is an Access record: this key is added by hand (Access owns the table).
            migrationBuilder.AddForeignKey(
                name: CostAlertConfiguration.RecipientForeignKey,
                schema: "projects",
                table: "CostAlert",
                column: "RecipientUserId",
                principalSchema: "access",
                principalTable: "User",
                principalColumn: "Id",
                onDelete: ReferentialAction.NoAction);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: CostAlertConfiguration.RecipientForeignKey,
                schema: "projects",
                table: "CostAlert");

            migrationBuilder.DropForeignKey(
                name: "FK_ProjectExpense_PriceItem_PriceItemId",
                schema: "projects",
                table: "ProjectExpense");

            migrationBuilder.DropForeignKey(
                name: "FK_ProjectExpense_PriceRange_PriceRangeId",
                schema: "projects",
                table: "ProjectExpense");

            migrationBuilder.DropForeignKey(
                name: "FK_ProjectItem_ProjectItem_Parent",
                schema: "projects",
                table: "ProjectItem");

            migrationBuilder.DropTable(
                name: "CostAlert",
                schema: "projects");

            migrationBuilder.DropTable(
                name: "PriceRange",
                schema: "projects");

            migrationBuilder.DropTable(
                name: "PriceItem",
                schema: "projects");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_ProjectItem_Project_Id",
                schema: "projects",
                table: "ProjectItem");

            migrationBuilder.DropIndex(
                name: "UX_ProjectItem_Parent_NameAr",
                schema: "projects",
                table: "ProjectItem");

            migrationBuilder.DropIndex(
                name: "UX_ProjectItem_Parent_NameEn",
                schema: "projects",
                table: "ProjectItem");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ProjectItem_Parent",
                schema: "projects",
                table: "ProjectItem");

            migrationBuilder.DropIndex(
                name: "IX_ProjectExpense_PriceItemId",
                schema: "projects",
                table: "ProjectExpense");

            migrationBuilder.DropIndex(
                name: "IX_ProjectExpense_PriceRangeId",
                schema: "projects",
                table: "ProjectExpense");

            migrationBuilder.DropIndex(
                name: "UX_ProjectExpense_IdempotencyKey",
                schema: "projects",
                table: "ProjectExpense");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ProjectExpense_PriceCheck",
                schema: "projects",
                table: "ProjectExpense");

            migrationBuilder.DropColumn(
                name: "ParentItemId",
                schema: "projects",
                table: "ProjectItem");

            migrationBuilder.DropColumn(
                name: "IdempotencyKey",
                schema: "projects",
                table: "ProjectExpense");

            migrationBuilder.DropColumn(
                name: "PriceCheck",
                schema: "projects",
                table: "ProjectExpense");

            migrationBuilder.DropColumn(
                name: "PriceItemId",
                schema: "projects",
                table: "ProjectExpense");

            migrationBuilder.DropColumn(
                name: "PriceJustification",
                schema: "projects",
                table: "ProjectExpense");

            migrationBuilder.DropColumn(
                name: "PriceRangeId",
                schema: "projects",
                table: "ProjectExpense");

            migrationBuilder.DropColumn(
                name: "ReferenceMaxPrice",
                schema: "projects",
                table: "ProjectExpense");

            migrationBuilder.DropColumn(
                name: "ReferenceMinPrice",
                schema: "projects",
                table: "ProjectExpense");

            migrationBuilder.CreateIndex(
                name: "UX_ProjectItem_NameAr",
                schema: "projects",
                table: "ProjectItem",
                columns: new[] { "ProjectId", "NameAr" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_ProjectItem_NameEn",
                schema: "projects",
                table: "ProjectItem",
                columns: new[] { "ProjectId", "NameEn" },
                unique: true);
        }
    }
}
