using System;
using AlMotaheda.Modules.Workforce.Data.Configurations;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlMotaheda.Modules.Workforce.Data.Migrations
{
    /// <summary>
    /// Weekly labor settlements (docs/project-workspace/09-project-financial-workspace.md): wages paid in cash from a
    /// custody, per worker and day, with their alerts; and the settlement a worker payment came from. Only adds: no row
    /// is changed or removed; existing payments have no settlement.
    /// </summary>
    /// <remarks>Down drops the settlements; the worker payments a confirmed settlement wrote stay, without the link.</remarks>
    internal partial class PayrollSettlements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SettlementId",
                schema: "workforce",
                table: "WorkerPayment",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PayrollSettlement",
                schema: "workforce",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    PeriodFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    PeriodTo = table.Column<DateOnly>(type: "date", nullable: false),
                    PaidOn = table.Column<DateOnly>(type: "date", nullable: false),
                    PaidByUserId = table.Column<int>(type: "int", nullable: false),
                    Method = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Status = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    RecordedBy = table.Column<int>(type: "int", nullable: false),
                    RecordedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    ReviewedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ReviewedBy = table.Column<int>(type: "int", nullable: true),
                    ReviewNote = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    ReversedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ReversedBy = table.Column<int>(type: "int", nullable: true),
                    ReverseReason = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    IdempotencyKey = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PayrollSettlement", x => x.Id);
                    table.CheckConstraint("CK_PayrollSettlement_Amount", "[Amount] > 0");
                    table.CheckConstraint("CK_PayrollSettlement_Method", "[Method] IN ('Cash', 'BankTransfer')");
                    table.CheckConstraint("CK_PayrollSettlement_Period", "[PeriodTo] >= [PeriodFrom]");
                    table.CheckConstraint("CK_PayrollSettlement_Reversed", "([ReversedAt] IS NULL AND [ReversedBy] IS NULL AND [ReverseReason] IS NULL) OR ([ReversedAt] IS NOT NULL AND [ReversedBy] IS NOT NULL AND [ReverseReason] IS NOT NULL)");
                    table.CheckConstraint("CK_PayrollSettlement_Status", "[Status] IN ('Pending', 'Confirmed', 'Rejected', 'Reversed')");
                });

            migrationBuilder.CreateTable(
                name: "PayrollSettlementLine",
                schema: "workforce",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SettlementId = table.Column<int>(type: "int", nullable: false),
                    WorkerId = table.Column<int>(type: "int", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    PaymentId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PayrollSettlementLine", x => x.Id);
                    table.CheckConstraint("CK_PayrollSettlementLine_Amount", "[Amount] > 0");
                    table.ForeignKey(
                        name: "FK_PayrollSettlementLine_PayrollSettlement_SettlementId",
                        column: x => x.SettlementId,
                        principalSchema: "workforce",
                        principalTable: "PayrollSettlement",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_PayrollSettlementLine_WorkerPayment_PaymentId",
                        column: x => x.PaymentId,
                        principalSchema: "workforce",
                        principalTable: "WorkerPayment",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_PayrollSettlementLine_Worker_WorkerId",
                        column: x => x.WorkerId,
                        principalSchema: "workforce",
                        principalTable: "Worker",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "SettlementAlert",
                schema: "workforce",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SettlementId = table.Column<int>(type: "int", nullable: false),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    RecipientUserId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SettlementAlert", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SettlementAlert_PayrollSettlement_SettlementId",
                        column: x => x.SettlementId,
                        principalSchema: "workforce",
                        principalTable: "PayrollSettlement",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "PayrollSettlementAllocation",
                schema: "workforce",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SettlementLineId = table.Column<int>(type: "int", nullable: false),
                    LineId = table.Column<int>(type: "int", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PayrollSettlementAllocation", x => x.Id);
                    table.CheckConstraint("CK_PayrollSettlementAllocation_Amount", "[Amount] > 0");
                    table.ForeignKey(
                        name: "FK_PayrollSettlementAllocation_AttendanceLine_LineId",
                        column: x => x.LineId,
                        principalSchema: "workforce",
                        principalTable: "AttendanceLine",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_PayrollSettlementAllocation_PayrollSettlementLine_SettlementLineId",
                        column: x => x.SettlementLineId,
                        principalSchema: "workforce",
                        principalTable: "PayrollSettlementLine",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkerPayment_SettlementId",
                schema: "workforce",
                table: "WorkerPayment",
                column: "SettlementId");

            migrationBuilder.CreateIndex(
                name: "IX_PayrollSettlement_PaidByUserId",
                schema: "workforce",
                table: "PayrollSettlement",
                column: "PaidByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PayrollSettlement_Project_Status_PaidOn",
                schema: "workforce",
                table: "PayrollSettlement",
                columns: new[] { "ProjectId", "Status", "PaidOn" });

            migrationBuilder.CreateIndex(
                name: "UX_PayrollSettlement_IdempotencyKey",
                schema: "workforce",
                table: "PayrollSettlement",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PayrollSettlementAllocation_LineId",
                schema: "workforce",
                table: "PayrollSettlementAllocation",
                column: "LineId");

            migrationBuilder.CreateIndex(
                name: "UX_PayrollSettlementAllocation_SettlementLine_Line",
                schema: "workforce",
                table: "PayrollSettlementAllocation",
                columns: new[] { "SettlementLineId", "LineId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PayrollSettlementLine_PaymentId",
                schema: "workforce",
                table: "PayrollSettlementLine",
                column: "PaymentId");

            migrationBuilder.CreateIndex(
                name: "IX_PayrollSettlementLine_WorkerId",
                schema: "workforce",
                table: "PayrollSettlementLine",
                column: "WorkerId");

            migrationBuilder.CreateIndex(
                name: "UX_PayrollSettlementLine_Settlement_Worker",
                schema: "workforce",
                table: "PayrollSettlementLine",
                columns: new[] { "SettlementId", "WorkerId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SettlementAlert_Project_CreatedAt",
                schema: "workforce",
                table: "SettlementAlert",
                columns: new[] { "ProjectId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "UX_SettlementAlert_Settlement_Recipient",
                schema: "workforce",
                table: "SettlementAlert",
                columns: new[] { "SettlementId", "RecipientUserId" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_WorkerPayment_PayrollSettlement_SettlementId",
                schema: "workforce",
                table: "WorkerPayment",
                column: "SettlementId",
                principalSchema: "workforce",
                principalTable: "PayrollSettlement",
                principalColumn: "Id");

            // The project and the people are other modules' records: these keys are added by hand.
            migrationBuilder.AddForeignKey(
                name: PayrollSettlementConfiguration.ProjectForeignKey,
                schema: "workforce",
                table: "PayrollSettlement",
                column: "ProjectId",
                principalSchema: "projects",
                principalTable: "Project",
                principalColumn: "Id",
                onDelete: ReferentialAction.NoAction);

            migrationBuilder.AddForeignKey(
                name: PayrollSettlementConfiguration.PaidByForeignKey,
                schema: "workforce",
                table: "PayrollSettlement",
                column: "PaidByUserId",
                principalSchema: "access",
                principalTable: "User",
                principalColumn: "Id",
                onDelete: ReferentialAction.NoAction);

            migrationBuilder.AddForeignKey(
                name: SettlementAlertConfiguration.RecipientForeignKey,
                schema: "workforce",
                table: "SettlementAlert",
                column: "RecipientUserId",
                principalSchema: "access",
                principalTable: "User",
                principalColumn: "Id",
                onDelete: ReferentialAction.NoAction);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(name: SettlementAlertConfiguration.RecipientForeignKey, schema: "workforce", table: "SettlementAlert");
            migrationBuilder.DropForeignKey(name: PayrollSettlementConfiguration.PaidByForeignKey, schema: "workforce", table: "PayrollSettlement");
            migrationBuilder.DropForeignKey(name: PayrollSettlementConfiguration.ProjectForeignKey, schema: "workforce", table: "PayrollSettlement");

            migrationBuilder.DropForeignKey(
                name: "FK_WorkerPayment_PayrollSettlement_SettlementId",
                schema: "workforce",
                table: "WorkerPayment");

            migrationBuilder.DropTable(
                name: "PayrollSettlementAllocation",
                schema: "workforce");

            migrationBuilder.DropTable(
                name: "SettlementAlert",
                schema: "workforce");

            migrationBuilder.DropTable(
                name: "PayrollSettlementLine",
                schema: "workforce");

            migrationBuilder.DropTable(
                name: "PayrollSettlement",
                schema: "workforce");

            migrationBuilder.DropIndex(
                name: "IX_WorkerPayment_SettlementId",
                schema: "workforce",
                table: "WorkerPayment");

            migrationBuilder.DropColumn(
                name: "SettlementId",
                schema: "workforce",
                table: "WorkerPayment");
        }
    }
}
