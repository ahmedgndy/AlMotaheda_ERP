using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlMotaheda.Modules.Finance.Data.Migrations
{
    /// <inheritdoc />
    internal partial class SalaryPayments : Migration
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
                sql: "[PayeeType] IN ('Supplier', 'Worker', 'Subcontractor')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PaymentRequest_Purpose",
                schema: "finance",
                table: "PaymentRequest",
                sql: "[Purpose] IN ('SupplierInvoices', 'WorkerWages', 'SubcontractWork', 'SubcontractAdvance', 'EquipmentCost')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PaymentRequest_SourceType",
                schema: "finance",
                table: "PaymentRequest",
                sql: "[SourceType] IN ('SupplierPayment', 'WorkerPayment', 'SubcontractPayment', 'EquipmentCostPayment')");
        }
    }
}
