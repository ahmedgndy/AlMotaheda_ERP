using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlMotaheda.Modules.Office.Data.Migrations
{
    /// <summary>
    /// The office ledger accepts the entries Finance's custody hierarchy writes (a custody transfer received or
    /// returned, its reversal, a reviewer's adjustment), so the office custody is one account of the company's
    /// treasury hierarchy without a second ledger. Additive: the source-type check constraint grows.
    /// </summary>
    internal partial class FinanceCustodyLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_OfficeLedgerEntry_Source",
                schema: "office",
                table: "OfficeLedgerEntry");

            migrationBuilder.AddCheckConstraint(
                name: "CK_OfficeLedgerEntry_Source",
                schema: "office",
                table: "OfficeLedgerEntry",
                sql: "[SourceType] IN ('OfficeFundingRequest', 'OfficeExpense', 'OfficeExpenseReversal', 'OfficeCashReturn', 'OfficeCashCount', 'CustodyTransfer', 'CustodyTransferReversal', 'CustodyAdjustment')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_OfficeLedgerEntry_Source",
                schema: "office",
                table: "OfficeLedgerEntry");

            migrationBuilder.AddCheckConstraint(
                name: "CK_OfficeLedgerEntry_Source",
                schema: "office",
                table: "OfficeLedgerEntry",
                sql: "[SourceType] IN ('OfficeFundingRequest', 'OfficeExpense', 'OfficeExpenseReversal', 'OfficeCashReturn', 'OfficeCashCount')");
        }
    }
}
