using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlMotaheda.Modules.Finance.Data.Migrations
{
    /// <inheritdoc />
    internal partial class TreasuryDeposits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_LedgerEntry_Origin",
                schema: "finance",
                table: "LedgerEntry");

            migrationBuilder.AddColumn<int>(
                name: "TreasuryDepositId",
                schema: "finance",
                table: "LedgerEntry",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TreasuryDeposit",
                schema: "finance",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Number = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false, computedColumnSql: "CONCAT('TD-', REPLICATE('0', 6 - LEN([Id])), [Id])", stored: true),
                    Account = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    DepositDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IdempotencyKey = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecordedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    RecordedBy = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TreasuryDeposit", x => x.Id);
                    table.CheckConstraint("CK_TreasuryDeposit_Account", "[Account] IN ('Bank', 'Cash')");
                    table.CheckConstraint("CK_TreasuryDeposit_Amount", "[Amount] > 0");
                });

            migrationBuilder.CreateIndex(
                name: "IX_LedgerEntry_TreasuryDepositId",
                schema: "finance",
                table: "LedgerEntry",
                column: "TreasuryDepositId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_LedgerEntry_Origin",
                schema: "finance",
                table: "LedgerEntry",
                sql: "[PaymentRequestId] IS NOT NULL OR [CustodyTransferId] IS NOT NULL OR [TreasuryDepositId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TreasuryDeposit_Account_Date",
                schema: "finance",
                table: "TreasuryDeposit",
                columns: new[] { "Account", "DepositDate" });

            migrationBuilder.CreateIndex(
                name: "UX_TreasuryDeposit_IdempotencyKey",
                schema: "finance",
                table: "TreasuryDeposit",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_LedgerEntry_TreasuryDeposit_TreasuryDepositId",
                schema: "finance",
                table: "LedgerEntry",
                column: "TreasuryDepositId",
                principalSchema: "finance",
                principalTable: "TreasuryDeposit",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_LedgerEntry_TreasuryDeposit_TreasuryDepositId",
                schema: "finance",
                table: "LedgerEntry");

            migrationBuilder.DropTable(
                name: "TreasuryDeposit",
                schema: "finance");

            migrationBuilder.DropIndex(
                name: "IX_LedgerEntry_TreasuryDepositId",
                schema: "finance",
                table: "LedgerEntry");

            migrationBuilder.DropCheckConstraint(
                name: "CK_LedgerEntry_Origin",
                schema: "finance",
                table: "LedgerEntry");

            migrationBuilder.DropColumn(
                name: "TreasuryDepositId",
                schema: "finance",
                table: "LedgerEntry");

            migrationBuilder.AddCheckConstraint(
                name: "CK_LedgerEntry_Origin",
                schema: "finance",
                table: "LedgerEntry",
                sql: "[PaymentRequestId] IS NOT NULL OR [CustodyTransferId] IS NOT NULL");
        }
    }
}
