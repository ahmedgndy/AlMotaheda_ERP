using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlMotaheda.Modules.Finance.Data.Migrations
{
    /// <inheritdoc />
    internal partial class InitialFinance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "finance");

            migrationBuilder.CreateTable(
                name: "PaymentRequest",
                schema: "finance",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Number = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false, computedColumnSql: "CONCAT('PAY-', REPLICATE('0', 6 - LEN([Id])), [Id])", stored: true),
                    SourceType = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    PayeeType = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    PayeeId = table.Column<int>(type: "int", nullable: false),
                    ProjectId = table.Column<int>(type: "int", nullable: true),
                    Purpose = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Method = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    RequestedDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Payload = table.Column<string>(type: "varchar(1000)", unicode: false, maxLength: 1000, nullable: true),
                    IdempotencyKey = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Origin = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    DecidedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    DecidedBy = table.Column<int>(type: "int", nullable: true),
                    DecisionNote = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    SentToBankAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    BankReference = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: true),
                    FailureReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    PaidOn = table.Column<DateOnly>(type: "date", nullable: true),
                    ConfirmedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ConfirmedBy = table.Column<int>(type: "int", nullable: true),
                    PaymentReference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    SourcePaymentId = table.Column<int>(type: "int", nullable: true),
                    ClosedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ClosedBy = table.Column<int>(type: "int", nullable: true),
                    CloseReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentRequest", x => x.Id);
                    table.CheckConstraint("CK_PaymentRequest_Amount", "[Amount] > 0");
                    table.CheckConstraint("CK_PaymentRequest_Method", "[Method] IN ('BankTransfer', 'Cheque', 'Cash')");
                    table.CheckConstraint("CK_PaymentRequest_Origin", "[Origin] IN ('Request', 'Imported')");
                    table.CheckConstraint("CK_PaymentRequest_Paid", "[Status] NOT IN ('Paid', 'Reversed') OR ([PaidOn] IS NOT NULL AND [SourcePaymentId] IS NOT NULL)");
                    table.CheckConstraint("CK_PaymentRequest_PayeeType", "[PayeeType] IN ('Supplier', 'Worker', 'Subcontractor')");
                    table.CheckConstraint("CK_PaymentRequest_Purpose", "[Purpose] IN ('SupplierInvoices', 'WorkerWages', 'SubcontractWork', 'SubcontractAdvance', 'EquipmentCost')");
                    table.CheckConstraint("CK_PaymentRequest_SourceType", "[SourceType] IN ('SupplierPayment', 'WorkerPayment', 'SubcontractPayment', 'EquipmentCostPayment')");
                    table.CheckConstraint("CK_PaymentRequest_Status", "[Status] IN ('Submitted', 'Approved', 'SentToBank', 'Paid', 'Rejected', 'Failed', 'Cancelled', 'Reversed')");
                });

            migrationBuilder.CreateTable(
                name: "LedgerEntry",
                schema: "finance",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EntryDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Account = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    CounterpartyType = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    CounterpartyId = table.Column<int>(type: "int", nullable: false),
                    ProjectId = table.Column<int>(type: "int", nullable: true),
                    Purpose = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    PaymentRequestId = table.Column<int>(type: "int", nullable: false),
                    SourceType = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    SourcePaymentId = table.Column<int>(type: "int", nullable: true),
                    ReversesEntryId = table.Column<long>(type: "bigint", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LedgerEntry", x => x.Id);
                    table.CheckConstraint("CK_LedgerEntry_Account", "[Account] IN ('Bank', 'Cash')");
                    table.CheckConstraint("CK_LedgerEntry_Amount", "[Amount] <> 0");
                    table.ForeignKey(
                        name: "FK_LedgerEntry_LedgerEntry_ReversesEntryId",
                        column: x => x.ReversesEntryId,
                        principalSchema: "finance",
                        principalTable: "LedgerEntry",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LedgerEntry_PaymentRequest_PaymentRequestId",
                        column: x => x.PaymentRequestId,
                        principalSchema: "finance",
                        principalTable: "PaymentRequest",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PaymentRequestEvent",
                schema: "finance",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RequestId = table.Column<int>(type: "int", nullable: false),
                    Action = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    FromStatus = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    ToStatus = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ActorUserId = table.Column<int>(type: "int", nullable: false),
                    At = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentRequestEvent", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PaymentRequestEvent_PaymentRequest_RequestId",
                        column: x => x.RequestId,
                        principalSchema: "finance",
                        principalTable: "PaymentRequest",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PaymentRequestLine",
                schema: "finance",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RequestId = table.Column<int>(type: "int", nullable: false),
                    RecordType = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    RecordId = table.Column<int>(type: "int", nullable: false),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentRequestLine", x => x.Id);
                    table.CheckConstraint("CK_PaymentRequestLine_Amount", "[Amount] > 0");
                    table.ForeignKey(
                        name: "FK_PaymentRequestLine_PaymentRequest_RequestId",
                        column: x => x.RequestId,
                        principalSchema: "finance",
                        principalTable: "PaymentRequest",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LedgerEntry_Counterparty",
                schema: "finance",
                table: "LedgerEntry",
                columns: new[] { "CounterpartyType", "CounterpartyId", "EntryDate" });

            migrationBuilder.CreateIndex(
                name: "IX_LedgerEntry_Date",
                schema: "finance",
                table: "LedgerEntry",
                columns: new[] { "EntryDate", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_LedgerEntry_PaymentRequestId",
                schema: "finance",
                table: "LedgerEntry",
                column: "PaymentRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_LedgerEntry_Project_Date",
                schema: "finance",
                table: "LedgerEntry",
                columns: new[] { "ProjectId", "EntryDate" });

            migrationBuilder.CreateIndex(
                name: "UX_LedgerEntry_Reverses",
                schema: "finance",
                table: "LedgerEntry",
                column: "ReversesEntryId",
                unique: true,
                filter: "[ReversesEntryId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentRequest_Payee",
                schema: "finance",
                table: "PaymentRequest",
                columns: new[] { "PayeeType", "PayeeId" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentRequest_Project",
                schema: "finance",
                table: "PaymentRequest",
                columns: new[] { "ProjectId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentRequest_Status",
                schema: "finance",
                table: "PaymentRequest",
                columns: new[] { "Status", "Id" });

            migrationBuilder.CreateIndex(
                name: "UX_PaymentRequest_IdempotencyKey",
                schema: "finance",
                table: "PaymentRequest",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_PaymentRequest_Number",
                schema: "finance",
                table: "PaymentRequest",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_PaymentRequest_SourcePayment",
                schema: "finance",
                table: "PaymentRequest",
                columns: new[] { "SourceType", "SourcePaymentId" },
                unique: true,
                filter: "[SourcePaymentId] IS NOT NULL AND [Status] = 'Paid'");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentRequestEvent_RequestId",
                schema: "finance",
                table: "PaymentRequestEvent",
                column: "RequestId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentRequestLine_Record",
                schema: "finance",
                table: "PaymentRequestLine",
                columns: new[] { "RecordType", "RecordId" })
                .Annotation("SqlServer:Include", new[] { "RequestId", "Amount" });

            migrationBuilder.CreateIndex(
                name: "UX_PaymentRequestLine_Record",
                schema: "finance",
                table: "PaymentRequestLine",
                columns: new[] { "RequestId", "RecordType", "RecordId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LedgerEntry",
                schema: "finance");

            migrationBuilder.DropTable(
                name: "PaymentRequestEvent",
                schema: "finance");

            migrationBuilder.DropTable(
                name: "PaymentRequestLine",
                schema: "finance");

            migrationBuilder.DropTable(
                name: "PaymentRequest",
                schema: "finance");
        }
    }
}
