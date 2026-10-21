using AlMotaheda.Modules.HR.Data.Configurations;
﻿using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlMotaheda.Modules.HR.Data.Migrations
{
    /// <summary>
    /// Section 12.D: an employee's requests to HR (<c>HrRequest</c>, its thread <c>HrRequestMessage</c>, its evidence
    /// <c>HrRequestAttachment</c>, and <c>HrRequestAlert</c> rows that tell HR). Additive. The alert's recipient is an
    /// <c>access.User</c>, written by hand; apply the Access migrations first.
    /// </summary>
    internal partial class HrRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HrRequest",
                schema: "hr",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Number = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false, computedColumnSql: "CONCAT('HRQ-', REPLICATE('0', 6 - LEN([Id])), [Id])", stored: true),
                    EmployeeId = table.Column<int>(type: "int", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Details = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    FromDate = table.Column<DateOnly>(type: "date", nullable: true),
                    ToDate = table.Column<DateOnly>(type: "date", nullable: true),
                    ReferralId = table.Column<int>(type: "int", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    IdempotencyKey = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SubmittedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    SubmittedBy = table.Column<int>(type: "int", nullable: false),
                    ReviewStartedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ReviewedBy = table.Column<int>(type: "int", nullable: true),
                    DecidedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    DecidedBy = table.Column<int>(type: "int", nullable: true),
                    DecisionNote = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HrRequest", x => x.Id);
                    table.CheckConstraint("CK_HrRequest_CaseResponse", "([Type] = 'CaseResponse' AND [ReferralId] IS NOT NULL) OR ([Type] <> 'CaseResponse' AND [ReferralId] IS NULL)");
                    table.CheckConstraint("CK_HrRequest_Dates", "[FromDate] IS NULL OR [ToDate] IS NULL OR [ToDate] >= [FromDate]");
                    table.CheckConstraint("CK_HrRequest_Decided", "([Status] IN ('Submitted', 'UnderReview', 'NeedsInfo') AND [DecidedAt] IS NULL AND [DecidedBy] IS NULL AND [DecisionNote] IS NULL) OR ([Status] IN ('Approved', 'Rejected', 'Closed') AND [DecidedAt] IS NOT NULL AND [DecidedBy] IS NOT NULL AND [DecisionNote] IS NOT NULL)");
                    table.CheckConstraint("CK_HrRequest_Status", "[Status] IN ('Submitted', 'UnderReview', 'NeedsInfo', 'Approved', 'Rejected', 'Closed')");
                    table.CheckConstraint("CK_HrRequest_Type", "[Type] IN ('AttendanceCorrection', 'Leave', 'Permission', 'OvertimeClarification', 'PayrollQuestion', 'CaseResponse', 'Other')");
                    table.ForeignKey(
                        name: "FK_HrRequest_Employee_EmployeeId",
                        column: x => x.EmployeeId,
                        principalSchema: "hr",
                        principalTable: "Employee",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_HrRequest_HrReferral_ReferralId",
                        column: x => x.ReferralId,
                        principalSchema: "hr",
                        principalTable: "HrReferral",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "HrRequestAlert",
                schema: "hr",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RequestId = table.Column<int>(type: "int", nullable: false),
                    RecipientUserId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HrRequestAlert", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HrRequestAlert_HrRequest_RequestId",
                        column: x => x.RequestId,
                        principalSchema: "hr",
                        principalTable: "HrRequest",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "HrRequestAttachment",
                schema: "hr",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RequestId = table.Column<int>(type: "int", nullable: true),
                    MessageId = table.Column<int>(type: "int", nullable: true),
                    FileName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ContentType = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    StorageKey = table.Column<string>(type: "varchar(60)", unicode: false, maxLength: 60, nullable: false),
                    IdempotencyKey = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UploadedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    UploadedBy = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HrRequestAttachment", x => x.Id);
                    table.CheckConstraint("CK_HrRequestAttachment_Sha256", "LEN([Sha256]) = 64 AND [Sha256] COLLATE Latin1_General_BIN NOT LIKE '%[^0-9a-f]%'");
                    table.CheckConstraint("CK_HrRequestAttachment_Size", "[SizeBytes] > 0");
                    table.ForeignKey(
                        name: "FK_HrRequestAttachment_HrRequest_RequestId",
                        column: x => x.RequestId,
                        principalSchema: "hr",
                        principalTable: "HrRequest",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "HrRequestMessage",
                schema: "hr",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RequestId = table.Column<int>(type: "int", nullable: false),
                    AuthorUserId = table.Column<int>(type: "int", nullable: false),
                    FromHr = table.Column<bool>(type: "bit", nullable: false),
                    Text = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HrRequestMessage", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HrRequestMessage_HrRequest_RequestId",
                        column: x => x.RequestId,
                        principalSchema: "hr",
                        principalTable: "HrRequest",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_HrRequest_Employee_Status",
                schema: "hr",
                table: "HrRequest",
                columns: new[] { "EmployeeId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_HrRequest_ReferralId",
                schema: "hr",
                table: "HrRequest",
                column: "ReferralId");

            migrationBuilder.CreateIndex(
                name: "IX_HrRequest_Status_SubmittedAt",
                schema: "hr",
                table: "HrRequest",
                columns: new[] { "Status", "SubmittedAt" });

            migrationBuilder.CreateIndex(
                name: "UX_HrRequest_IdempotencyKey",
                schema: "hr",
                table: "HrRequest",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_HrRequest_Number",
                schema: "hr",
                table: "HrRequest",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_HrRequestAlert_Request_Recipient",
                schema: "hr",
                table: "HrRequestAlert",
                columns: new[] { "RequestId", "RecipientUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HrRequestAttachment_Request",
                schema: "hr",
                table: "HrRequestAttachment",
                column: "RequestId");

            migrationBuilder.CreateIndex(
                name: "UX_HrRequestAttachment_IdempotencyKey",
                schema: "hr",
                table: "HrRequestAttachment",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_HrRequestAttachment_StorageKey",
                schema: "hr",
                table: "HrRequestAttachment",
                column: "StorageKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HrRequestMessage_Request",
                schema: "hr",
                table: "HrRequestMessage",
                columns: new[] { "RequestId", "Id" });
        

            // Written by hand: the recipient is an Access user.
            migrationBuilder.AddForeignKey(
                name: HrRequestAlertConfiguration.RecipientForeignKey,
                schema: "hr",
                table: "HrRequestAlert",
                column: "RecipientUserId",
                principalSchema: "access",
                principalTable: "User",
                principalColumn: "Id",
                onDelete: ReferentialAction.NoAction);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(name: HrRequestAlertConfiguration.RecipientForeignKey, schema: "hr", table: "HrRequestAlert");
            migrationBuilder.DropTable(
                name: "HrRequestAlert",
                schema: "hr");

            migrationBuilder.DropTable(
                name: "HrRequestAttachment",
                schema: "hr");

            migrationBuilder.DropTable(
                name: "HrRequestMessage",
                schema: "hr");

            migrationBuilder.DropTable(
                name: "HrRequest",
                schema: "hr");
        }
    }
}
