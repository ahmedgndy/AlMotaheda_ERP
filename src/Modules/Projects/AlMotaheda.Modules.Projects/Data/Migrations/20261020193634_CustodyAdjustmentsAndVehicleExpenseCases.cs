using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlMotaheda.Modules.Projects.Data.Migrations
{
    /// <summary>
    /// A project custody entry may be a reviewed adjustment up or down (after a cash count, written by Finance's
    /// custody controls with a reason and evidence), and a price exception case may be about a vehicle expense claim
    /// (Fleet). Only two allowed-value checks are widened; no row is changed.
    /// </summary>
    /// <remarks>Down fails if an adjustment entry or a vehicle-expense case exists; remove them first.</remarks>
    internal partial class CustodyAdjustmentsAndVehicleExpenseCases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_PriceException_SourceType",
                schema: "projects",
                table: "PriceException");

            migrationBuilder.DropCheckConstraint(
                name: "CK_CustodyEntry_Kind",
                schema: "projects",
                table: "CustodyEntry");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PriceException_SourceType",
                schema: "projects",
                table: "PriceException",
                sql: "[SourceType] IN ('ProjectExpense', 'SupplierInvoiceLine', 'VehicleExpense')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_CustodyEntry_Kind",
                schema: "projects",
                table: "CustodyEntry",
                sql: "[Kind] IN ('Funding', 'Return', 'Payroll', 'AdjustmentIn', 'AdjustmentOut')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_PriceException_SourceType",
                schema: "projects",
                table: "PriceException");

            migrationBuilder.DropCheckConstraint(
                name: "CK_CustodyEntry_Kind",
                schema: "projects",
                table: "CustodyEntry");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PriceException_SourceType",
                schema: "projects",
                table: "PriceException",
                sql: "[SourceType] IN ('ProjectExpense', 'SupplierInvoiceLine')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_CustodyEntry_Kind",
                schema: "projects",
                table: "CustodyEntry",
                sql: "[Kind] IN ('Funding', 'Return', 'Payroll')");
        }
    }
}
