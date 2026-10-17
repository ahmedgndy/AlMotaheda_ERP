using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlMotaheda.Modules.Archive.Data.Migrations
{
    /// <inheritdoc />
    internal partial class SupplierAndRepairLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_DocumentLink_EntityType",
                schema: "docs",
                table: "DocumentLink");

            migrationBuilder.AddCheckConstraint(
                name: "CK_DocumentLink_EntityType",
                schema: "docs",
                table: "DocumentLink",
                sql: "[EntityType] IN ('Expense', 'SupplierPayment', 'DailyReport', 'ReportRequest', 'LaborSheet', 'SubContract', 'SubPayment', 'Equipment', 'EquipmentCost', 'Budget', 'Supplier', 'RepairRequest')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_DocumentLink_EntityType",
                schema: "docs",
                table: "DocumentLink");

            migrationBuilder.AddCheckConstraint(
                name: "CK_DocumentLink_EntityType",
                schema: "docs",
                table: "DocumentLink",
                sql: "[EntityType] IN ('Expense', 'SupplierPayment', 'DailyReport', 'ReportRequest', 'LaborSheet', 'SubContract', 'SubPayment', 'Equipment', 'EquipmentCost', 'Budget')");
        }
    }
}
