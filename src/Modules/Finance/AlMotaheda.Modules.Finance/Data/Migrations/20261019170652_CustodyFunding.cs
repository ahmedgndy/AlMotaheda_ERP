using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlMotaheda.Modules.Finance.Data.Migrations
{
    /// <summary>
    /// Custody funding: a payment request can now pay a custody holder (payee CustodyHolder, purpose CustodyAdvance,
    /// source CustodyFunding). Only the allowed values of three checks are widened; no row is changed.
    /// </summary>
    /// <remarks>Down fails if a custody funding request exists; such requests must be removed first.</remarks>
    internal partial class CustodyFunding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_PaymentRequest_PayeeType",
                schema: "finance",
                table: "PaymentRequest");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PaymentRequest_Purpose",
                schema: "finance",
                table: "PaymentRequest");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PaymentRequest_SourceType",
                schema: "finance",
                table: "PaymentRequest");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PaymentRequest_PayeeType",
                schema: "finance",
                table: "PaymentRequest",
                sql: "[PayeeType] IN ('Supplier', 'Worker', 'Subcontractor', 'Employees', 'CustodyHolder')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PaymentRequest_Purpose",
                schema: "finance",
                table: "PaymentRequest",
                sql: "[Purpose] IN ('SupplierInvoices', 'WorkerWages', 'SubcontractWork', 'SubcontractAdvance', 'EquipmentCost', 'Salaries', 'CustodyAdvance')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PaymentRequest_SourceType",
                schema: "finance",
                table: "PaymentRequest",
                sql: "[SourceType] IN ('SupplierPayment', 'WorkerPayment', 'SubcontractPayment', 'EquipmentCostPayment', 'SalarySheetPayment', 'CustodyFunding')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_PaymentRequest_PayeeType",
                schema: "finance",
                table: "PaymentRequest");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PaymentRequest_Purpose",
                schema: "finance",
                table: "PaymentRequest");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PaymentRequest_SourceType",
                schema: "finance",
                table: "PaymentRequest");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PaymentRequest_PayeeType",
                schema: "finance",
                table: "PaymentRequest",
                sql: "[PayeeType] IN ('Supplier', 'Worker', 'Subcontractor', 'Employees')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PaymentRequest_Purpose",
                schema: "finance",
                table: "PaymentRequest",
                sql: "[Purpose] IN ('SupplierInvoices', 'WorkerWages', 'SubcontractWork', 'SubcontractAdvance', 'EquipmentCost', 'Salaries')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PaymentRequest_SourceType",
                schema: "finance",
                table: "PaymentRequest",
                sql: "[SourceType] IN ('SupplierPayment', 'WorkerPayment', 'SubcontractPayment', 'EquipmentCostPayment', 'SalarySheetPayment')");
        }
    }
}
