using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlMotaheda.Modules.Projects.Data.Migrations
{
    /// <summary>
    /// After merging the Office module: a price exception's source is one of the project expense, the supplier invoice
    /// line, the vehicle expense (Fleet) or the office expense (Office); each branch's migration had re-created the
    /// check constraint with its own list. The nullable project of a company-office exception was already made so by
    /// <c>OfficePriceExceptions</c>. Additive.
    /// </summary>
    internal partial class OfficeAndTreasuryCodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_PriceException_SourceType",
                schema: "projects",
                table: "PriceException");


            migrationBuilder.AddCheckConstraint(
                name: "CK_PriceException_SourceType",
                schema: "projects",
                table: "PriceException",
                sql: "[SourceType] IN ('ProjectExpense', 'SupplierInvoiceLine', 'VehicleExpense', 'OfficeExpense')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_PriceException_SourceType",
                schema: "projects",
                table: "PriceException");


            migrationBuilder.AddCheckConstraint(
                name: "CK_PriceException_SourceType",
                schema: "projects",
                table: "PriceException",
                sql: "[SourceType] IN ('ProjectExpense', 'SupplierInvoiceLine', 'VehicleExpense')");
        }
    }
}
