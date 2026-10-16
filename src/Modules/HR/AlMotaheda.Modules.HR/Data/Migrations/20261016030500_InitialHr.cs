using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlMotaheda.Modules.HR.Data.Migrations
{
    /// <inheritdoc />
    internal partial class InitialHr : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "hr");

            migrationBuilder.CreateTable(
                name: "Department",
                schema: "hr",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    NameAr = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    NameEn = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    UpdatedBy = table.Column<int>(type: "int", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Department", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SalarySheet",
                schema: "hr",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Month = table.Column<DateOnly>(type: "date", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Total = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    EmployeeCount = table.Column<int>(type: "int", nullable: false),
                    CalculatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CalculatedBy = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    SubmittedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    SubmittedBy = table.Column<int>(type: "int", nullable: true),
                    ReviewedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    ReviewedBy = table.Column<int>(type: "int", nullable: true),
                    ReviewNote = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    PaidOn = table.Column<DateOnly>(type: "date", nullable: true),
                    PaymentRequestId = table.Column<int>(type: "int", nullable: true),
                    PaidAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    PaidBy = table.Column<int>(type: "int", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalarySheet", x => x.Id);
                    table.CheckConstraint("CK_SalarySheet_Month", "DAY([Month]) = 1");
                    table.CheckConstraint("CK_SalarySheet_Paid", "[Status] <> 'Paid' OR ([PaidOn] IS NOT NULL AND [PaymentRequestId] IS NOT NULL)");
                    table.CheckConstraint("CK_SalarySheet_Status", "[Status] IN ('Draft', 'Submitted', 'Returned', 'Approved', 'Paid')");
                    table.CheckConstraint("CK_SalarySheet_Total", "[Total] >= 0");
                });

            migrationBuilder.CreateTable(
                name: "Employee",
                schema: "hr",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EmployeeNo = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false, computedColumnSql: "CONCAT('EMP-', REPLICATE('0', 6 - LEN([Id])), [Id])", stored: true),
                    NameAr = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    NameEn = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    NationalId = table.Column<string>(type: "char(14)", unicode: false, fixedLength: true, maxLength: 14, nullable: false),
                    Phone = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: true),
                    Email = table.Column<string>(type: "varchar(200)", unicode: false, maxLength: 200, nullable: true),
                    JobTitle = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    DepartmentId = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    HireDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: true),
                    EndReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    UserId = table.Column<int>(type: "int", nullable: true),
                    BankName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Iban = table.Column<string>(type: "varchar(34)", unicode: false, maxLength: 34, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    UpdatedBy = table.Column<int>(type: "int", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Employee", x => x.Id);
                    table.CheckConstraint("CK_Employee_Ended", "([Status] = 'Active' AND [EndDate] IS NULL) OR ([Status] = 'Ended' AND [EndDate] IS NOT NULL AND [EndDate] >= [HireDate])");
                    table.CheckConstraint("CK_Employee_Kind", "[Kind] IN ('OfficeEmployee', 'Engineer', 'Staff')");
                    table.CheckConstraint("CK_Employee_NationalId", "LEN([NationalId]) = 14 AND [NationalId] NOT LIKE '%[^0-9]%'");
                    table.CheckConstraint("CK_Employee_Status", "[Status] IN ('Active', 'Ended')");
                    table.ForeignKey(
                        name: "FK_Employee_Department_DepartmentId",
                        column: x => x.DepartmentId,
                        principalSchema: "hr",
                        principalTable: "Department",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "EmployeeDocument",
                schema: "hr",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EmployeeId = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Number = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    IssueDate = table.Column<DateOnly>(type: "date", nullable: true),
                    ExpiryDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    UpdatedBy = table.Column<int>(type: "int", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmployeeDocument", x => x.Id);
                    table.CheckConstraint("CK_EmployeeDocument_Dates", "[IssueDate] IS NULL OR [ExpiryDate] IS NULL OR [ExpiryDate] >= [IssueDate]");
                    table.CheckConstraint("CK_EmployeeDocument_Kind", "[Kind] IN ('NationalIdCard', 'Contract', 'Qualification', 'MilitaryStatus', 'SocialInsurance', 'Other')");
                    table.ForeignKey(
                        name: "FK_EmployeeDocument_Employee_EmployeeId",
                        column: x => x.EmployeeId,
                        principalSchema: "hr",
                        principalTable: "Employee",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "PayrollInput",
                schema: "hr",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EmployeeId = table.Column<int>(type: "int", nullable: false),
                    Month = table.Column<DateOnly>(type: "date", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false),
                    CancelledAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    CancelledBy = table.Column<int>(type: "int", nullable: true),
                    CancelReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PayrollInput", x => x.Id);
                    table.CheckConstraint("CK_PayrollInput_Amount", "[Amount] > 0");
                    table.CheckConstraint("CK_PayrollInput_Kind", "[Kind] IN ('Bonus', 'Deduction', 'AdvanceRecovery')");
                    table.CheckConstraint("CK_PayrollInput_Month", "DAY([Month]) = 1");
                    table.CheckConstraint("CK_PayrollInput_Status", "[Status] IN ('Active', 'Cancelled')");
                    table.ForeignKey(
                        name: "FK_PayrollInput_Employee_EmployeeId",
                        column: x => x.EmployeeId,
                        principalSchema: "hr",
                        principalTable: "Employee",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "SalarySheetLine",
                schema: "hr",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SheetId = table.Column<int>(type: "int", nullable: false),
                    EmployeeId = table.Column<int>(type: "int", nullable: false),
                    EmployeeNo = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    NameAr = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    NameEn = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    DepartmentId = table.Column<int>(type: "int", nullable: false),
                    BasicSalary = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    FixedAllowance = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Bonuses = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Deductions = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    AdvanceRecoveries = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    NetSalary = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalarySheetLine", x => x.Id);
                    table.CheckConstraint("CK_SalarySheetLine_Net", "[NetSalary] >= 0");
                    table.CheckConstraint("CK_SalarySheetLine_Sum", "[NetSalary] = [BasicSalary] + [FixedAllowance] + [Bonuses] - [Deductions] - [AdvanceRecoveries]");
                    table.ForeignKey(
                        name: "FK_SalarySheetLine_Employee_EmployeeId",
                        column: x => x.EmployeeId,
                        principalSchema: "hr",
                        principalTable: "Employee",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SalarySheetLine_SalarySheet_SheetId",
                        column: x => x.SheetId,
                        principalSchema: "hr",
                        principalTable: "SalarySheet",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "SalaryStructure",
                schema: "hr",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EmployeeId = table.Column<int>(type: "int", nullable: false),
                    EffectiveFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    BasicSalary = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    FixedAllowance = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedBy = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalaryStructure", x => x.Id);
                    table.CheckConstraint("CK_SalaryStructure_Allowance", "[FixedAllowance] >= 0");
                    table.CheckConstraint("CK_SalaryStructure_Basic", "[BasicSalary] > 0");
                    table.ForeignKey(
                        name: "FK_SalaryStructure_Employee_EmployeeId",
                        column: x => x.EmployeeId,
                        principalSchema: "hr",
                        principalTable: "Employee",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "UX_Department_NameAr",
                schema: "hr",
                table: "Department",
                column: "NameAr",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_Department_NameEn",
                schema: "hr",
                table: "Department",
                column: "NameEn",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Employee_DepartmentId",
                schema: "hr",
                table: "Employee",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_Employee_Status_Name",
                schema: "hr",
                table: "Employee",
                columns: new[] { "Status", "NameEn" });

            migrationBuilder.CreateIndex(
                name: "UX_Employee_EmployeeNo",
                schema: "hr",
                table: "Employee",
                column: "EmployeeNo",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_Employee_NationalId",
                schema: "hr",
                table: "Employee",
                column: "NationalId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_Employee_UserId",
                schema: "hr",
                table: "Employee",
                column: "UserId",
                unique: true,
                filter: "[UserId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeDocument_EmployeeId",
                schema: "hr",
                table: "EmployeeDocument",
                column: "EmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeDocument_ExpiryDate",
                schema: "hr",
                table: "EmployeeDocument",
                column: "ExpiryDate",
                filter: "[ExpiryDate] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PayrollInput_EmployeeId",
                schema: "hr",
                table: "PayrollInput",
                column: "EmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_PayrollInput_Month_Employee",
                schema: "hr",
                table: "PayrollInput",
                columns: new[] { "Month", "EmployeeId" });

            migrationBuilder.CreateIndex(
                name: "UX_SalarySheet_Month",
                schema: "hr",
                table: "SalarySheet",
                column: "Month",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SalarySheetLine_EmployeeId",
                schema: "hr",
                table: "SalarySheetLine",
                column: "EmployeeId");

            migrationBuilder.CreateIndex(
                name: "UX_SalarySheetLine_Employee",
                schema: "hr",
                table: "SalarySheetLine",
                columns: new[] { "SheetId", "EmployeeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_SalaryStructure_Employee_EffectiveFrom",
                schema: "hr",
                table: "SalaryStructure",
                columns: new[] { "EmployeeId", "EffectiveFrom" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EmployeeDocument",
                schema: "hr");

            migrationBuilder.DropTable(
                name: "PayrollInput",
                schema: "hr");

            migrationBuilder.DropTable(
                name: "SalarySheetLine",
                schema: "hr");

            migrationBuilder.DropTable(
                name: "SalaryStructure",
                schema: "hr");

            migrationBuilder.DropTable(
                name: "SalarySheet",
                schema: "hr");

            migrationBuilder.DropTable(
                name: "Employee",
                schema: "hr");

            migrationBuilder.DropTable(
                name: "Department",
                schema: "hr");
        }
    }
}
