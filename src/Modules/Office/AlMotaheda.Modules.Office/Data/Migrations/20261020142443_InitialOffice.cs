using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlMotaheda.Modules.Office.Data.Migrations
{
    /// <inheritdoc />
    internal partial class InitialOffice : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "office");

            migrationBuilder.CreateTable(
                name: "OfficeAlert",
                schema: "office",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CustodyId = table.Column<int>(type: "int", nullable: false),
                    SourceType = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    SourceId = table.Column<int>(type: "int", nullable: false),
                    RecipientUserId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    Kind = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OfficeAlert", x => x.Id);
                    table.CheckConstraint("CK_OfficeAlert_Kind", "[Kind] IN ('ExpenseSubmitted', 'ExpensePosted', 'FundingRequested', 'FundsDisbursed', 'LowBalance', 'Discrepancy', 'SpendRequested')");
                });

            migrationBuilder.CreateTable(
                name: "OfficeAttachment",
                schema: "office",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Purpose = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    OwnerType = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: true),
                    OwnerId = table.Column<int>(type: "int", nullable: true),
                    StorageKey = table.Column<string>(type: "varchar(200)", unicode: false, maxLength: 200, nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ContentType = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    CaptureSource = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    CapturedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    DeviceModel = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    QualityNote = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IdempotencyKey = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UploadedBy = table.Column<int>(type: "int", nullable: false),
                    UploadedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    BoundAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OfficeAttachment", x => x.Id);
                    table.CheckConstraint("CK_OfficeAttachment_Owner", "([OwnerType] IS NULL AND [OwnerId] IS NULL) OR ([OwnerType] IN ('OfficeExpense', 'OfficeFundingRequest', 'OfficeSpendRequest', 'OfficeCashCount') AND [OwnerId] IS NOT NULL)");
                    table.CheckConstraint("CK_OfficeAttachment_Purpose", "[Purpose] IN ('Receipt', 'BeforeRepair', 'AfterRepair', 'Quotation', 'Transfer', 'CashCount', 'Supporting')");
                });

            migrationBuilder.CreateTable(
                name: "OfficeCustody",
                schema: "office",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    NameAr = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    NameEn = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    HolderUserId = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    LowBalanceThreshold = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    LowBalanceWarnedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    StatusChangedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    StatusChangedBy = table.Column<int>(type: "int", nullable: true),
                    StatusReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OfficeCustody", x => x.Id);
                    table.CheckConstraint("CK_OfficeCustody_Status", "[Status] IN ('Active', 'Suspended', 'Closed')");
                    table.CheckConstraint("CK_OfficeCustody_Threshold", "[LowBalanceThreshold] >= 0");
                });

            migrationBuilder.CreateTable(
                name: "OfficeCashCount",
                schema: "office",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CustodyId = table.Column<int>(type: "int", nullable: false),
                    CountedOn = table.Column<DateOnly>(type: "date", nullable: false),
                    CountedBy = table.Column<int>(type: "int", nullable: false),
                    CountedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    RecordedBalance = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    CountedAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Difference = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Explanation = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    IdempotencyKey = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ReviewedBy = table.Column<int>(type: "int", nullable: true),
                    ReviewedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ReviewNote = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    AdjustmentEntryId = table.Column<long>(type: "bigint", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OfficeCashCount", x => x.Id);
                    table.CheckConstraint("CK_OfficeCashCount_Counted", "[CountedAmount] >= 0");
                    table.CheckConstraint("CK_OfficeCashCount_Difference", "[Difference] = [CountedAmount] - [RecordedBalance]");
                    table.CheckConstraint("CK_OfficeCashCount_Status", "[Status] IN ('Matched', 'Submitted', 'Accepted', 'Adjusted')");
                    table.ForeignKey(
                        name: "FK_OfficeCashCount_OfficeCustody_CustodyId",
                        column: x => x.CustodyId,
                        principalSchema: "office",
                        principalTable: "OfficeCustody",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "OfficeCashReturn",
                schema: "office",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CustodyId = table.Column<int>(type: "int", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    ReturnedOn = table.Column<DateOnly>(type: "date", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    IdempotencyKey = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OfficeCashReturn", x => x.Id);
                    table.CheckConstraint("CK_OfficeCashReturn_Amount", "[Amount] > 0");
                    table.ForeignKey(
                        name: "FK_OfficeCashReturn_OfficeCustody_CustodyId",
                        column: x => x.CustodyId,
                        principalSchema: "office",
                        principalTable: "OfficeCustody",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "OfficeFundingRequest",
                schema: "office",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CustodyId = table.Column<int>(type: "int", nullable: false),
                    RequestedAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    RequestedDate = table.Column<DateOnly>(type: "date", nullable: false),
                    IdempotencyKey = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestedBy = table.Column<int>(type: "int", nullable: false),
                    RequestedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ApprovedAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    ReviewedBy = table.Column<int>(type: "int", nullable: true),
                    ReviewedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ReviewNote = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    PaymentRequestId = table.Column<int>(type: "int", nullable: true),
                    PaymentRequestNumber = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: true),
                    PaymentRequestKey = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PaymentMethod = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: true),
                    DisbursedAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    DisbursedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    DisbursementReference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    DisbursedBy = table.Column<int>(type: "int", nullable: true),
                    ReceivedAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    ReceivedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ReceivedBy = table.Column<int>(type: "int", nullable: true),
                    ReceiptNote = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CancelledAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    CancelledBy = table.Column<int>(type: "int", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OfficeFundingRequest", x => x.Id);
                    table.CheckConstraint("CK_OfficeFundingRequest_Approved", "[ApprovedAmount] IS NULL OR ([ApprovedAmount] > 0 AND [ApprovedAmount] <= [RequestedAmount])");
                    table.CheckConstraint("CK_OfficeFundingRequest_Received", "[ReceivedAmount] IS NULL OR ([ReceivedAmount] > 0 AND [ReceivedAmount] <= [DisbursedAmount])");
                    table.CheckConstraint("CK_OfficeFundingRequest_Requested", "[RequestedAmount] > 0");
                    table.CheckConstraint("CK_OfficeFundingRequest_Status", "[Status] IN ('Submitted', 'Approved', 'Rejected', 'Disbursed', 'Received', 'Cancelled')");
                    table.ForeignKey(
                        name: "FK_OfficeFundingRequest_OfficeCustody_CustodyId",
                        column: x => x.CustodyId,
                        principalSchema: "office",
                        principalTable: "OfficeCustody",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "OfficeLedgerEntry",
                schema: "office",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CustodyId = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    EntryDate = table.Column<DateOnly>(type: "date", nullable: false),
                    SourceType = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    SourceId = table.Column<int>(type: "int", nullable: false),
                    ReversesEntryId = table.Column<long>(type: "bigint", nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Reference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    PostedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    PostedBy = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OfficeLedgerEntry", x => x.Id);
                    table.CheckConstraint("CK_OfficeLedgerEntry_Amount", "[Amount] <> 0");
                    table.CheckConstraint("CK_OfficeLedgerEntry_Kind", "[Kind] IN ('Funding', 'Expense', 'Return', 'Reversal', 'Adjustment')");
                    table.CheckConstraint("CK_OfficeLedgerEntry_Sign", "([Kind] IN ('Funding', 'Reversal') AND [Amount] > 0) OR ([Kind] IN ('Expense', 'Return') AND [Amount] < 0) OR [Kind] = 'Adjustment'");
                    table.CheckConstraint("CK_OfficeLedgerEntry_Source", "[SourceType] IN ('OfficeFundingRequest', 'OfficeExpense', 'OfficeExpenseReversal', 'OfficeCashReturn', 'OfficeCashCount')");
                    table.ForeignKey(
                        name: "FK_OfficeLedgerEntry_OfficeCustody_CustodyId",
                        column: x => x.CustodyId,
                        principalSchema: "office",
                        principalTable: "OfficeCustody",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_OfficeLedgerEntry_OfficeLedgerEntry_ReversesEntryId",
                        column: x => x.ReversesEntryId,
                        principalSchema: "office",
                        principalTable: "OfficeLedgerEntry",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "OfficeSpendRequest",
                schema: "office",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CustodyId = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    Location = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    Asset = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    Supplier = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    EstimatedAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    IsEmergency = table.Column<bool>(type: "bit", nullable: false),
                    EmergencyReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    IdempotencyKey = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestedBy = table.Column<int>(type: "int", nullable: false),
                    RequestedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ReviewedBy = table.Column<int>(type: "int", nullable: true),
                    ReviewedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ReviewNote = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    AfterEvidenceWaivedReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    AfterEvidenceWaivedBy = table.Column<int>(type: "int", nullable: true),
                    AfterEvidenceWaivedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ExpenseId = table.Column<int>(type: "int", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OfficeSpendRequest", x => x.Id);
                    table.CheckConstraint("CK_OfficeSpendRequest_Emergency", "[IsEmergency] = 0 OR [EmergencyReason] IS NOT NULL");
                    table.CheckConstraint("CK_OfficeSpendRequest_Estimate", "[EstimatedAmount] IS NULL OR [EstimatedAmount] > 0");
                    table.CheckConstraint("CK_OfficeSpendRequest_Kind", "[Kind] IN ('Repair', 'Purchase')");
                    table.CheckConstraint("CK_OfficeSpendRequest_Status", "[Status] IN ('Submitted', 'Approved', 'Rejected', 'Completed', 'Cancelled')");
                    table.ForeignKey(
                        name: "FK_OfficeSpendRequest_OfficeCustody_CustodyId",
                        column: x => x.CustodyId,
                        principalSchema: "office",
                        principalTable: "OfficeCustody",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "OfficeExpense",
                schema: "office",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CustodyId = table.Column<int>(type: "int", nullable: false),
                    Category = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    PriceItemId = table.Column<int>(type: "int", nullable: true),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Purpose = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Vendor = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    InvoiceNo = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    Quantity = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    Unit = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    UnitPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Discount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Tax = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    ExpenseDate = table.Column<DateOnly>(type: "date", nullable: false),
                    IdempotencyKey = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SpendRequestId = table.Column<int>(type: "int", nullable: true),
                    PriceCheck = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: true),
                    PriceRangeId = table.Column<int>(type: "int", nullable: true),
                    ReferencePrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    ReferenceMinPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    ReferenceMaxPrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    VariancePerUnit = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    VarianceTotal = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    VariancePercent = table.Column<decimal>(type: "decimal(9,2)", precision: 9, scale: 2, nullable: true),
                    PriceJustification = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    PriceExceptionId = table.Column<int>(type: "int", nullable: true),
                    RequiredApprovals = table.Column<int>(type: "int", nullable: false),
                    ReviewReasons = table.Column<string>(type: "varchar(300)", unicode: false, maxLength: 300, nullable: true),
                    DuplicateOf = table.Column<string>(type: "varchar(200)", unicode: false, maxLength: 200, nullable: true),
                    Round = table.Column<int>(type: "int", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    SubmittedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    DecidedBy = table.Column<int>(type: "int", nullable: true),
                    DecidedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    DecisionNote = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    PostingStatus = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    LedgerEntryId = table.Column<long>(type: "bigint", nullable: true),
                    PostedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ReversalEntryId = table.Column<long>(type: "bigint", nullable: true),
                    ReversedBy = table.Column<int>(type: "int", nullable: true),
                    ReversedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ReversalReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OfficeExpense", x => x.Id);
                    table.CheckConstraint("CK_OfficeExpense_Approvals", "[RequiredApprovals] IN (1, 2) AND [Round] >= 1");
                    table.CheckConstraint("CK_OfficeExpense_Category", "[Category] IN ('Internet', 'Stationery', 'Cleaning', 'Refreshments', 'Utilities', 'Equipment', 'Maintenance', 'Transport', 'Other')");
                    table.CheckConstraint("CK_OfficeExpense_Numbers", "[Quantity] > 0 AND [UnitPrice] >= 0 AND [Discount] >= 0 AND [Tax] >= 0 AND [Amount] > 0");
                    table.CheckConstraint("CK_OfficeExpense_PostedApproved", "[PostingStatus] = 'NotPosted' OR [Status] = 'Approved'");
                    table.CheckConstraint("CK_OfficeExpense_Posting", "[PostingStatus] IN ('NotPosted', 'Posted', 'Reversed')");
                    table.CheckConstraint("CK_OfficeExpense_Status", "[Status] IN ('Submitted', 'PartlyApproved', 'Approved', 'Returned', 'Rejected')");
                    table.ForeignKey(
                        name: "FK_OfficeExpense_OfficeCustody_CustodyId",
                        column: x => x.CustodyId,
                        principalSchema: "office",
                        principalTable: "OfficeCustody",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_OfficeExpense_OfficeLedgerEntry_LedgerEntryId",
                        column: x => x.LedgerEntryId,
                        principalSchema: "office",
                        principalTable: "OfficeLedgerEntry",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_OfficeExpense_OfficeLedgerEntry_ReversalEntryId",
                        column: x => x.ReversalEntryId,
                        principalSchema: "office",
                        principalTable: "OfficeLedgerEntry",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_OfficeExpense_OfficeSpendRequest_SpendRequestId",
                        column: x => x.SpendRequestId,
                        principalSchema: "office",
                        principalTable: "OfficeSpendRequest",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "OfficeExpenseApproval",
                schema: "office",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ExpenseId = table.Column<int>(type: "int", nullable: false),
                    Round = table.Column<int>(type: "int", nullable: false),
                    ApproverUserId = table.Column<int>(type: "int", nullable: false),
                    ApproverRole = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    Note = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ApprovedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OfficeExpenseApproval", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OfficeExpenseApproval_OfficeExpense_ExpenseId",
                        column: x => x.ExpenseId,
                        principalSchema: "office",
                        principalTable: "OfficeExpense",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_OfficeAlert_Recipient",
                schema: "office",
                table: "OfficeAlert",
                columns: new[] { "RecipientUserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_OfficeAlert_Source",
                schema: "office",
                table: "OfficeAlert",
                columns: new[] { "SourceType", "SourceId", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_OfficeAttachment_Owner",
                schema: "office",
                table: "OfficeAttachment",
                columns: new[] { "OwnerType", "OwnerId" });

            migrationBuilder.CreateIndex(
                name: "IX_OfficeAttachment_Sha256",
                schema: "office",
                table: "OfficeAttachment",
                column: "Sha256");

            migrationBuilder.CreateIndex(
                name: "UX_OfficeAttachment_IdempotencyKey",
                schema: "office",
                table: "OfficeAttachment",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OfficeCashCount_Custody_Date",
                schema: "office",
                table: "OfficeCashCount",
                columns: new[] { "CustodyId", "CountedOn" });

            migrationBuilder.CreateIndex(
                name: "UX_OfficeCashCount_IdempotencyKey",
                schema: "office",
                table: "OfficeCashCount",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OfficeCashReturn_Custody",
                schema: "office",
                table: "OfficeCashReturn",
                column: "CustodyId");

            migrationBuilder.CreateIndex(
                name: "UX_OfficeCashReturn_IdempotencyKey",
                schema: "office",
                table: "OfficeCashReturn",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OfficeCustody_Holder_Status",
                schema: "office",
                table: "OfficeCustody",
                columns: new[] { "HolderUserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "UX_OfficeCustody_NameAr",
                schema: "office",
                table: "OfficeCustody",
                column: "NameAr",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_OfficeCustody_NameEn",
                schema: "office",
                table: "OfficeCustody",
                column: "NameEn",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OfficeExpense_Custody_Status_Date",
                schema: "office",
                table: "OfficeExpense",
                columns: new[] { "CustodyId", "Status", "ExpenseDate" });

            migrationBuilder.CreateIndex(
                name: "IX_OfficeExpense_Date_Amount",
                schema: "office",
                table: "OfficeExpense",
                columns: new[] { "ExpenseDate", "Amount" });

            migrationBuilder.CreateIndex(
                name: "IX_OfficeExpense_LedgerEntryId",
                schema: "office",
                table: "OfficeExpense",
                column: "LedgerEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_OfficeExpense_PriceException",
                schema: "office",
                table: "OfficeExpense",
                column: "PriceExceptionId");

            migrationBuilder.CreateIndex(
                name: "IX_OfficeExpense_ReversalEntryId",
                schema: "office",
                table: "OfficeExpense",
                column: "ReversalEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_OfficeExpense_Vendor_Invoice",
                schema: "office",
                table: "OfficeExpense",
                columns: new[] { "Vendor", "InvoiceNo" });

            migrationBuilder.CreateIndex(
                name: "UX_OfficeExpense_IdempotencyKey",
                schema: "office",
                table: "OfficeExpense",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_OfficeExpense_SpendRequest",
                schema: "office",
                table: "OfficeExpense",
                column: "SpendRequestId",
                unique: true,
                filter: "[SpendRequestId] IS NOT NULL AND [Status] <> 'Rejected'");

            migrationBuilder.CreateIndex(
                name: "UX_OfficeExpenseApproval_Once",
                schema: "office",
                table: "OfficeExpenseApproval",
                columns: new[] { "ExpenseId", "Round", "ApproverUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OfficeFundingRequest_Custody_Status",
                schema: "office",
                table: "OfficeFundingRequest",
                columns: new[] { "CustodyId", "Status" });

            migrationBuilder.CreateIndex(
                name: "UX_OfficeFundingRequest_IdempotencyKey",
                schema: "office",
                table: "OfficeFundingRequest",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OfficeLedgerEntry_Custody_Date",
                schema: "office",
                table: "OfficeLedgerEntry",
                columns: new[] { "CustodyId", "EntryDate", "Id" });

            migrationBuilder.CreateIndex(
                name: "UX_OfficeLedgerEntry_Reverses",
                schema: "office",
                table: "OfficeLedgerEntry",
                column: "ReversesEntryId",
                unique: true,
                filter: "[ReversesEntryId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_OfficeLedgerEntry_Source",
                schema: "office",
                table: "OfficeLedgerEntry",
                columns: new[] { "SourceType", "SourceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OfficeSpendRequest_Custody_Status",
                schema: "office",
                table: "OfficeSpendRequest",
                columns: new[] { "CustodyId", "Status" });

            migrationBuilder.CreateIndex(
                name: "UX_OfficeSpendRequest_IdempotencyKey",
                schema: "office",
                table: "OfficeSpendRequest",
                column: "IdempotencyKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OfficeAlert",
                schema: "office");

            migrationBuilder.DropTable(
                name: "OfficeAttachment",
                schema: "office");

            migrationBuilder.DropTable(
                name: "OfficeCashCount",
                schema: "office");

            migrationBuilder.DropTable(
                name: "OfficeCashReturn",
                schema: "office");

            migrationBuilder.DropTable(
                name: "OfficeExpenseApproval",
                schema: "office");

            migrationBuilder.DropTable(
                name: "OfficeFundingRequest",
                schema: "office");

            migrationBuilder.DropTable(
                name: "OfficeExpense",
                schema: "office");

            migrationBuilder.DropTable(
                name: "OfficeLedgerEntry",
                schema: "office");

            migrationBuilder.DropTable(
                name: "OfficeSpendRequest",
                schema: "office");

            migrationBuilder.DropTable(
                name: "OfficeCustody",
                schema: "office");
        }
    }
}
