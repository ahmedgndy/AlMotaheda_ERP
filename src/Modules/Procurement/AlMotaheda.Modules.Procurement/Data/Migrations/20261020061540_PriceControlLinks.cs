using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlMotaheda.Modules.Procurement.Data.Migrations
{
    /// <summary>
    /// Price Control on the purchasing chain (docs/project-workspace/10-price-control.md): a requisition line and an
    /// order line may name the price-list item their price is checked against; an invoice line keeps the result of
    /// that check and the case it opened; an invoice may wait in PriceHold for the Executive. Only adds: no row
    /// changes. The price item and case live in the Projects schema and are not foreign keys here (as the catalog
    /// item on the same lines).
    /// </summary>
    /// <remarks>Down restores the old status check; it fails while an invoice is in PriceHold.</remarks>
    internal partial class PriceControlLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_SupplierInvoice_Status",
                schema: "proc",
                table: "SupplierInvoice");

            migrationBuilder.AddColumn<string>(
                name: "PriceCheck",
                schema: "proc",
                table: "SupplierInvoiceLine",
                type: "varchar(20)",
                unicode: false,
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PriceExceptionId",
                schema: "proc",
                table: "SupplierInvoiceLine",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PriceItemId",
                schema: "proc",
                table: "RequisitionLine",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PriceItemId",
                schema: "proc",
                table: "PurchaseOrderLine",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SupplierInvoiceLine_PriceExceptionId",
                schema: "proc",
                table: "SupplierInvoiceLine",
                column: "PriceExceptionId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SupplierInvoice_Status",
                schema: "proc",
                table: "SupplierInvoice",
                sql: "[Status] IN ('Matched', 'Mismatch', 'Posted', 'Returned', 'PriceHold')");

            migrationBuilder.CreateIndex(
                name: "IX_RequisitionLine_PriceItemId",
                schema: "proc",
                table: "RequisitionLine",
                column: "PriceItemId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrderLine_PriceItemId",
                schema: "proc",
                table: "PurchaseOrderLine",
                column: "PriceItemId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SupplierInvoiceLine_PriceExceptionId",
                schema: "proc",
                table: "SupplierInvoiceLine");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SupplierInvoice_Status",
                schema: "proc",
                table: "SupplierInvoice");

            migrationBuilder.DropIndex(
                name: "IX_RequisitionLine_PriceItemId",
                schema: "proc",
                table: "RequisitionLine");

            migrationBuilder.DropIndex(
                name: "IX_PurchaseOrderLine_PriceItemId",
                schema: "proc",
                table: "PurchaseOrderLine");

            migrationBuilder.DropColumn(
                name: "PriceCheck",
                schema: "proc",
                table: "SupplierInvoiceLine");

            migrationBuilder.DropColumn(
                name: "PriceExceptionId",
                schema: "proc",
                table: "SupplierInvoiceLine");

            migrationBuilder.DropColumn(
                name: "PriceItemId",
                schema: "proc",
                table: "RequisitionLine");

            migrationBuilder.DropColumn(
                name: "PriceItemId",
                schema: "proc",
                table: "PurchaseOrderLine");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SupplierInvoice_Status",
                schema: "proc",
                table: "SupplierInvoice",
                sql: "[Status] IN ('Matched', 'Mismatch', 'Posted', 'Returned')");
        }
    }
}
