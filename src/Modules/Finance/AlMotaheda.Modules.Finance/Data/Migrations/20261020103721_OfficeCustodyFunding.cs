using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlMotaheda.Modules.Finance.Data.Migrations
{
    /// <inheritdoc />
    internal partial class OfficeCustodyFunding : Migration
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
                sql: "[PayeeType] IN ('Supplier', 'Worker', 'Subcontractor', 'Employees', 'CustodyHolder', 'OfficeCustody')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PaymentRequest_Purpose",
                schema: "finance",
                table: "PaymentRequest",
                sql: "[Purpose] IN ('SupplierInvoices', 'WorkerWages', 'SubcontractWork', 'SubcontractAdvance', 'EquipmentCost', 'Salaries', 'CustodyAdvance', 'OfficeCustodyAdvance')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PaymentRequest_SourceType",
                schema: "finance",
                table: "PaymentRequest",
                sql: "[SourceType] IN ('SupplierPayment', 'WorkerPayment', 'SubcontractPayment', 'EquipmentCostPayment', 'SalarySheetPayment', 'CustodyFunding', 'OfficeCustodyFunding')");
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
    }
}
