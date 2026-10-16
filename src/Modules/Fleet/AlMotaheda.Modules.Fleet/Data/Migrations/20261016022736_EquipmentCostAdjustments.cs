using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlMotaheda.Modules.Fleet.Data.Migrations
{
    /// <inheritdoc />
    internal partial class EquipmentCostAdjustments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EquipmentCostAdjustment",
                schema: "fleet",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CostId = table.Column<int>(type: "int", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    RequestedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    RequestedBy = table.Column<int>(type: "int", nullable: false),
                    DecidedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    DecidedBy = table.Column<int>(type: "int", nullable: true),
                    DecisionNote = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EquipmentCostAdjustment", x => x.Id);
                    table.CheckConstraint("CK_EquipmentCostAdjustment_Amount", "[Amount] <> 0");
                    table.CheckConstraint("CK_EquipmentCostAdjustment_Status", "[Status] IN ('Requested', 'Approved', 'Rejected')");
                    table.ForeignKey(
                        name: "FK_EquipmentCostAdjustment_EquipmentCost_CostId",
                        column: x => x.CostId,
                        principalSchema: "fleet",
                        principalTable: "EquipmentCost",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_EquipmentCostAdjustment_Cost_Status",
                schema: "fleet",
                table: "EquipmentCostAdjustment",
                columns: new[] { "CostId", "Status" });

            migrationBuilder.CreateIndex(
                name: "UX_EquipmentCostAdjustment_OneOpen",
                schema: "fleet",
                table: "EquipmentCostAdjustment",
                column: "CostId",
                unique: true,
                filter: "[Status] = 'Requested'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EquipmentCostAdjustment",
                schema: "fleet");
        }
    }
}
