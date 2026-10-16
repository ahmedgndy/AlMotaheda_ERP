using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.HR.Common;
using AlMotaheda.Modules.HR.Data;
using AlMotaheda.Modules.HR.Data.Configurations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.HR.Features.Payroll;

internal sealed record SalaryStructureView(
    int Id, int EmployeeId, DateOnly EffectiveFrom, decimal BasicSalary, decimal FixedAllowance, decimal MonthlyTotal, string? Note,
    DateTime CreatedAt, int CreatedBy)
{
    public static SalaryStructureView From(SalaryStructure structure) => new(
        structure.Id, structure.EmployeeId, structure.EffectiveFrom, structure.BasicSalary, structure.FixedAllowance,
        structure.BasicSalary + structure.FixedAllowance, structure.Note, structure.CreatedAt, structure.CreatedBy);
}

/// <summary>
/// <c>GET /api/hr/employees/{id}/salaries</c> (hr View + See money): every salary version, newest first.
/// <c>POST</c> on the same path (hr Edit + See money): a new version from <c>effectiveFrom</c>. Versions are never
/// changed. A version can't start in a month whose sheet is already submitted, approved or paid (409 month_locked).
/// </summary>
internal static class SalaryStructures
{
    internal sealed record Request(string? EffectiveFrom, decimal? BasicSalary, decimal? FixedAllowance, string? Note);

    public static void Map(IEndpointRouteBuilder api)
    {
        api.MapGet("/hr/employees/{employeeId:int}/salaries", ListAsync)
            .RequirePermission(HrPermissions.Module, PermissionAction.View)
            .RequirePermission(HrPermissions.Module, PermissionAction.SeeMoney)
            .WithName("ListSalaryStructures")
            .WithSummary("An employee's salary history (hr See money)");

        api.MapPost("/hr/employees/{employeeId:int}/salaries", AddAsync)
            .RequirePermission(HrPermissions.Module, PermissionAction.Edit)
            .RequirePermission(HrPermissions.Module, PermissionAction.SeeMoney)
            .WithName("AddSalaryStructure")
            .WithSummary("Set an employee's salary from a date (hr Edit + See money). 409 salary_version_exists, month_locked, employee_ended");
    }

    internal static async Task<Results<Ok<IReadOnlyList<SalaryStructureView>>, ProblemHttpResult>> ListAsync(
        int employeeId, HrDbContext db, CancellationToken cancellationToken)
    {
        if (!await db.Employees.AnyAsync(employee => employee.Id == employeeId, cancellationToken))
        {
            return HrProblems.EmployeeNotFound();
        }

        var versions = await db.SalaryStructures.AsNoTracking()
            .Where(structure => structure.EmployeeId == employeeId)
            .OrderByDescending(structure => structure.EffectiveFrom)
            .ToListAsync(cancellationToken);
        return TypedResults.Ok<IReadOnlyList<SalaryStructureView>>(versions.Select(SalaryStructureView.From).ToList());
    }

    internal static async Task<Results<Created<SalaryStructureView>, ValidationProblem, ProblemHttpResult>> AddAsync(
        int employeeId, Request request, HttpContext httpContext, HrDbContext db, CompanyClock clock, HrLog log,
        CancellationToken cancellationToken)
    {
        if (HrCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var effectiveFrom = Input.RequiredDate(request.EffectiveFrom, errors, "effectiveFrom", "The start date");
        var basic = NumberInput.RequiredMoney(request.BasicSalary, errors, "basicSalary", "The basic salary");
        var allowance = request.FixedAllowance is 0 ? 0 : NumberInput.OptionalMoney(request.FixedAllowance, errors, "fixedAllowance", "The fixed allowance") ?? 0;
        var note = Input.OptionalText(request.Note, SalaryStructureConfiguration.NoteMaxLength, errors, "note", "The note");
        if (errors.HasErrors)
        {
            return HrProblems.Validation(errors);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await AppLock.TryAcquireAsync(db, AppLock.Payroll, cancellationToken))
        {
            return HrProblems.TryAgainLater();
        }

        var employee = await db.Employees.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == employeeId, cancellationToken);
        if (employee is null)
        {
            return HrProblems.EmployeeNotFound();
        }

        if (employee.Status == EmployeeStatus.Ended)
        {
            return HrProblems.Ended();
        }

        if (await PayrollMonths.LockedFromAsync(db, effectiveFrom!.Value, cancellationToken) is { } locked)
        {
            return HrProblems.Locked(PayrollMonths.Format(locked));
        }

        var structure = new SalaryStructure
        {
            EmployeeId = employeeId,
            EffectiveFrom = effectiveFrom.Value,
            BasicSalary = basic!.Value,
            FixedAllowance = allowance,
            Note = note,
            CreatedAt = clock.UtcNow,
            CreatedBy = caller.UserId,
        };
        db.SalaryStructures.Add(structure);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, SalaryStructureConfiguration.EffectiveIndex))
        {
            return HrProblems.SalaryVersionTaken();
        }

        await transaction.CommitAsync(cancellationToken);
        log.SalarySet(caller.UserId, employeeId, structure.Id);
        return TypedResults.Created($"/api/hr/employees/{employeeId}/salaries", SalaryStructureView.From(structure));
    }
}

/// <summary>Which months can still change. A month is locked once its sheet is submitted, approved or paid.</summary>
internal static class PayrollMonths
{
    public static DateOnly FirstDay(DateOnly date) => new(date.Year, date.Month, 1);

    public static string Format(DateOnly month) => month.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The month of <paramref name="date"/> if its sheet is locked, else <c>null</c>.</summary>
    public static async Task<DateOnly?> LockedAsync(HrDbContext db, DateOnly date, CancellationToken cancellationToken)
    {
        var month = FirstDay(date);
        var locked = await db.SalarySheets.AnyAsync(
            sheet => sheet.Month == month && sheet.Status != SalarySheetStatus.Draft && sheet.Status != SalarySheetStatus.Returned,
            cancellationToken);
        return locked ? month : null;
    }

    /// <summary>The latest locked month on or after the month of <paramref name="date"/>, or <c>null</c>: a salary starting then would change it.</summary>
    public static async Task<DateOnly?> LockedFromAsync(HrDbContext db, DateOnly date, CancellationToken cancellationToken)
    {
        var month = FirstDay(date);
        return await db.SalarySheets
            .Where(sheet => sheet.Month >= month && sheet.Status != SalarySheetStatus.Draft && sheet.Status != SalarySheetStatus.Returned)
            .OrderByDescending(sheet => sheet.Month)
            .Select(sheet => (DateOnly?)sheet.Month)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
