using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlMotaheda.Modules.Projects.Data.Migrations
{
    /// <inheritdoc />
    internal partial class MasterCatalogVariancesComponents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_BudgetLine_CostGroup",
                schema: "projects",
                table: "BudgetLine");

            migrationBuilder.AddColumn<string>(
                name: "Address",
                schema: "projects",
                table: "Supplier",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CommercialRegistrationNo",
                schema: "projects",
                table: "Supplier",
                type: "varchar(30)",
                unicode: false,
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContactPerson",
                schema: "projects",
                table: "Supplier",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Email",
                schema: "projects",
                table: "Supplier",
                type: "varchar(254)",
                unicode: false,
                maxLength: 254,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LegalName",
                schema: "projects",
                table: "Supplier",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MasterItemId",
                schema: "projects",
                table: "ProjectItem",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MasterItemId",
                schema: "projects",
                table: "CatalogItem",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MasterItem",
                schema: "projects",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    NameAr = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    NameEn = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    Category = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    Unit = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Status = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    UpdatedBy = table.Column<int>(type: "int", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MasterItem", x => x.Id);
                    table.CheckConstraint("CK_MasterItem_Category", "([Kind] IN ('Work', 'Composite') AND [Category] IN ('Concrete', 'SteelFixing', 'Formwork', 'Brickwork', 'Plastering', 'Tiling', 'Painting', 'Excavation', 'Waterproofing', 'Piling', 'Mep', 'Guarding', 'EquipmentOperation', 'General')) OR ([Kind] NOT IN ('Work', 'Composite') AND [Category] IN ('CementAggregates', 'ReadyMix', 'Steel', 'Masonry', 'Finishing', 'Waterproofing', 'Piling', 'Formwork', 'Consumables', 'Services', 'Other'))");
                    table.CheckConstraint("CK_MasterItem_Kind", "[Kind] IN ('Material', 'Service', 'Work', 'Composite', 'Labor', 'Equipment')");
                    table.CheckConstraint("CK_MasterItem_Status", "[Status] IN ('Active', 'Inactive')");
                    table.CheckConstraint("CK_MasterItem_Unit", "[Unit] IN ('Bag', 'Ton', 'M3', 'M2', 'M', 'Piece', 'Trip', 'Roll', 'Lot', 'Job', 'Day', 'Visit', 'Brick', 'Liter', 'Kg', 'Other')");
                });

            migrationBuilder.CreateTable(
                name: "PriceVariance",
                schema: "projects",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    ExpenseId = table.Column<int>(type: "int", nullable: false),
                    CatalogItemId = table.Column<int>(type: "int", nullable: false),
                    ReferencePrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    ActualPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    UnitVariance = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    TotalVariance = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    VariancePercent = table.Column<decimal>(type: "decimal(9,2)", precision: 9, scale: 2, nullable: false),
                    Direction = table.Column<string>(type: "varchar(10)", unicode: false, maxLength: 10, nullable: false),
                    TolerancePercent = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    RoundingAllowance = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Status = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    RaisedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    RaisedBy = table.Column<int>(type: "int", nullable: false),
                    DecidedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    DecidedBy = table.Column<int>(type: "int", nullable: true),
                    DecisionNote = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PriceVariance", x => x.Id);
                    table.CheckConstraint("CK_PriceVariance_Decision", "([Status] = 'Open' AND [DecidedAt] IS NULL AND [DecidedBy] IS NULL) OR ([Status] <> 'Open' AND [DecidedAt] IS NOT NULL AND [DecidedBy] IS NOT NULL)");
                    table.CheckConstraint("CK_PriceVariance_Direction", "[Direction] IN ('Above', 'Below')");
                    table.CheckConstraint("CK_PriceVariance_Prices", "[ReferencePrice] > 0 AND [ActualPrice] > 0 AND [Quantity] > 0");
                    table.CheckConstraint("CK_PriceVariance_Status", "[Status] IN ('Open', 'Accepted', 'Rejected', 'Superseded')");
                    table.ForeignKey(
                        name: "FK_PriceVariance_CatalogItem_CatalogItemId",
                        column: x => x.CatalogItemId,
                        principalSchema: "projects",
                        principalTable: "CatalogItem",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_PriceVariance_ProjectExpense_ExpenseId",
                        column: x => x.ExpenseId,
                        principalSchema: "projects",
                        principalTable: "ProjectExpense",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_PriceVariance_Project_ProjectId",
                        column: x => x.ProjectId,
                        principalSchema: "projects",
                        principalTable: "Project",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "PricingPolicy",
                schema: "projects",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    VarianceTolerancePercent = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    RoundingAllowance = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    UpdatedBy = table.Column<int>(type: "int", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PricingPolicy", x => x.Id);
                    table.CheckConstraint("CK_PricingPolicy_Rounding", "[RoundingAllowance] >= 0");
                    table.CheckConstraint("CK_PricingPolicy_Singleton", "[Id] = 1");
                    table.CheckConstraint("CK_PricingPolicy_Tolerance", "[VarianceTolerancePercent] >= 0 AND [VarianceTolerancePercent] <= 100");
                });

            migrationBuilder.CreateTable(
                name: "ProjectItemComponent",
                schema: "projects",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProjectItemId = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    CatalogItemId = table.Column<int>(type: "int", nullable: true),
                    PlannedQuantity = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: true),
                    QuantityPerUnit = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectItemComponent", x => x.Id);
                    table.CheckConstraint("CK_ProjectItemComponent_Kind", "[Kind] IN ('Material', 'Labor', 'Equipment', 'Subcontract')");
                    table.CheckConstraint("CK_ProjectItemComponent_Material", "([Kind] = 'Material' AND [CatalogItemId] IS NOT NULL) OR ([Kind] <> 'Material' AND [CatalogItemId] IS NULL)");
                    table.CheckConstraint("CK_ProjectItemComponent_Quantities", "([PlannedQuantity] IS NULL OR [PlannedQuantity] > 0) AND ([QuantityPerUnit] IS NULL OR [QuantityPerUnit] > 0)");
                    table.ForeignKey(
                        name: "FK_ProjectItemComponent_CatalogItem_CatalogItemId",
                        column: x => x.CatalogItemId,
                        principalSchema: "projects",
                        principalTable: "CatalogItem",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ProjectItemComponent_ProjectItem_ProjectItemId",
                        column: x => x.ProjectItemId,
                        principalSchema: "projects",
                        principalTable: "ProjectItem",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "MasterItemPrice",
                schema: "projects",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MasterItemId = table.Column<int>(type: "int", nullable: false),
                    Price = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    PreviousPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    EffectiveFrom = table.Column<DateOnly>(type: "date", nullable: false),
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
                    table.PrimaryKey("PK_MasterItemPrice", x => x.Id);
                    table.CheckConstraint("CK_MasterItemPrice_Decision", "([Status] = 'Pending' AND [DecidedAt] IS NULL AND [DecidedBy] IS NULL) OR ([Status] <> 'Pending' AND [DecidedAt] IS NOT NULL AND [DecidedBy] IS NOT NULL)");
                    table.CheckConstraint("CK_MasterItemPrice_Price", "[Price] > 0 AND ([PreviousPrice] IS NULL OR [PreviousPrice] > 0)");
                    table.CheckConstraint("CK_MasterItemPrice_Status", "[Status] IN ('Pending', 'Approved', 'Rejected')");
                    table.ForeignKey(
                        name: "FK_MasterItemPrice_MasterItem_MasterItemId",
                        column: x => x.MasterItemId,
                        principalSchema: "projects",
                        principalTable: "MasterItem",
                        principalColumn: "Id");
                });

            migrationBuilder.InsertData(
                schema: "projects",
                table: "PricingPolicy",
                columns: new[] { "Id", "RoundingAllowance", "UpdatedAt", "UpdatedBy", "VarianceTolerancePercent" },
                values: new object[] { 1, 0.01m, null, null, 0m });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectItem_MasterItemId",
                schema: "projects",
                table: "ProjectItem",
                column: "MasterItemId");

            migrationBuilder.CreateIndex(
                name: "UX_ProjectItem_Master",
                schema: "projects",
                table: "ProjectItem",
                columns: new[] { "ProjectId", "MasterItemId" },
                unique: true,
                filter: "[MasterItemId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CatalogItem_MasterItemId",
                schema: "projects",
                table: "CatalogItem",
                column: "MasterItemId");

            migrationBuilder.CreateIndex(
                name: "UX_CatalogItem_Master",
                schema: "projects",
                table: "CatalogItem",
                columns: new[] { "ProjectId", "MasterItemId" },
                unique: true,
                filter: "[MasterItemId] IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_BudgetLine_CostGroup",
                schema: "projects",
                table: "BudgetLine",
                sql: "[CostGroup] IN ('Labor', 'Subcontractors', 'Materials', 'Equipment', 'SiteExpenses', 'Other', 'Staff')");

            migrationBuilder.CreateIndex(
                name: "UX_MasterItem_NameAr",
                schema: "projects",
                table: "MasterItem",
                column: "NameAr",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_MasterItem_NameEn",
                schema: "projects",
                table: "MasterItem",
                column: "NameEn",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MasterItemPrice_Status",
                schema: "projects",
                table: "MasterItemPrice",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "UX_MasterItemPrice_ApprovedFrom",
                schema: "projects",
                table: "MasterItemPrice",
                columns: new[] { "MasterItemId", "EffectiveFrom" },
                unique: true,
                filter: "[Status] = 'Approved'");

            migrationBuilder.CreateIndex(
                name: "UX_MasterItemPrice_Pending",
                schema: "projects",
                table: "MasterItemPrice",
                column: "MasterItemId",
                unique: true,
                filter: "[Status] = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "IX_PriceVariance_CatalogItemId",
                schema: "projects",
                table: "PriceVariance",
                column: "CatalogItemId");

            migrationBuilder.CreateIndex(
                name: "IX_PriceVariance_Project_Status",
                schema: "projects",
                table: "PriceVariance",
                columns: new[] { "ProjectId", "Status" });

            migrationBuilder.CreateIndex(
                name: "UX_PriceVariance_Active",
                schema: "projects",
                table: "PriceVariance",
                column: "ExpenseId",
                unique: true,
                filter: "[Status] IN ('Open', 'Rejected')");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectItemComponent_CatalogItemId",
                schema: "projects",
                table: "ProjectItemComponent",
                column: "CatalogItemId");

            migrationBuilder.CreateIndex(
                name: "UX_ProjectItemComponent_Kind",
                schema: "projects",
                table: "ProjectItemComponent",
                columns: new[] { "ProjectItemId", "Kind" },
                unique: true,
                filter: "[Kind] <> 'Material'");

            migrationBuilder.CreateIndex(
                name: "UX_ProjectItemComponent_Material",
                schema: "projects",
                table: "ProjectItemComponent",
                columns: new[] { "ProjectItemId", "CatalogItemId" },
                unique: true,
                filter: "[CatalogItemId] IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_CatalogItem_MasterItem_MasterItemId",
                schema: "projects",
                table: "CatalogItem",
                column: "MasterItemId",
                principalSchema: "projects",
                principalTable: "MasterItem",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_ProjectItem_MasterItem_MasterItemId",
                schema: "projects",
                table: "ProjectItem",
                column: "MasterItemId",
                principalSchema: "projects",
                principalTable: "MasterItem",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CatalogItem_MasterItem_MasterItemId",
                schema: "projects",
                table: "CatalogItem");

            migrationBuilder.DropForeignKey(
                name: "FK_ProjectItem_MasterItem_MasterItemId",
                schema: "projects",
                table: "ProjectItem");

            migrationBuilder.DropTable(
                name: "MasterItemPrice",
                schema: "projects");

            migrationBuilder.DropTable(
                name: "PriceVariance",
                schema: "projects");

            migrationBuilder.DropTable(
                name: "PricingPolicy",
                schema: "projects");

            migrationBuilder.DropTable(
                name: "ProjectItemComponent",
                schema: "projects");

            migrationBuilder.DropTable(
                name: "MasterItem",
                schema: "projects");

            migrationBuilder.DropIndex(
                name: "IX_ProjectItem_MasterItemId",
                schema: "projects",
                table: "ProjectItem");

            migrationBuilder.DropIndex(
                name: "UX_ProjectItem_Master",
                schema: "projects",
                table: "ProjectItem");

            migrationBuilder.DropIndex(
                name: "IX_CatalogItem_MasterItemId",
                schema: "projects",
                table: "CatalogItem");

            migrationBuilder.DropIndex(
                name: "UX_CatalogItem_Master",
                schema: "projects",
                table: "CatalogItem");

            migrationBuilder.DropCheckConstraint(
                name: "CK_BudgetLine_CostGroup",
                schema: "projects",
                table: "BudgetLine");

            migrationBuilder.DropColumn(
                name: "Address",
                schema: "projects",
                table: "Supplier");

            migrationBuilder.DropColumn(
                name: "CommercialRegistrationNo",
                schema: "projects",
                table: "Supplier");

            migrationBuilder.DropColumn(
                name: "ContactPerson",
                schema: "projects",
                table: "Supplier");

            migrationBuilder.DropColumn(
                name: "Email",
                schema: "projects",
                table: "Supplier");

            migrationBuilder.DropColumn(
                name: "LegalName",
                schema: "projects",
                table: "Supplier");

            migrationBuilder.DropColumn(
                name: "MasterItemId",
                schema: "projects",
                table: "ProjectItem");

            migrationBuilder.DropColumn(
                name: "MasterItemId",
                schema: "projects",
                table: "CatalogItem");

            migrationBuilder.AddCheckConstraint(
                name: "CK_BudgetLine_CostGroup",
                schema: "projects",
                table: "BudgetLine",
                sql: "[CostGroup] IN ('Labor', 'Subcontractors', 'Materials', 'Equipment', 'SiteExpenses', 'Other')");
        }
    }
}
