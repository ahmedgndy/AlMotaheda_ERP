using System.Globalization;
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

internal sealed record PayrollInputView(
    int Id, int EmployeeId, string EmployeeNo, string EmployeeNameAr, string EmployeeNameEn, string Month, string Kind, decimal Amount,
    string Reason, string Status, DateTime CreatedAt, int CreatedBy, DateTime? CancelledAt, int? CancelledBy, string? CancelReason,
    string RowVersion)
{
    public static PayrollInputView From(PayrollInput input, Employee employee) => new(
        input.Id, input.EmployeeId, employee.EmployeeNo, employee.NameAr, employee.NameEn, PayrollMonths.Format(input.Month),
        input.Kind.ToString(), input.Amount, input.Reason, input.Status.ToString(), input.CreatedAt, input.CreatedBy, input.CancelledAt,
        input.CancelledBy, input.CancelReason, RowVersions.Format(input.RowVersion));
}

/// <summary>
/// The bonuses, deductions and advance recoveries of a month (hr See money for all of them).
/// <c>GET /api/hr/payroll-inputs?month=2026-10</c> (View), <c>POST /api/hr/payroll-inputs</c> (Create),
/// <c>POST /api/hr/payroll-inputs/{id}/cancel</c> (Edit). Inputs are never edited: a wrong one is cancelled with a
/// reason and a new one added. Nothing changes in a month whose sheet is submitted, approved or paid (409 month_locked).
/// </summary>
internal static class PayrollInputs
{
    internal sealed record AddRequest(int? EmployeeId, string? Month, string? Kind, decimal? Amount, string? Reason);

    internal sealed record CancelRequest(string? Reason, string? RowVersion);

    public static void Map(IEndpointRouteBuilder api)
    {
        api.MapGet("/hr/payroll-inputs", ListAsync)
            .RequirePermission(HrPermissions.Module, PermissionAction.View)
            .RequirePermission(HrPermissions.Module, PermissionAction.SeeMoney)
            .WithName("ListPayrollInputs")
            .WithSummary("A month's bonuses, deductions and advance recoveries (month=yyyy-MM, optional employeeId)");

        api.MapPost("/hr/payroll-inputs", AddAsync)
            .RequirePermission(HrPermissions.Module, PermissionAction.Create)
            .RequirePermission(HrPermissions.Module, PermissionAction.SeeMoney)
            .WithName("AddPayrollInput")
            .WithSummary("Add a bonus, deduction or advance recovery for one month. 409 month_locked, employee_ended");

        api.MapPost("/hr/payroll-inputs/{inputId:int}/cancel", CancelAsync)
            .RequirePermission(HrPermissions.Module, PermissionAction.Edit)
            .RequirePermission(HrPermissions.Module, PermissionAction.SeeMoney)
            .WithName("CancelPayrollInput")
            .WithSummary("Cancel a payroll input, with a reason. 409 month_locked, invalid_status");
    }

    /// <summary>Reads <c>yyyy-MM</c> as the first day of that month.</summary>
    public static DateOnly? ParseMonth(string? value, ValidationErrors errors, string field = "month")
    {
        if (Input.Clean(value) is not { } text
            || !DateOnly.TryParseExact(text + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var month)
            || month.Year < 2000)
        {
            errors.Add(field, $"{field} must be a month written as yyyy-MM, for example 2026-10.");
            return null;
        }

        return month;
    }

    internal static async Task<Results<Ok<IReadOnlyList<PayrollInputView>>, ValidationProblem>> ListAsync(
        HrDbContext db, CancellationToken cancellationToken, string? month = null, int? employeeId = null)
    {
        var errors = new ValidationErrors();
        var wanted = ParseMonth(month, errors);
        if (errors.HasErrors)
        {
            return HrProblems.Validation(errors);
        }

        var query = db.PayrollInputs.AsNoTracking().Where(input => input.Month == wanted!.Value);
        if (employeeId is { } id)
        {
            query = query.Where(input => input.EmployeeId == id);
        }

        var rows = await query
            .Join(db.Employees, input => input.EmployeeId, employee => employee.Id, (input, employee) => new { input, employee })
            .OrderBy(row => row.employee.NameEn).ThenBy(row => row.input.Id)
            .ToListAsync(cancellationToken);
        return TypedResults.Ok<IReadOnlyList<PayrollInputView>>(rows.Select(row => PayrollInputView.From(row.input, row.employee)).ToList());
    }

