using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlMotaheda.Modules.Archive.Data.Migrations
{
    /// <summary>
    /// Documents can be linked to custody movements, supply agreements, weekly labor settlements and subcontract
    /// variations (their receipts and signed sheets). Only the allowed link types are widened; no row is changed.
    /// </summary>
    /// <remarks>Down fails if a document is linked to one of the new kinds; unlink it first.</remarks>
    internal partial class CustodyAndSettlementLinks : Migration
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
                sql: "[EntityType] IN ('Expense', 'SupplierPayment', 'DailyReport', 'ReportRequest', 'LaborSheet', 'SubContract', 'SubPayment', 'Equipment', 'EquipmentCost', 'Budget', 'CustodyEntry', 'SupplyAgreement', 'PayrollSettlement', 'ContractVariation')");
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
