using System;
using AlMotaheda.Modules.Projects.Data.Configurations;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlMotaheda.Modules.Projects.Data.Migrations
{
    /// <summary>
    /// Cash custody and supply agreements (docs/project-workspace/09-project-financial-workspace.md): the custody
    /// movements table (fundings, returns, wages paid from a custody), the supply agreements table, and two optional
    /// columns on records (the agreement, and the payee of a purchase paid in cash with no supplier). Only adds: no
    /// row is changed or removed. The record check is widened (a custody purchase may name a payee instead of a
    /// supplier); every existing row met the old check, so it meets the new one.
    /// </summary>
    /// <remarks>
    /// Down restores the old record check; it fails if a custody purchase without a supplier was recorded since,
    /// which must then be given a supplier or removed first. Down drops the custody movements and agreements.
    /// </remarks>
    internal partial class CustodyAndSupply : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ProjectExpense_KindColumns",
                schema: "projects",
                table: "ProjectExpense");

            migrationBuilder.AddColumn<string>(
                name: "PayeeName",
                schema: "projects",
                table: "ProjectExpense",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SupplyAgreementId",
                schema: "projects",
                table: "ProjectExpense",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CustodyEntry",
                schema: "projects",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    HolderUserId = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    EntryDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Method = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Status = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    SourceType = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: true),
                    SourceId = table.Column<int>(type: "int", nullable: true),
                    IdempotencyKey = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    UpdatedBy = table.Column<int>(type: "int", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustodyEntry", x => x.Id);
                    table.CheckConstraint("CK_CustodyEntry_Amount", "[Amount] > 0");
                    table.CheckConstraint("CK_CustodyEntry_Kind", "[Kind] IN ('Funding', 'Return', 'Payroll')");
                    table.CheckConstraint("CK_CustodyEntry_Pending", "[Status] = 'Posted' OR [Kind] = 'Payroll'");
                    table.CheckConstraint("CK_CustodyEntry_Source", "([SourceType] IS NULL AND [SourceId] IS NULL) OR ([SourceType] IS NOT NULL AND [SourceId] IS NOT NULL)");
                    table.CheckConstraint("CK_CustodyEntry_Status", "[Status] IN ('Pending', 'Posted', 'Released')");
                    table.ForeignKey(
                        name: "FK_CustodyEntry_Project_ProjectId",
                        column: x => x.ProjectId,
                        principalSchema: "projects",
                        principalTable: "Project",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "SupplyAgreement",
                schema: "projects",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    SupplierId = table.Column<int>(type: "int", nullable: false),
                    ProjectItemId = table.Column<int>(type: "int", nullable: false),
                    MaterialName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Unit = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    AgreedQuantity = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: true),
                    AgreedUnitPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    AgreedValue = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: true),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Status = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    UpdatedBy = table.Column<int>(type: "int", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplyAgreement", x => x.Id);
                    table.CheckConstraint("CK_SupplyAgreement_Agreed", "([AgreedQuantity] IS NULL OR [AgreedQuantity] > 0) AND ([AgreedUnitPrice] IS NULL OR [AgreedUnitPrice] > 0) AND ([AgreedValue] IS NULL OR [AgreedValue] > 0)");
                    table.CheckConstraint("CK_SupplyAgreement_Dates", "[EndDate] IS NULL OR [StartDate] IS NULL OR [EndDate] >= [StartDate]");
                    table.CheckConstraint("CK_SupplyAgreement_Status", "[Status] IN ('Active', 'Closed')");
                    table.CheckConstraint("CK_SupplyAgreement_Unit", "[Unit] IN ('Bag', 'Ton', 'M3', 'M2', 'M', 'Piece', 'Trip', 'Roll', 'Lot', 'Job', 'Day', 'Visit', 'Brick', 'Liter', 'Kg', 'Other')");
                    table.ForeignKey(
                        name: "FK_SupplyAgreement_ProjectItem_Project_Item",
                        columns: x => new { x.ProjectId, x.ProjectItemId },
                        principalSchema: "projects",
                        principalTable: "ProjectItem",
                        principalColumns: new[] { "ProjectId", "Id" });
                    table.ForeignKey(
                        name: "FK_SupplyAgreement_Project_ProjectId",
                        column: x => x.ProjectId,
                        principalSchema: "projects",
                        principalTable: "Project",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SupplyAgreement_Supplier_SupplierId",
                        column: x => x.SupplierId,
                        principalSchema: "projects",
                        principalTable: "Supplier",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectExpense_Project_Recorder_Channel",
                schema: "projects",
                table: "ProjectExpense",
                columns: new[] { "ProjectId", "CreatedBy", "Channel" });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectExpense_SupplyAgreementId",
                schema: "projects",
                table: "ProjectExpense",
                column: "SupplyAgreementId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ProjectExpense_KindColumns",
                schema: "projects",
                table: "ProjectExpense",
                sql: "([Kind] = 'Supply' AND ([SupplierId] IS NOT NULL OR ([Channel] = 'Custody' AND [PayeeName] IS NOT NULL)) AND [ItemName] IS NOT NULL AND [Unit] IS NOT NULL AND [Quantity] IS NOT NULL AND [UnitPrice] IS NOT NULL AND [Quantity] > 0 AND [UnitPrice] > 0 AND [Category] IS NULL AND ABS([Amount] - ROUND([Quantity] * [UnitPrice], 2)) < 0.01) OR ([Kind] = 'Expense' AND [Category] IS NOT NULL AND [Description] IS NOT NULL AND [ItemName] IS NULL AND [Unit] IS NULL AND [Quantity] IS NULL AND [UnitPrice] IS NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_CustodyEntry_Project_Holder",
                schema: "projects",
                table: "CustodyEntry",
                columns: new[] { "ProjectId", "HolderUserId" });

            migrationBuilder.CreateIndex(
                name: "UX_CustodyEntry_IdempotencyKey",
                schema: "projects",
                table: "CustodyEntry",
                column: "IdempotencyKey",
                unique: true,
                filter: "[IdempotencyKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_CustodyEntry_Source",
                schema: "projects",
                table: "CustodyEntry",
                columns: new[] { "SourceType", "SourceId" },
                unique: true,
                filter: "[SourceType] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SupplyAgreement_ProjectId_ProjectItemId",
                schema: "projects",
                table: "SupplyAgreement",
                columns: new[] { "ProjectId", "ProjectItemId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplyAgreement_ProjectItemId",
                schema: "projects",
                table: "SupplyAgreement",
                column: "ProjectItemId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplyAgreement_SupplierId",
                schema: "projects",
                table: "SupplyAgreement",
                column: "SupplierId");

            migrationBuilder.CreateIndex(
                name: "UX_SupplyAgreement_Project_Supplier_Item_Material",
                schema: "projects",
                table: "SupplyAgreement",
                columns: new[] { "ProjectId", "SupplierId", "ProjectItemId", "MaterialName" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_ProjectExpense_SupplyAgreement_SupplyAgreementId",
                schema: "projects",
                table: "ProjectExpense",
                column: "SupplyAgreementId",
                principalSchema: "projects",
                principalTable: "SupplyAgreement",
                principalColumn: "Id");

            // The holder is an Access record: this key is added by hand (Access owns the table).
            migrationBuilder.AddForeignKey(
                name: CustodyEntryConfiguration.HolderForeignKey,
                schema: "projects",
                table: "CustodyEntry",
                column: "HolderUserId",
                principalSchema: "access",
                principalTable: "User",
                principalColumn: "Id",
                onDelete: ReferentialAction.NoAction);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: CustodyEntryConfiguration.HolderForeignKey,
                schema: "projects",
                table: "CustodyEntry");

            migrationBuilder.DropForeignKey(
                name: "FK_ProjectExpense_SupplyAgreement_SupplyAgreementId",
                schema: "projects",
                table: "ProjectExpense");

            migrationBuilder.DropTable(
                name: "CustodyEntry",
                schema: "projects");

            migrationBuilder.DropTable(
                name: "SupplyAgreement",
                schema: "projects");

            migrationBuilder.DropIndex(
                name: "IX_ProjectExpense_Project_Recorder_Channel",
                schema: "projects",
                table: "ProjectExpense");

            migrationBuilder.DropIndex(
                name: "IX_ProjectExpense_SupplyAgreementId",
                schema: "projects",
                table: "ProjectExpense");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ProjectExpense_KindColumns",
                schema: "projects",
                table: "ProjectExpense");

            migrationBuilder.DropColumn(
                name: "PayeeName",
                schema: "projects",
                table: "ProjectExpense");

            migrationBuilder.DropColumn(
                name: "SupplyAgreementId",
                schema: "projects",
                table: "ProjectExpense");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ProjectExpense_KindColumns",
                schema: "projects",
                table: "ProjectExpense",
                sql: "([Kind] = 'Supply' AND [SupplierId] IS NOT NULL AND [ItemName] IS NOT NULL AND [Unit] IS NOT NULL AND [Quantity] IS NOT NULL AND [UnitPrice] IS NOT NULL AND [Quantity] > 0 AND [UnitPrice] > 0 AND [Category] IS NULL AND ABS([Amount] - ROUND([Quantity] * [UnitPrice], 2)) < 0.01) OR ([Kind] = 'Expense' AND [Category] IS NOT NULL AND [Description] IS NOT NULL AND [ItemName] IS NULL AND [Unit] IS NULL AND [Quantity] IS NULL AND [UnitPrice] IS NULL)");
        }
    }
}
