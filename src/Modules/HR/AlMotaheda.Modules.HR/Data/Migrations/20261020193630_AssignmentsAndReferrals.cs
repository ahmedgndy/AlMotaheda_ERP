using System;
using AlMotaheda.Modules.HR.Data.Configurations;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlMotaheda.Modules.HR.Data.Migrations
{
    /// <summary>
    /// Salaried employees on projects over time (<c>EmployeeAssignment</c>: a driver on a site, a supervisor, an
    /// administrator; for employees without a sign-in account) and the cases other modules refer to HR
    /// (<c>HrReferral</c>: a rejected vehicle claim, a violation), closed by HR with an outcome; a deduction is only
    /// ever a <c>PayrollInput</c> raised through the ordinary payroll flow. Only adds; no row is changed.
    /// </summary>
    /// <remarks>Down drops both tables; it fails while a referral points to a payroll input (the input stays).</remarks>
    internal partial class AssignmentsAndReferrals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EmployeeAssignment",
                schema: "hr",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EmployeeId = table.Column<int>(type: "int", nullable: false),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    Role = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    AssignedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    AssignedBy = table.Column<int>(type: "int", nullable: false),
                    EndedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    EndedBy = table.Column<int>(type: "int", nullable: true),
                    EndReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmployeeAssignment", x => x.Id);
                    table.CheckConstraint("CK_EmployeeAssignment_Dates", "[EndDate] IS NULL OR [EndDate] >= [StartDate]");
                    table.CheckConstraint("CK_EmployeeAssignment_End", "([EndDate] IS NULL AND [EndedAt] IS NULL AND [EndedBy] IS NULL) OR ([EndDate] IS NOT NULL AND [EndedAt] IS NOT NULL AND [EndedBy] IS NOT NULL)");
                    table.CheckConstraint("CK_EmployeeAssignment_Role", "[Role] IN ('SiteEngineer', 'Driver', 'Supervisor', 'Administrator', 'Guard', 'Other')");
                    table.ForeignKey(
                        name: "FK_EmployeeAssignment_Employee_EmployeeId",
                        column: x => x.EmployeeId,
                        principalSchema: "hr",
                        principalTable: "Employee",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "HrReferral",
                schema: "hr",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Number = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false, computedColumnSql: "CONCAT('HRR-', REPLICATE('0', 6 - LEN([Id])), [Id])", stored: true),
                    SourceType = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    SourceId = table.Column<int>(type: "int", nullable: false),
                    EmployeeId = table.Column<int>(type: "int", nullable: false),
                    VehicleId = table.Column<int>(type: "int", nullable: true),
                    Subject = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Details = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    ReferenceNo = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ReferredAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    ReferredBy = table.Column<int>(type: "int", nullable: false),
                    InvestigationStartedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    InvestigatedBy = table.Column<int>(type: "int", nullable: true),
                    InvestigationNote = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Outcome = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    ClosingNote = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ClosedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ClosedBy = table.Column<int>(type: "int", nullable: true),
                    DeductionInputId = table.Column<int>(type: "int", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HrReferral", x => x.Id);
                    table.CheckConstraint("CK_HrReferral_Amount", "[Amount] IS NULL OR [Amount] > 0");
                    table.CheckConstraint("CK_HrReferral_Closed", "([Status] <> 'Closed' AND [Outcome] IS NULL AND [ClosedAt] IS NULL AND [ClosedBy] IS NULL) OR ([Status] = 'Closed' AND [Outcome] IS NOT NULL AND [ClosedAt] IS NOT NULL AND [ClosedBy] IS NOT NULL)");
                    table.CheckConstraint("CK_HrReferral_Deduction", "([Outcome] = 'DeductionApplied' AND [DeductionInputId] IS NOT NULL) OR ([Outcome] IS NULL OR [Outcome] <> 'DeductionApplied') AND [DeductionInputId] IS NULL");
                    table.CheckConstraint("CK_HrReferral_Outcome", "[Outcome] IS NULL OR [Outcome] IN ('DeductionApplied', 'Warning', 'NoAction')");
                    table.CheckConstraint("CK_HrReferral_SourceType", "[SourceType] IN ('VehicleExpense', 'VehicleViolation')");
                    table.CheckConstraint("CK_HrReferral_Status", "[Status] IN ('Open', 'UnderInvestigation', 'Closed')");
                    table.ForeignKey(
                        name: "FK_HrReferral_Employee_EmployeeId",
                        column: x => x.EmployeeId,
                        principalSchema: "hr",
                        principalTable: "Employee",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_HrReferral_PayrollInput_DeductionInputId",
                        column: x => x.DeductionInputId,
                        principalSchema: "hr",
                        principalTable: "PayrollInput",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeAssignment_Employee_Start",
                schema: "hr",
                table: "EmployeeAssignment",
                columns: new[] { "EmployeeId", "StartDate" });

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeAssignment_Project_Start",
                schema: "hr",
                table: "EmployeeAssignment",
                columns: new[] { "ProjectId", "StartDate" });

            migrationBuilder.CreateIndex(
                name: "UX_EmployeeAssignment_Open",
                schema: "hr",
                table: "EmployeeAssignment",
                columns: new[] { "EmployeeId", "ProjectId" },
                unique: true,
                filter: "[EndDate] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_HrReferral_DeductionInputId",
                schema: "hr",
                table: "HrReferral",
                column: "DeductionInputId");

            migrationBuilder.CreateIndex(
                name: "IX_HrReferral_EmployeeId",
                schema: "hr",
                table: "HrReferral",
                column: "EmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_HrReferral_Status",
                schema: "hr",
                table: "HrReferral",
                columns: new[] { "Status", "Id" });

            migrationBuilder.CreateIndex(
                name: "UX_HrReferral_Number",
                schema: "hr",
                table: "HrReferral",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_HrReferral_Source",
                schema: "hr",
                table: "HrReferral",
                columns: new[] { "SourceType", "SourceId" },
                unique: true);

            // Written by hand: the project is a Projects record.
            migrationBuilder.AddForeignKey(
                name: EmployeeAssignmentConfiguration.ProjectForeignKey,
                schema: "hr",
                table: "EmployeeAssignment",
                column: "ProjectId",
                principalSchema: "projects",
                principalTable: "Project",
                principalColumn: "Id",
                onDelete: ReferentialAction.NoAction);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(name: EmployeeAssignmentConfiguration.ProjectForeignKey, schema: "hr", table: "EmployeeAssignment");
            migrationBuilder.DropTable(
                name: "EmployeeAssignment",
                schema: "hr");

            migrationBuilder.DropTable(
                name: "HrReferral",
                schema: "hr");
        }
    }
}
