using System.Globalization;
using System.Security.Claims;
using AlMotaheda.Modules.HR.Data;
using AlMotaheda.Modules.Reports.Contracts;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.HR.Integration;

/// <summary>Employee salaries by month: the salary sheet lines, as calculated and frozen on each sheet.</summary>
internal sealed class HrReports(HrDbContext db) : IReportSource
{
    public const string Salaries = "HR-SALARIES";

    public IReadOnlyList<ReportDefinition> Reports { get; } =
    [
        new(Salaries, "رواتب الموظفين الشهرية", "Employee salaries by month", ReportCategories.HumanResources, [new("hr", ReportActions.SeeMoney)],
            [
                new("fromMonth", ReportFilterTypes.Month, true, "من شهر", "From month"),
                new("toMonth", ReportFilterTypes.Month, true, "إلى شهر", "To month"),
                new("departmentId", ReportFilterTypes.Id, false, "القسم", "Department"),
                new("status", ReportFilterTypes.Option, false, "حالة الكشف", "Sheet status", Enum.GetNames<SalarySheetStatus>()),
            ],
            [
                new("month", "الشهر", "Month", ReportColumnTypes.Text),
                new("sheetStatus", "حالة الكشف", "Sheet status", ReportColumnTypes.Text),
                new("employeeNo", "رقم الموظف", "Employee no.", ReportColumnTypes.Text),
                new("employee", "الموظف", "Employee", ReportColumnTypes.Text),
                new("department", "القسم", "Department", ReportColumnTypes.Text),
                new("basic", "الأساسي", "Basic", ReportColumnTypes.Money, Total: true),
                new("allowance", "البدل الثابت", "Allowance", ReportColumnTypes.Money, Total: true),
                new("bonuses", "المكافآت", "Bonuses", ReportColumnTypes.Money, Total: true),
                new("deductions", "الخصومات", "Deductions", ReportColumnTypes.Money, Total: true),
                new("advances", "استرداد السلف", "Advance recoveries", ReportColumnTypes.Money, Total: true),
                new("net", "الصافي", "Net", ReportColumnTypes.Money, Total: true),
            ]),
    ];

    public async Task<ReportData?> RunAsync(string code, ReportQuery query, ClaimsPrincipal caller, CancellationToken cancellationToken)
    {
        if (code != Salaries)
        {
            throw new ArgumentOutOfRangeException(nameof(code), code, "Not an HR report.");
        }

        var from = query.Date("fromMonth")!.Value;
        var to = query.Date("toMonth")!.Value;
        var lines = db.SalarySheetLines.AsNoTracking()
            .Join(db.SalarySheets.Where(sheet => sheet.Month >= from && sheet.Month <= to), line => line.SheetId, sheet => sheet.Id, (line, sheet) => new { line, sheet.Month, sheet.Status, SheetId = sheet.Id });
        if (query.Id("departmentId") is { } departmentId)
        {
            lines = lines.Where(row => row.line.DepartmentId == departmentId);
        }

        if (query.Text("status") is { } status)
        {
            var wanted = Enum.Parse<SalarySheetStatus>(status);
            lines = lines.Where(row => row.Status == wanted);
        }

        var rows = await lines.OrderBy(row => row.Month).ThenBy(row => row.line.NameEn).ToListAsync(cancellationToken);
        var departments = await db.Departments.AsNoTracking().ToDictionaryAsync(department => department.Id, department => department.NameEn, cancellationToken);

        return new ReportData(rows.Select(row => new ReportRow(new Dictionary<string, object?>
        {
            ["month"] = row.Month.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            ["sheetStatus"] = row.Status.ToString(),
            ["employeeNo"] = row.line.EmployeeNo,
            ["employee"] = row.line.NameEn,
            ["department"] = departments.GetValueOrDefault(row.line.DepartmentId),
            ["basic"] = row.line.BasicSalary,
            ["allowance"] = row.line.FixedAllowance,
            ["bonuses"] = row.line.Bonuses,
            ["deductions"] = row.line.Deductions,
            ["advances"] = row.line.AdvanceRecoveries,
            ["net"] = row.line.NetSalary,
        }, new ReportLink("salarySheet", row.SheetId))).ToList());
    }
}
