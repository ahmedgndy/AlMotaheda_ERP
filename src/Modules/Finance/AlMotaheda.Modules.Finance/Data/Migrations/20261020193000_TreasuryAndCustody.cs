using System;
using AlMotaheda.Modules.Finance.Data.Configurations;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace AlMotaheda.Modules.Finance.Data.Migrations
{
    /// <summary>
    /// The company treasury and the custody hierarchy (docs/project-workspace/12-treasury-custody.md): the two
    /// treasury accounts (Bank, Cash; seeded with no opening balance), custody accounts (accountant, engineer-project,
    /// vehicle, payroll batch) with their append-only movements, custody transfers with their history and alerts,
    /// payroll batches with allocations, cash counts, adjustments and period closes. A ledger entry may now come
    /// from a custody transfer instead of a payment request (<c>PaymentRequestId</c> nullable, <c>CustodyTransferId</c>),
    /// may be tagged <c>Custody</c> with its account, and a payment request may name the custody it is paid from.
    /// Only adds and widens: no existing row is changed.
    /// </summary>
    /// <remarks>
    /// Down drops the new tables and columns; it fails if a ledger entry has no payment request (one written by a
    /// transfer), if a request names a custody account, or if a transfer, account or batch row exists: remove them first.
    /// </remarks>
    internal partial class TreasuryAndCustody : Migration
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
                name: "CK_LedgerEntry_Account",
                schema: "finance",
                table: "LedgerEntry");

            migrationBuilder.AddColumn<int>(
                name: "SourceCustodyAccountId",
                schema: "finance",
                table: "PaymentRequest",
                type: "int",
                nullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "PaymentRequestId",
                schema: "finance",
                table: "LedgerEntry",
                type: "int",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AddColumn<int>(
                name: "CustodyAccountId",
                schema: "finance",
                table: "LedgerEntry",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CustodyTransferId",
                schema: "finance",
                table: "LedgerEntry",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CustodyAccount",
                schema: "finance",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Number = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false, computedColumnSql: "CONCAT('CA-', REPLICATE('0', 6 - LEN([Id])), [Id])", stored: true),
                    Kind = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    HolderUserId = table.Column<int>(type: "int", nullable: true),
                    ReferenceId = table.Column<int>(type: "int", nullable: true),
                    ParentAccountId = table.Column<int>(type: "int", nullable: true),
                    NameAr = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    NameEn = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    OpenedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    OpenedBy = table.Column<int>(type: "int", nullable: false),
                    ClosedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ClosedBy = table.Column<int>(type: "int", nullable: true),
                    CloseReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustodyAccount", x => x.Id);
                    table.CheckConstraint("CK_CustodyAccount_Closed", "([Status] = 'Active' AND [ClosedAt] IS NULL AND [ClosedBy] IS NULL) OR ([Status] = 'Closed' AND [ClosedAt] IS NOT NULL AND [ClosedBy] IS NOT NULL)");
                    table.CheckConstraint("CK_CustodyAccount_Kind", "[Kind] IN ('Accountant', 'PayrollBatch', 'EngineerProject', 'Vehicle', 'Office')");
                    table.CheckConstraint("CK_CustodyAccount_KindFields", "([Kind] = 'Accountant' AND [HolderUserId] IS NOT NULL AND [ReferenceId] IS NULL) OR ([Kind] IN ('EngineerProject', 'PayrollBatch', 'Office') AND [HolderUserId] IS NOT NULL AND [ReferenceId] IS NOT NULL) OR ([Kind] = 'Vehicle' AND [HolderUserId] IS NULL AND [ReferenceId] IS NOT NULL)");
                    table.CheckConstraint("CK_CustodyAccount_Status", "[Status] IN ('Active', 'Closed')");
                    table.ForeignKey(
                        name: "FK_CustodyAccount_CustodyAccount_ParentAccountId",
                        column: x => x.ParentAccountId,
                        principalSchema: "finance",
                        principalTable: "CustodyAccount",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "TreasuryAccount",
                schema: "finance",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Account = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    NameAr = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    NameEn = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    OpeningBalance = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    OpeningDate = table.Column<DateOnly>(type: "date", nullable: false),
                    OpeningSetAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    OpeningSetBy = table.Column<int>(type: "int", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TreasuryAccount", x => x.Id);
                    table.CheckConstraint("CK_TreasuryAccount_Account", "[Account] IN ('Bank', 'Cash')");
                    table.CheckConstraint("CK_TreasuryAccount_Opening", "([OpeningSetAt] IS NULL AND [OpeningSetBy] IS NULL) OR ([OpeningSetAt] IS NOT NULL AND [OpeningSetBy] IS NOT NULL)");
                });

            migrationBuilder.CreateTable(
                name: "CustodyCashCount",
                schema: "finance",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AccountId = table.Column<int>(type: "int", nullable: false),
                    CountDate = table.Column<DateOnly>(type: "date", nullable: false),
                    CountedAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    BookBalance = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Difference = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    EvidenceReference = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    IdempotencyKey = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CountedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CountedBy = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustodyCashCount", x => x.Id);
                    table.CheckConstraint("CK_CustodyCashCount_Counted", "[CountedAmount] >= 0");
                    table.CheckConstraint("CK_CustodyCashCount_Difference", "[Difference] = [CountedAmount] - [BookBalance]");
                    table.ForeignKey(
                        name: "FK_CustodyCashCount_CustodyAccount_AccountId",
                        column: x => x.AccountId,
                        principalSchema: "finance",
                        principalTable: "CustodyAccount",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "CustodyPeriodClose",
                schema: "finance",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AccountId = table.Column<int>(type: "int", nullable: false),
                    PeriodEnd = table.Column<DateOnly>(type: "date", nullable: false),
                    ClosingBalance = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    PendingAtClose = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ClosedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    ClosedBy = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustodyPeriodClose", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustodyPeriodClose_CustodyAccount_AccountId",
                        column: x => x.AccountId,
                        principalSchema: "finance",
                        principalTable: "CustodyAccount",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "PayrollBatch",
                schema: "finance",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Number = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false, computedColumnSql: "CONCAT('PB-', REPLICATE('0', 6 - LEN([Id])), [Id])", stored: true),
                    Kind = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    PeriodFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    PeriodTo = table.Column<DateOnly>(type: "date", nullable: false),
                    SalarySheetId = table.Column<int>(type: "int", nullable: true),
                    AccountId = table.Column<int>(type: "int", nullable: false),
                    HolderUserId = table.Column<int>(type: "int", nullable: false),
                    NameAr = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    NameEn = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    ClosedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ClosedBy = table.Column<int>(type: "int", nullable: true),
                    CloseNote = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PayrollBatch", x => x.Id);
                    table.CheckConstraint("CK_PayrollBatch_Kind", "[Kind] IN ('DailyLabor', 'Salaries')");
                    table.CheckConstraint("CK_PayrollBatch_Period", "[PeriodTo] >= [PeriodFrom]");
                    table.CheckConstraint("CK_PayrollBatch_Sheet", "([Kind] = 'Salaries' AND [SalarySheetId] IS NOT NULL) OR ([Kind] <> 'Salaries' AND [SalarySheetId] IS NULL)");
                    table.CheckConstraint("CK_PayrollBatch_Status", "[Status] IN ('Open', 'Closed')");
                    table.ForeignKey(
                        name: "FK_PayrollBatch_CustodyAccount_AccountId",
                        column: x => x.AccountId,
                        principalSchema: "finance",
                        principalTable: "CustodyAccount",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "CustodyAdjustment",
                schema: "finance",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AccountId = table.Column<int>(type: "int", nullable: false),
                    CashCountId = table.Column<int>(type: "int", nullable: true),
                    EntryDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    EvidenceReference = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    IdempotencyKey = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustodyAdjustment", x => x.Id);
                    table.CheckConstraint("CK_CustodyAdjustment_Amount", "[Amount] <> 0");
                    table.ForeignKey(
                        name: "FK_CustodyAdjustment_CustodyAccount_AccountId",
                        column: x => x.AccountId,
                        principalSchema: "finance",
                        principalTable: "CustodyAccount",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_CustodyAdjustment_CustodyCashCount_CashCountId",
                        column: x => x.CashCountId,
                        principalSchema: "finance",
                        principalTable: "CustodyCashCount",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "CustodyTransfer",
                schema: "finance",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Number = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false, computedColumnSql: "CONCAT('CT-', REPLICATE('0', 6 - LEN([Id])), [Id])", stored: true),
                    SourceKind = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    SourceTreasuryAccount = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    SourceAccountId = table.Column<int>(type: "int", nullable: true),
                    DestinationKind = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    DestinationTreasuryAccount = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    DestinationAccountId = table.Column<int>(type: "int", nullable: true),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Purpose = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    PayrollBatchId = table.Column<int>(type: "int", nullable: true),
                    ProjectId = table.Column<int>(type: "int", nullable: true),
                    Reference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    EvidenceDocumentId = table.Column<int>(type: "int", nullable: true),
                    EvidenceReference = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    RequestedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    RequestedBy = table.Column<int>(type: "int", nullable: false),
                    DecidedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    DecidedBy = table.Column<int>(type: "int", nullable: true),
                    DecisionNote = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    DisbursedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    DisbursedBy = table.Column<int>(type: "int", nullable: true),
                    DisbursedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    DisbursementReference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ReceivedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ReceivedBy = table.Column<int>(type: "int", nullable: true),
                    ReceivedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    ClosedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ClosedBy = table.Column<int>(type: "int", nullable: true),
                    CloseReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    IdempotencyKey = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustodyTransfer", x => x.Id);
                    table.CheckConstraint("CK_CustodyTransfer_Amount", "[Amount] > 0");
                    table.CheckConstraint("CK_CustodyTransfer_Destination", "([DestinationKind] = 'Treasury' AND [DestinationTreasuryAccount] IN ('Bank', 'Cash') AND [DestinationAccountId] IS NULL) OR ([DestinationKind] = 'Custody' AND [DestinationTreasuryAccount] IS NULL AND [DestinationAccountId] IS NOT NULL)");
                    table.CheckConstraint("CK_CustodyTransfer_Disbursed", "[Status] NOT IN ('Disbursed', 'Received', 'Reversed') OR ([DisbursedAt] IS NOT NULL AND [DisbursedBy] IS NOT NULL AND [DisbursedOn] IS NOT NULL)");
                    table.CheckConstraint("CK_CustodyTransfer_Ends", "[SourceKind] = 'Treasury' OR [DestinationKind] = 'Treasury' OR [SourceAccountId] <> [DestinationAccountId]");
                    table.CheckConstraint("CK_CustodyTransfer_Purpose", "[Purpose] IN ('CustodyAdvance', 'CustodyReturn')");
                    table.CheckConstraint("CK_CustodyTransfer_Received", "[Status] <> 'Received' OR ([ReceivedAt] IS NOT NULL AND [ReceivedBy] IS NOT NULL AND [ReceivedOn] IS NOT NULL)");
                    table.CheckConstraint("CK_CustodyTransfer_Source", "([SourceKind] = 'Treasury' AND [SourceTreasuryAccount] IN ('Bank', 'Cash') AND [SourceAccountId] IS NULL) OR ([SourceKind] = 'Custody' AND [SourceTreasuryAccount] IS NULL AND [SourceAccountId] IS NOT NULL)");
                    table.CheckConstraint("CK_CustodyTransfer_Status", "[Status] IN ('Requested', 'Approved', 'Disbursed', 'Received', 'Rejected', 'Cancelled', 'Reversed')");
                    table.ForeignKey(
                        name: "FK_CustodyTransfer_CustodyAccount_DestinationAccountId",
                        column: x => x.DestinationAccountId,
                        principalSchema: "finance",
                        principalTable: "CustodyAccount",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_CustodyTransfer_CustodyAccount_SourceAccountId",
                        column: x => x.SourceAccountId,
                        principalSchema: "finance",
                        principalTable: "CustodyAccount",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_CustodyTransfer_PayrollBatch_PayrollBatchId",
                        column: x => x.PayrollBatchId,
                        principalSchema: "finance",
                        principalTable: "PayrollBatch",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "PayrollBatchAllocation",
                schema: "finance",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    BatchId = table.Column<int>(type: "int", nullable: false),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    EntitlementAtAllocation = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PayrollBatchAllocation", x => x.Id);
                    table.CheckConstraint("CK_PayrollBatchAllocation_Amount", "[Amount] > 0");
                    table.CheckConstraint("CK_PayrollBatchAllocation_Entitlement", "[EntitlementAtAllocation] >= 0");
                    table.ForeignKey(
                        name: "FK_PayrollBatchAllocation_PayrollBatch_BatchId",
                        column: x => x.BatchId,
                        principalSchema: "finance",
                        principalTable: "PayrollBatch",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "CustodyMovement",
                schema: "finance",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AccountId = table.Column<int>(type: "int", nullable: false),
                    EntryDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    SourceType = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    SourceId = table.Column<int>(type: "int", nullable: false),
                    TransferId = table.Column<int>(type: "int", nullable: true),
                    PaymentRequestId = table.Column<int>(type: "int", nullable: true),
                    ReversesMovementId = table.Column<long>(type: "bigint", nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Reference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    UpdatedBy = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustodyMovement", x => x.Id);
                    table.CheckConstraint("CK_CustodyMovement_Amount", "[Amount] <> 0");
                    table.CheckConstraint("CK_CustodyMovement_Kind", "[Kind] IN ('TransferIn', 'TransferOut', 'Payment', 'Adjustment', 'Reversal')");
                    table.CheckConstraint("CK_CustodyMovement_Pending", "[Status] = 'Posted' OR [Amount] < 0");
                    table.CheckConstraint("CK_CustodyMovement_SourceType", "[SourceType] IN ('CustodyTransfer', 'CustodyTransferReversal', 'PaymentRequest', 'PaymentRequestReversal', 'CustodyAdjustment')");
                    table.CheckConstraint("CK_CustodyMovement_Status", "[Status] IN ('Pending', 'Posted', 'Released')");
                    table.ForeignKey(
                        name: "FK_CustodyMovement_CustodyAccount_AccountId",
                        column: x => x.AccountId,
                        principalSchema: "finance",
                        principalTable: "CustodyAccount",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_CustodyMovement_CustodyMovement_ReversesMovementId",
                        column: x => x.ReversesMovementId,
                        principalSchema: "finance",
                        principalTable: "CustodyMovement",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_CustodyMovement_CustodyTransfer_TransferId",
                        column: x => x.TransferId,
                        principalSchema: "finance",
                        principalTable: "CustodyTransfer",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_CustodyMovement_PaymentRequest_PaymentRequestId",
                        column: x => x.PaymentRequestId,
                        principalSchema: "finance",
                        principalTable: "PaymentRequest",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "CustodyTransferAlert",
                schema: "finance",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TransferId = table.Column<int>(type: "int", nullable: false),
                    RecipientUserId = table.Column<int>(type: "int", nullable: false),
                    Stage = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustodyTransferAlert", x => x.Id);
                    table.CheckConstraint("CK_CustodyTransferAlert_Stage", "[Stage] IN ('Requested', 'Disbursed')");
                    table.ForeignKey(
                        name: "FK_CustodyTransferAlert_CustodyTransfer_TransferId",
                        column: x => x.TransferId,
                        principalSchema: "finance",
                        principalTable: "CustodyTransfer",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "CustodyTransferEvent",
                schema: "finance",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TransferId = table.Column<int>(type: "int", nullable: false),
                    Action = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    FromStatus = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    ToStatus = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ActorUserId = table.Column<int>(type: "int", nullable: false),
                    At = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustodyTransferEvent", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustodyTransferEvent_CustodyTransfer_TransferId",
                        column: x => x.TransferId,
                        principalSchema: "finance",
                        principalTable: "CustodyTransfer",
                        principalColumn: "Id");
                });

            migrationBuilder.InsertData(
                schema: "finance",
                table: "TreasuryAccount",
                columns: new[] { "Id", "Account", "NameAr", "NameEn", "Notes", "OpeningBalance", "OpeningDate", "OpeningSetAt", "OpeningSetBy" },
                values: new object[,]
                {
                    { 1, "Bank", "البنك", "Bank", null, 0m, new DateOnly(2026, 1, 1), null, null },
                    { 2, "Cash", "الخزينة", "Cash", null, 0m, new DateOnly(2026, 1, 1), null, null }
                });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentRequest_SourceCustody",
                schema: "finance",
                table: "PaymentRequest",
                columns: new[] { "SourceCustodyAccountId", "Status" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_PaymentRequest_CustodySource",
                schema: "finance",
                table: "PaymentRequest",
                sql: "[SourceCustodyAccountId] IS NULL OR [Method] = 'Cash'");

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

            migrationBuilder.CreateIndex(
                name: "IX_LedgerEntry_Account_Date",
                schema: "finance",
                table: "LedgerEntry",
                columns: new[] { "Account", "EntryDate" });

            migrationBuilder.CreateIndex(
                name: "IX_LedgerEntry_CustodyAccount_Date",
                schema: "finance",
                table: "LedgerEntry",
                columns: new[] { "CustodyAccountId", "EntryDate" });

            migrationBuilder.CreateIndex(
                name: "IX_LedgerEntry_CustodyTransferId",
                schema: "finance",
                table: "LedgerEntry",
                column: "CustodyTransferId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_LedgerEntry_Account",
                schema: "finance",
                table: "LedgerEntry",
                sql: "[Account] IN ('Bank', 'Cash', 'Custody')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_LedgerEntry_CustodyAccount",
                schema: "finance",
                table: "LedgerEntry",
                sql: "([Account] = 'Custody' AND [CustodyAccountId] IS NOT NULL) OR ([Account] <> 'Custody' AND [CustodyAccountId] IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_LedgerEntry_Origin",
                schema: "finance",
                table: "LedgerEntry",
                sql: "[PaymentRequestId] IS NOT NULL OR [CustodyTransferId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CustodyAccount_HolderUserId",
                schema: "finance",
                table: "CustodyAccount",
                column: "HolderUserId");

            migrationBuilder.CreateIndex(
                name: "IX_CustodyAccount_ParentAccountId",
                schema: "finance",
                table: "CustodyAccount",
                column: "ParentAccountId");

            migrationBuilder.CreateIndex(
                name: "UX_CustodyAccount_Active",
                schema: "finance",
                table: "CustodyAccount",
                columns: new[] { "Kind", "HolderUserId", "ReferenceId" },
                unique: true,
                filter: "[Status] = 'Active'");

            migrationBuilder.CreateIndex(
                name: "UX_CustodyAccount_Number",
                schema: "finance",
                table: "CustodyAccount",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustodyAdjustment_Account_Date",
                schema: "finance",
                table: "CustodyAdjustment",
                columns: new[] { "AccountId", "EntryDate" });

            migrationBuilder.CreateIndex(
                name: "IX_CustodyAdjustment_CashCountId",
                schema: "finance",
                table: "CustodyAdjustment",
                column: "CashCountId");

            migrationBuilder.CreateIndex(
                name: "UX_CustodyAdjustment_IdempotencyKey",
                schema: "finance",
                table: "CustodyAdjustment",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustodyCashCount_Account_Date",
                schema: "finance",
                table: "CustodyCashCount",
                columns: new[] { "AccountId", "CountDate" });

            migrationBuilder.CreateIndex(
                name: "UX_CustodyCashCount_IdempotencyKey",
                schema: "finance",
                table: "CustodyCashCount",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustodyMovement_Account_Date",
                schema: "finance",
                table: "CustodyMovement",
                columns: new[] { "AccountId", "EntryDate" });

            migrationBuilder.CreateIndex(
                name: "IX_CustodyMovement_PaymentRequestId",
                schema: "finance",
                table: "CustodyMovement",
                column: "PaymentRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_CustodyMovement_TransferId",
                schema: "finance",
                table: "CustodyMovement",
                column: "TransferId");

            migrationBuilder.CreateIndex(
                name: "UX_CustodyMovement_Account_Source",
                schema: "finance",
                table: "CustodyMovement",
                columns: new[] { "AccountId", "SourceType", "SourceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_CustodyMovement_Reverses",
                schema: "finance",
                table: "CustodyMovement",
                column: "ReversesMovementId",
                unique: true,
                filter: "[ReversesMovementId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_CustodyPeriodClose_Account_PeriodEnd",
                schema: "finance",
                table: "CustodyPeriodClose",
                columns: new[] { "AccountId", "PeriodEnd" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustodyTransfer_Destination",
                schema: "finance",
                table: "CustodyTransfer",
                columns: new[] { "DestinationAccountId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_CustodyTransfer_PayrollBatchId",
                schema: "finance",
                table: "CustodyTransfer",
                column: "PayrollBatchId");

            migrationBuilder.CreateIndex(
                name: "IX_CustodyTransfer_ProjectId",
                schema: "finance",
                table: "CustodyTransfer",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_CustodyTransfer_Source",
                schema: "finance",
                table: "CustodyTransfer",
                columns: new[] { "SourceAccountId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_CustodyTransfer_Status",
                schema: "finance",
                table: "CustodyTransfer",
                columns: new[] { "Status", "Id" });

            migrationBuilder.CreateIndex(
                name: "UX_CustodyTransfer_IdempotencyKey",
                schema: "finance",
                table: "CustodyTransfer",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_CustodyTransfer_Number",
                schema: "finance",
                table: "CustodyTransfer",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_CustodyTransferAlert_Transfer_Recipient_Stage",
                schema: "finance",
                table: "CustodyTransferAlert",
                columns: new[] { "TransferId", "RecipientUserId", "Stage" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustodyTransferEvent_TransferId",
                schema: "finance",
                table: "CustodyTransferEvent",
                column: "TransferId");

            migrationBuilder.CreateIndex(
                name: "IX_PayrollBatch_Status_Period",
                schema: "finance",
                table: "PayrollBatch",
                columns: new[] { "Status", "PeriodFrom" });

            migrationBuilder.CreateIndex(
                name: "UX_PayrollBatch_AccountId",
                schema: "finance",
                table: "PayrollBatch",
                column: "AccountId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_PayrollBatch_Number",
                schema: "finance",
                table: "PayrollBatch",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_PayrollBatch_SalarySheetId",
                schema: "finance",
                table: "PayrollBatch",
                column: "SalarySheetId",
                unique: true,
                filter: "[SalarySheetId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_PayrollBatchAllocation_Batch_Project",
                schema: "finance",
                table: "PayrollBatchAllocation",
                columns: new[] { "BatchId", "ProjectId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_TreasuryAccount_Account",
                schema: "finance",
                table: "TreasuryAccount",
                column: "Account",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_LedgerEntry_CustodyAccount_CustodyAccountId",
                schema: "finance",
                table: "LedgerEntry",
                column: "CustodyAccountId",
                principalSchema: "finance",
                principalTable: "CustodyAccount",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_LedgerEntry_CustodyTransfer_CustodyTransferId",
                schema: "finance",
                table: "LedgerEntry",
                column: "CustodyTransferId",
                principalSchema: "finance",
                principalTable: "CustodyTransfer",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PaymentRequest_CustodyAccount_SourceCustodyAccountId",
                schema: "finance",
                table: "PaymentRequest",
                column: "SourceCustodyAccountId",
                principalSchema: "finance",
                principalTable: "CustodyAccount",
                principalColumn: "Id");

            // Written by hand: other modules own these tables.
            migrationBuilder.AddForeignKey(
                name: CustodyAccountConfiguration.HolderForeignKey,
                schema: "finance",
                table: "CustodyAccount",
                column: "HolderUserId",
                principalSchema: "access",
                principalTable: "User",
                principalColumn: "Id",
                onDelete: ReferentialAction.NoAction);

            migrationBuilder.AddForeignKey(
                name: PayrollBatchConfiguration.HolderForeignKey,
                schema: "finance",
                table: "PayrollBatch",
                column: "HolderUserId",
                principalSchema: "access",
                principalTable: "User",
                principalColumn: "Id",
                onDelete: ReferentialAction.NoAction);

            migrationBuilder.AddForeignKey(
                name: CustodyTransferAlertConfiguration.RecipientForeignKey,
                schema: "finance",
                table: "CustodyTransferAlert",
                column: "RecipientUserId",
                principalSchema: "access",
                principalTable: "User",
                principalColumn: "Id",
                onDelete: ReferentialAction.NoAction);

            migrationBuilder.AddForeignKey(
                name: PayrollBatchAllocationConfiguration.ProjectForeignKey,
                schema: "finance",
                table: "PayrollBatchAllocation",
                column: "ProjectId",
                principalSchema: "projects",
                principalTable: "Project",
                principalColumn: "Id",
                onDelete: ReferentialAction.NoAction);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(name: CustodyAccountConfiguration.HolderForeignKey, schema: "finance", table: "CustodyAccount");
            migrationBuilder.DropForeignKey(name: PayrollBatchConfiguration.HolderForeignKey, schema: "finance", table: "PayrollBatch");
            migrationBuilder.DropForeignKey(name: CustodyTransferAlertConfiguration.RecipientForeignKey, schema: "finance", table: "CustodyTransferAlert");
            migrationBuilder.DropForeignKey(name: PayrollBatchAllocationConfiguration.ProjectForeignKey, schema: "finance", table: "PayrollBatchAllocation");
            migrationBuilder.DropForeignKey(
                name: "FK_LedgerEntry_CustodyAccount_CustodyAccountId",
                schema: "finance",
                table: "LedgerEntry");

            migrationBuilder.DropForeignKey(
                name: "FK_LedgerEntry_CustodyTransfer_CustodyTransferId",
                schema: "finance",
                table: "LedgerEntry");

            migrationBuilder.DropForeignKey(
                name: "FK_PaymentRequest_CustodyAccount_SourceCustodyAccountId",
                schema: "finance",
                table: "PaymentRequest");

            migrationBuilder.DropTable(
                name: "CustodyAdjustment",
                schema: "finance");

            migrationBuilder.DropTable(
                name: "CustodyMovement",
                schema: "finance");

            migrationBuilder.DropTable(
                name: "CustodyPeriodClose",
                schema: "finance");

            migrationBuilder.DropTable(
                name: "CustodyTransferAlert",
                schema: "finance");

            migrationBuilder.DropTable(
                name: "CustodyTransferEvent",
                schema: "finance");

            migrationBuilder.DropTable(
                name: "PayrollBatchAllocation",
                schema: "finance");

            migrationBuilder.DropTable(
                name: "TreasuryAccount",
                schema: "finance");

            migrationBuilder.DropTable(
                name: "CustodyCashCount",
                schema: "finance");

            migrationBuilder.DropTable(
                name: "CustodyTransfer",
                schema: "finance");

            migrationBuilder.DropTable(
                name: "PayrollBatch",
                schema: "finance");

            migrationBuilder.DropTable(
                name: "CustodyAccount",
                schema: "finance");

            migrationBuilder.DropIndex(
                name: "IX_PaymentRequest_SourceCustody",
                schema: "finance",
                table: "PaymentRequest");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PaymentRequest_CustodySource",
                schema: "finance",
                table: "PaymentRequest");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PaymentRequest_PayeeType",
                schema: "finance",
                table: "PaymentRequest");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PaymentRequest_Purpose",
                schema: "finance",
                table: "PaymentRequest");

            migrationBuilder.DropIndex(
                name: "IX_LedgerEntry_Account_Date",
                schema: "finance",
                table: "LedgerEntry");

            migrationBuilder.DropIndex(
                name: "IX_LedgerEntry_CustodyAccount_Date",
                schema: "finance",
                table: "LedgerEntry");

            migrationBuilder.DropIndex(
                name: "IX_LedgerEntry_CustodyTransferId",
                schema: "finance",
                table: "LedgerEntry");

            migrationBuilder.DropCheckConstraint(
                name: "CK_LedgerEntry_Account",
                schema: "finance",
                table: "LedgerEntry");

            migrationBuilder.DropCheckConstraint(
                name: "CK_LedgerEntry_CustodyAccount",
                schema: "finance",
                table: "LedgerEntry");

            migrationBuilder.DropCheckConstraint(
                name: "CK_LedgerEntry_Origin",
                schema: "finance",
                table: "LedgerEntry");

            migrationBuilder.DropColumn(
                name: "SourceCustodyAccountId",
                schema: "finance",
                table: "PaymentRequest");

            migrationBuilder.DropColumn(
                name: "CustodyAccountId",
                schema: "finance",
                table: "LedgerEntry");

            migrationBuilder.DropColumn(
                name: "CustodyTransferId",
                schema: "finance",
                table: "LedgerEntry");

            migrationBuilder.AlterColumn<int>(
                name: "PaymentRequestId",
                schema: "finance",
                table: "LedgerEntry",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);

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
                name: "CK_LedgerEntry_Account",
                schema: "finance",
                table: "LedgerEntry",
                sql: "[Account] IN ('Bank', 'Cash')");
        }
    }
}
