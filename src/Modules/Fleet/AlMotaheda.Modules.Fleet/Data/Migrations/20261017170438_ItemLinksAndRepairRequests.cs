using System;
using AlMotaheda.Modules.Fleet.Data.Configurations;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlMotaheda.Modules.Fleet.Data.Migrations
{
    /// <inheritdoc />
    internal partial class ItemLinksAndRepairRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_EquipmentCost_Type",
                schema: "fleet",
                table: "EquipmentCost");

            migrationBuilder.AddColumn<DateTime>(
                name: "ItemLinkedAt",
                schema: "fleet",
                table: "EquipmentCost",
                type: "datetime2(3)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ItemLinkedBy",
                schema: "fleet",
                table: "EquipmentCost",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ProjectItemId",
                schema: "fleet",
                table: "EquipmentCost",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "RepairRequest",
                schema: "fleet",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EquipmentId = table.Column<int>(type: "int", nullable: false),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    Problem = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    Priority = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    RequestedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    RequestedBy = table.Column<int>(type: "int", nullable: false),
                    ReviewedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ReviewedBy = table.Column<int>(type: "int", nullable: true),
                    ReviewNote = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    CompletedBy = table.Column<int>(type: "int", nullable: true),
                    Result = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    EquipmentCostId = table.Column<int>(type: "int", nullable: true),
                    CancelledAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    CancelledBy = table.Column<int>(type: "int", nullable: true),
                    CancelReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RepairRequest", x => x.Id);
                    table.CheckConstraint("CK_RepairRequest_Completion", "([Status] = 'Completed' AND [CompletedAt] IS NOT NULL AND [CompletedBy] IS NOT NULL AND [Result] IS NOT NULL) OR ([Status] <> 'Completed' AND [CompletedAt] IS NULL AND [EquipmentCostId] IS NULL)");
                    table.CheckConstraint("CK_RepairRequest_Priority", "[Priority] IN ('Low', 'Normal', 'Urgent')");
                    table.CheckConstraint("CK_RepairRequest_Review", "([Status] = 'Open' AND [ReviewedAt] IS NULL AND [ReviewedBy] IS NULL) OR [Status] = 'Cancelled' OR ([Status] IN ('Approved', 'Rejected', 'Completed') AND [ReviewedAt] IS NOT NULL AND [ReviewedBy] IS NOT NULL)");
                    table.CheckConstraint("CK_RepairRequest_Status", "[Status] IN ('Open', 'Approved', 'Rejected', 'Completed', 'Cancelled')");
                    table.ForeignKey(
                        name: "FK_RepairRequest_EquipmentCost_EquipmentCostId",
                        column: x => x.EquipmentCostId,
                        principalSchema: "fleet",
                        principalTable: "EquipmentCost",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_RepairRequest_Equipment_EquipmentId",
                        column: x => x.EquipmentId,
                        principalSchema: "fleet",
                        principalTable: "Equipment",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_EquipmentCost_Equipment_Date",
                schema: "fleet",
                table: "EquipmentCost",
                columns: new[] { "EquipmentId", "CostDate" });

            migrationBuilder.CreateIndex(
                name: "IX_EquipmentCost_Project_Item",
                schema: "fleet",
                table: "EquipmentCost",
                columns: new[] { "ProjectId", "ProjectItemId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_EquipmentCost_Type",
                schema: "fleet",
                table: "EquipmentCost",
                sql: "[Type] IN ('Rental', 'Fuel', 'Maintenance', 'Repair', 'SpareParts', 'Transport', 'Other')");

            migrationBuilder.CreateIndex(
                name: "IX_RepairRequest_EquipmentCostId",
                schema: "fleet",
                table: "RepairRequest",
                column: "EquipmentCostId");

            migrationBuilder.CreateIndex(
                name: "IX_RepairRequest_EquipmentId",
                schema: "fleet",
                table: "RepairRequest",
                column: "EquipmentId");

            migrationBuilder.CreateIndex(
                name: "IX_RepairRequest_Project_Status",
                schema: "fleet",
                table: "RepairRequest",
                columns: new[] { "ProjectId", "Status" });

            // Cross-schema keys, written by hand like every key to the projects schema.
            migrationBuilder.AddForeignKey(
                name: EquipmentCostConfiguration.ProjectItemForeignKey,
                schema: "fleet",
                table: "EquipmentCost",
                column: "ProjectItemId",
                principalSchema: "projects",
                principalTable: "ProjectItem",
                principalColumn: "Id",
                onDelete: ReferentialAction.NoAction);

            migrationBuilder.AddForeignKey(
                name: RepairRequestConfiguration.ProjectForeignKey,
                schema: "fleet",
                table: "RepairRequest",
                column: "ProjectId",
                principalSchema: "projects",
                principalTable: "Project",
                principalColumn: "Id",
                onDelete: ReferentialAction.NoAction);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(name: RepairRequestConfiguration.ProjectForeignKey, schema: "fleet", table: "RepairRequest");
            migrationBuilder.DropForeignKey(name: EquipmentCostConfiguration.ProjectItemForeignKey, schema: "fleet", table: "EquipmentCost");

            migrationBuilder.DropTable(
                name: "RepairRequest",
                schema: "fleet");

            migrationBuilder.DropIndex(
                name: "IX_EquipmentCost_Equipment_Date",
                schema: "fleet",
                table: "EquipmentCost");

            migrationBuilder.DropIndex(
                name: "IX_EquipmentCost_Project_Item",
                schema: "fleet",
                table: "EquipmentCost");

            migrationBuilder.DropCheckConstraint(
                name: "CK_EquipmentCost_Type",
                schema: "fleet",
                table: "EquipmentCost");

            migrationBuilder.DropColumn(
                name: "ItemLinkedAt",
                schema: "fleet",
                table: "EquipmentCost");

            migrationBuilder.DropColumn(
                name: "ItemLinkedBy",
                schema: "fleet",
                table: "EquipmentCost");

            migrationBuilder.DropColumn(
                name: "ProjectItemId",
                schema: "fleet",
                table: "EquipmentCost");

            migrationBuilder.AddCheckConstraint(
                name: "CK_EquipmentCost_Type",
                schema: "fleet",
                table: "EquipmentCost",
                sql: "[Type] IN ('Rental', 'Fuel', 'Maintenance', 'Repair', 'Transport', 'Other')");
        }
    }
}
