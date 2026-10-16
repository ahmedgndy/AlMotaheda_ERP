using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.HR.Common;
using AlMotaheda.Modules.HR.Data;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.HR.Features.Payroll;

/// <summary>A salary sheet in the list. <c>total</c> is <c>null</c> without hr See money.</summary>
internal sealed record SalarySheetSummary(
    int Id, string Month, string Status, int EmployeeCount, decimal? Total, DateTime CalculatedAt, DateTime? SubmittedAt,
    DateTime? ReviewedAt, DateOnly? PaidOn, int? PaymentRequestId, string RowVersion)
{
    public static SalarySheetSummary From(SalarySheet sheet, bool seesMoney) => new(
        sheet.Id, PayrollMonths.Format(sheet.Month), sheet.Status.ToString(), sheet.EmployeeCount, seesMoney ? sheet.Total : null,
        sheet.CalculatedAt, sheet.SubmittedAt, sheet.ReviewedAt, sheet.PaidOn, sheet.PaymentRequestId, RowVersions.Format(sheet.RowVersion));
}

/// <summary>
/// One salary sheet with its lines. Amounts are <c>null</c> without hr See money. Its payment requests are listed by
/// Finance: <c>GET /api/payment-requests?payeeType=Employees&amp;payeeId={id}</c>.
/// </summary>
internal sealed record SalarySheetView(
    int Id,
    string Month,
    string Status,
    int EmployeeCount,
    decimal? Total,
    DateTime CalculatedAt,
    HrPersonRef? CalculatedBy,
    HrPersonRef? SubmittedBy,
    DateTime? SubmittedAt,
    HrPersonRef? ReviewedBy,
    DateTime? ReviewedAt,
    string? ReviewNote,
    DateOnly? PaidOn,
    int? PaymentRequestId,
    IReadOnlyList<SalarySheetLineView> Lines,
    string RowVersion)
{
    public static async Task<SalarySheetView?> LoadAsync(
        HrDbContext db, IUserDirectory users, int sheetId, bool seesMoney, CancellationToken cancellationToken)
    {
        var sheet = await db.SalarySheets.AsNoTracking()
            .Include(candidate => candidate.Lines)
            .SingleOrDefaultAsync(candidate => candidate.Id == sheetId, cancellationToken);
        return sheet is null ? null : await FromAsync(sheet, users, seesMoney, cancellationToken);
    }

    public static async Task<SalarySheetView> FromAsync(SalarySheet sheet, IUserDirectory users, bool seesMoney, CancellationToken cancellationToken)
    {
        var people = await users.FindAsync(
            new[] { sheet.CalculatedBy, sheet.SubmittedBy ?? 0, sheet.ReviewedBy ?? 0 }.Where(id => id > 0).Distinct().ToList(),
            cancellationToken);

        return new SalarySheetView(
            sheet.Id,
            PayrollMonths.Format(sheet.Month),
            sheet.Status.ToString(),
            sheet.EmployeeCount,
            seesMoney ? sheet.Total : null,
            sheet.CalculatedAt,
            HrPersonRef.From(sheet.CalculatedBy, people),
            HrPersonRef.From(sheet.SubmittedBy, people),
            sheet.SubmittedAt,
            HrPersonRef.From(sheet.ReviewedBy, people),
            sheet.ReviewedAt,
            sheet.ReviewNote,
            sheet.PaidOn,
            sheet.PaymentRequestId,
            sheet.Lines.OrderBy(line => line.NameEn).ThenBy(line => line.EmployeeId)
                .Select(line => SalarySheetLineView.From(line, seesMoney)).ToList(),
            RowVersions.Format(sheet.RowVersion));
    }
}

internal sealed record SalarySheetLineView(
    int EmployeeId, string EmployeeNo, string NameAr, string NameEn, int DepartmentId,
    decimal? BasicSalary, decimal? FixedAllowance, decimal? Bonuses, decimal? Deductions, decimal? AdvanceRecoveries, decimal? NetSalary)
{
    public static SalarySheetLineView From(SalarySheetLine line, bool seesMoney) => seesMoney
        ? new(line.EmployeeId, line.EmployeeNo, line.NameAr, line.NameEn, line.DepartmentId,
            line.BasicSalary, line.FixedAllowance, line.Bonuses, line.Deductions, line.AdvanceRecoveries, line.NetSalary)
        : new(line.EmployeeId, line.EmployeeNo, line.NameAr, line.NameEn, line.DepartmentId, null, null, null, null, null, null);
}

/// <summary>Reads what the calculation of a month needs: employees on the payroll, salary versions, active inputs.</summary>
internal static class SalarySheetCalculation
{
    public static async Task<SalaryCalculator.Result> RunAsync(HrDbContext db, DateOnly month, CancellationToken cancellationToken)
    {
        var last = SalaryCalculator.LastDay(month);

        var employees = await db.Employees.AsNoTracking()
            .Where(employee => employee.HireDate <= last && (employee.EndDate == null || employee.EndDate >= month))
            .Select(employee => new SalaryCalculator.EmployeeFacts(
                employee.Id, employee.EmployeeNo, employee.NameAr, employee.NameEn, employee.DepartmentId, employee.HireDate, employee.EndDate))
            .ToListAsync(cancellationToken);
        var ids = employees.Select(employee => employee.Id).ToList();

        var versions = await db.SalaryStructures.AsNoTracking()
            .Where(structure => ids.Contains(structure.EmployeeId) && structure.EffectiveFrom <= last)
            .Select(structure => new SalaryCalculator.SalaryVersion(
                structure.EmployeeId, structure.EffectiveFrom, structure.BasicSalary, structure.FixedAllowance))
            .ToListAsync(cancellationToken);

        var inputs = await db.PayrollInputs.AsNoTracking()
            .Where(input => input.Month == month && input.Status == PayrollInputStatus.Active)
            .Select(input => new SalaryCalculator.InputAmount(input.EmployeeId, input.Kind, input.Amount))
            .ToListAsync(cancellationToken);

        return SalaryCalculator.Calculate(month, employees, versions, inputs);
    }
}