    internal static async Task<Results<Created<PayrollInputView>, ValidationProblem, ProblemHttpResult>> AddAsync(
        AddRequest request, HttpContext httpContext, HrDbContext db, CompanyClock clock, HrLog log, CancellationToken cancellationToken)
    {
        if (HrCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        if (request.EmployeeId is not > 0)
        {
            errors.Add("employeeId", "The employee is required.");
        }

        var month = ParseMonth(request.Month, errors);
        var kind = Input.RequiredName<PayrollInputKind>(request.Kind, errors, "kind");
        var amount = NumberInput.RequiredMoney(request.Amount, errors, "amount", "The amount");
        var reason = Input.RequiredText(request.Reason, PayrollInputConfiguration.ReasonMaxLength, errors, "reason", "The reason");
        if (errors.HasErrors)
        {
            return HrProblems.Validation(errors);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await AppLock.TryAcquireAsync(db, AppLock.Payroll, cancellationToken))
        {
            return HrProblems.TryAgainLater();
        }

        var employee = await db.Employees.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == request.EmployeeId, cancellationToken);
        if (employee is null)
        {
            return HrProblems.EmployeeNotFound();
        }

        if (!Employee.WorksIn(employee.HireDate, employee.EndDate, month!.Value, SalaryCalculator.LastDay(month.Value)))
        {
            return employee.Status == EmployeeStatus.Ended
                ? HrProblems.Ended()
                : HrProblems.Validation(Error("month", "The employee doesn't work in this month."));
        }

        if (await PayrollMonths.LockedAsync(db, month.Value, cancellationToken) is { } locked)
        {
            return HrProblems.Locked(PayrollMonths.Format(locked));
        }

        var input = new PayrollInput
        {
            EmployeeId = employee.Id,
            Month = month.Value,
            Kind = kind!.Value,
            Amount = amount!.Value,
            Reason = reason!,
            CreatedAt = clock.UtcNow,
            CreatedBy = caller.UserId,
        };
        db.PayrollInputs.Add(input);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        log.InputAdded(caller.UserId, employee.Id, input.Id, input.Kind.ToString());
        return TypedResults.Created($"/api/hr/payroll-inputs/{input.Id}", PayrollInputView.From(input, employee));
    }

    internal static async Task<Results<Ok<PayrollInputView>, ValidationProblem, ProblemHttpResult>> CancelAsync(
        int inputId, CancelRequest request, HttpContext httpContext, HrDbContext db, CompanyClock clock, HrLog log,
        CancellationToken cancellationToken)
    {
        if (HrCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var reason = Input.RequiredText(request.Reason, PayrollInputConfiguration.ReasonMaxLength, errors, "reason", "The reason");
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (errors.HasErrors)
        {
            return HrProblems.Validation(errors);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await AppLock.TryAcquireAsync(db, AppLock.Payroll, cancellationToken))
        {
            return HrProblems.TryAgainLater();
        }

        var input = await db.PayrollInputs.SingleOrDefaultAsync(candidate => candidate.Id == inputId, cancellationToken);
        if (input is null)
        {
            return HrProblems.InputNotFound();
        }

        if (input.Status != PayrollInputStatus.Active)
        {
            return HrProblems.WrongStatus("This payroll input is already cancelled.");
        }

        if (await PayrollMonths.LockedAsync(db, input.Month, cancellationToken) is { } locked)
        {
            return HrProblems.Locked(PayrollMonths.Format(locked));
        }

        input.Status = PayrollInputStatus.Cancelled;
        input.CancelledAt = clock.UtcNow;
        input.CancelledBy = caller.UserId;
        input.CancelReason = reason;
        db.Entry(input).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        await transaction.CommitAsync(cancellationToken);
        log.InputCancelled(caller.UserId, input.Id);

        var employee = await db.Employees.AsNoTracking().SingleAsync(candidate => candidate.Id == input.EmployeeId, cancellationToken);
        return TypedResults.Ok(PayrollInputView.From(input, employee));
    }

    private static ValidationErrors Error(string field, string message)
    {
        var errors = new ValidationErrors();
        errors.Add(field, message);
        return errors;
    }
}
