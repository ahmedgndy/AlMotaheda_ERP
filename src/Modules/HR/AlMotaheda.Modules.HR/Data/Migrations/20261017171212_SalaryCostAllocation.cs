using System;
using AlMotaheda.Modules.HR.Data.Configurations;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlMotaheda.Modules.HR.Data.Migrations
{
    /// <inheritdoc />
    internal partial class SalaryCostAllocation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CostAllocationPolicy",
                schema: "hr",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    WorkingDaysPerMonth = table.Column<int>(type: "int", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    UpdatedBy = table.Column<int>(type: "int", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CostAllocationPolicy", x => x.Id);
                    table.CheckConstraint("CK_CostAllocationPolicy_Days", "[WorkingDaysPerMonth] IS NULL OR ([WorkingDaysPerMonth] BETWEEN 1 AND 31)");
                    table.CheckConstraint("CK_CostAllocationPolicy_Singleton", "[Id] = 1");
                });

            migrationBuilder.CreateTable(
                name: "ProjectWorkDays",
                schema: "hr",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EmployeeId = table.Column<int>(type: "int", nullable: false),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    ProjectItemId = table.Column<int>(type: "int", nullable: true),
                    Month = table.Column<DateOnly>(type: "date", nullable: false),
                    Days = table.Column<decimal>(type: "decimal(4,1)", precision: 4, scale: 1, nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Status = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    ReviewedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ReviewedBy = table.Column<int>(type: "int", nullable: true),
                    ReviewNote = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    UpdatedBy = table.Column<int>(type: "int", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectWorkDays", x => x.Id);
                    table.CheckConstraint("CK_ProjectWorkDays_Days", "[Days] > 0 AND [Days] <= 31 AND [Days] * 2 = ROUND([Days] * 2, 0)");
                    table.CheckConstraint("CK_ProjectWorkDays_Month", "DAY([Month]) = 1");
                    table.CheckConstraint("CK_ProjectWorkDays_Status", "[Status] IN ('Recorded', 'Approved', 'Returned', 'Rejected', 'Superseded')");
                    table.ForeignKey(
                        name: "FK_ProjectWorkDays_Employee_EmployeeId",
                        column: x => x.EmployeeId,
                        principalSchema: "hr",
                        principalTable: "Employee",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "SalaryCostAllocation",
                schema: "hr",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    WorkDaysId = table.Column<int>(type: "int", nullable: false),
                    EmployeeId = table.Column<int>(type: "int", nullable: false),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    ProjectItemId = table.Column<int>(type: "int", nullable: true),
                    Month = table.Column<DateOnly>(type: "date", nullable: false),
                    Days = table.Column<decimal>(type: "decimal(4,1)", precision: 4, scale: 1, nullable: false),
                    MonthlyPay = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    WorkingDays = table.Column<int>(type: "int", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Status = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    ReversedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ReversedBy = table.Column<int>(type: "int", nullable: true),
                    ReverseReason = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalaryCostAllocation", x => x.Id);
                    table.CheckConstraint("CK_SalaryCostAllocation_Amounts", "[Amount] >= 0 AND [MonthlyPay] >= 0 AND [Days] > 0 AND [WorkingDays] BETWEEN 1 AND 31");
                    table.CheckConstraint("CK_SalaryCostAllocation_Reversal", "([Status] = 'Active' AND [ReversedAt] IS NULL AND [ReversedBy] IS NULL AND [ReverseReason] IS NULL) OR ([Status] = 'Reversed' AND [ReversedAt] IS NOT NULL AND [ReversedBy] IS NOT NULL AND [ReverseReason] IS NOT NULL)");
                    table.CheckConstraint("CK_SalaryCostAllocation_Status", "[Status] IN ('Active', 'Reversed')");
                    table.ForeignKey(
                        name: "FK_SalaryCostAllocation_Employee_EmployeeId",
                        column: x => x.EmployeeId,
                        principalSchema: "hr",
                        principalTable: "Employee",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SalaryCostAllocation_ProjectWorkDays_WorkDaysId",
                        column: x => x.WorkDaysId,
                        principalSchema: "hr",
                        principalTable: "ProjectWorkDays",
                        principalColumn: "Id");
                });

            migrationBuilder.InsertData(
                schema: "hr",
                table: "CostAllocationPolicy",
                columns: new[] { "Id", "UpdatedAt", "UpdatedBy", "WorkingDaysPerMonth" },
                values: new object[] { 1, null, null, null });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectWorkDays_Month_Status",
                schema: "hr",
                table: "ProjectWorkDays",
                columns: new[] { "Month", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectWorkDays_ProjectId",
                schema: "hr",
                table: "ProjectWorkDays",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "UX_ProjectWorkDays_Approved",
                schema: "hr",
                table: "ProjectWorkDays",
                columns: new[] { "EmployeeId", "ProjectId", "Month" },
                unique: true,
                filter: "[Status] = 'Approved'");

            migrationBuilder.CreateIndex(
                name: "UX_ProjectWorkDays_Pending",
                schema: "hr",
                table: "ProjectWorkDays",
                columns: new[] { "EmployeeId", "ProjectId", "Month" },
                unique: true,
                filter: "[Status] IN ('Recorded', 'Returned')");

            migrationBuilder.CreateIndex(
                name: "IX_SalaryCostAllocation_EmployeeId",
                schema: "hr",
                table: "SalaryCostAllocation",
                column: "EmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_SalaryCostAllocation_Project_Month",
                schema: "hr",
                table: "SalaryCostAllocation",
                columns: new[] { "ProjectId", "Month" });

            migrationBuilder.CreateIndex(
                name: "UX_SalaryCostAllocation_Active",
                schema: "hr",
                table: "SalaryCostAllocation",
                column: "WorkDaysId",
                unique: true,
                filter: "[Status] = 'Active'");

            // Cross-schema keys to projects, written by hand like every key to another module's schema.
            migrationBuilder.AddForeignKey(
                name: ProjectWorkDaysConfiguration.ProjectForeignKey,
                schema: "hr",
                table: "ProjectWorkDays",
                column: "ProjectId",
                principalSchema: "projects",
                principalTable: "Project",
                principalColumn: "Id",
                onDelete: ReferentialAction.NoAction);

            migrationBuilder.AddForeignKey(
                name: ProjectWorkDaysConfiguration.ProjectItemForeignKey,
                schema: "hr",
                table: "ProjectWorkDays",
                column: "ProjectItemId",
                principalSchema: "projects",
                principalTable: "ProjectItem",
                principalColumn: "Id",
                onDelete: ReferentialAction.NoAction);

            migrationBuilder.AddForeignKey(
                name: SalaryCostAllocationConfiguration.ProjectForeignKey,
                schema: "hr",
                table: "SalaryCostAllocation",
                column: "ProjectId",
                principalSchema: "projects",
                principalTable: "Project",
                principalColumn: "Id",
                onDelete: ReferentialAction.NoAction);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(name: SalaryCostAllocationConfiguration.ProjectForeignKey, schema: "hr", table: "SalaryCostAllocation");
            migrationBuilder.DropForeignKey(name: ProjectWorkDaysConfiguration.ProjectItemForeignKey, schema: "hr", table: "ProjectWorkDays");
            migrationBuilder.DropForeignKey(name: ProjectWorkDaysConfiguration.ProjectForeignKey, schema: "hr", table: "ProjectWorkDays");

            migrationBuilder.DropTable(
                name: "CostAllocationPolicy",
                schema: "hr");

            migrationBuilder.DropTable(
                name: "SalaryCostAllocation",
                schema: "hr");

            migrationBuilder.DropTable(
                name: "ProjectWorkDays",
                schema: "hr");
        }
    }
}
