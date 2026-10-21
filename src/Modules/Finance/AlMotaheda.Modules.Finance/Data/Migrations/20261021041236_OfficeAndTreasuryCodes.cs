using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlMotaheda.Modules.Finance.Data.Migrations
{
    /// <summary>
    /// After merging the Office module: the payee types, purposes and source types a payment request may carry are the
    /// union of the treasury hierarchy's (<c>CustodyAccount</c>, <c>CustodyReturn</c>) and the office custody's
    /// (<c>OfficeCustody</c>, <c>OfficeCustodyAdvance</c>, <c>OfficeCustodyFunding</c>); each branch's migration had
    /// re-created the check constraints with its own list. Additive.
    /// </summary>
    internal partial class OfficeAndTreasuryCodes : Migration
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
                sql: "[PayeeType] IN ('Supplier', 'Worker', 'Subcontractor', 'Employees', 'CustodyHolder', 'CustodyAccount', 'OfficeCustody')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PaymentRequest_Purpose",
                schema: "finance",
                table: "PaymentRequest",
                sql: "[Purpose] IN ('SupplierInvoices', 'WorkerWages', 'SubcontractWork', 'SubcontractAdvance', 'EquipmentCost', 'Salaries', 'CustodyAdvance', 'CustodyReturn', 'OfficeCustodyAdvance')");

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
                sql: "[PayeeType] IN ('Supplier', 'Worker', 'Subcontractor', 'Employees', 'CustodyHolder', 'CustodyAccount')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PaymentRequest_Purpose",
                schema: "finance",
                table: "PaymentRequest",
                sql: "[Purpose] IN ('SupplierInvoices', 'WorkerWages', 'SubcontractWork', 'SubcontractAdvance', 'EquipmentCost', 'Salaries', 'CustodyAdvance', 'CustodyReturn')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PaymentRequest_SourceType",
                schema: "finance",
                table: "PaymentRequest",
                sql: "[SourceType] IN ('SupplierPayment', 'WorkerPayment', 'SubcontractPayment', 'EquipmentCostPayment', 'SalarySheetPayment', 'CustodyFunding')");
        }
    }
}
