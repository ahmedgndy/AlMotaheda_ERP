using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.HR.Common;
using AlMotaheda.Modules.HR.Data;
using AlMotaheda.Modules.HR.Data.Configurations;
using AlMotaheda.Modules.HR.Features.Payroll;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.HR.Features.Referrals;

/// <summary>A referral as HR sees it. The amount is <c>null</c> without hr See money.</summary>
internal sealed record HrReferralView(
    int Id,
    string Number,
    string SourceType,
    int SourceId,
    int EmployeeId,
    string EmployeeNo,
    string EmployeeNameAr,
    string EmployeeNameEn,
    int? VehicleId,
    string Subject,
    string Details,
    decimal? Amount,
    string? ReferenceNo,
    string Status,
    DateTime ReferredAt,
    HrPersonRef? ReferredBy,
    DateTime? InvestigationStartedAt,
    HrPersonRef? InvestigatedBy,
    string? InvestigationNote,
    string? Outcome,
    string? ClosingNote,
    DateTime? ClosedAt,
    HrPersonRef? ClosedBy,
    int? DeductionInputId,
    string RowVersion);

/// <summary>
/// The cases referred to HR: <c>GET /api/hr/referrals</c> and <c>/{id}</c> (hr View), <c>POST /{id}/start-investigation</c>
/// (hr Edit), <c>POST /{id}/close</c> (hr Approve) with an outcome: <c>NoAction</c>, <c>Warning</c>, or
/// <c>DeductionApplied</c>, which raises a payroll deduction through the ordinary flow (the month must be open, hr
/// Create and See money) that the salary sheet's review then approves. Nothing is ever deducted automatically.
/// </summary>
internal static class HrReferralEndpoints
{
    internal sealed record ListResponse(IReadOnlyList<HrReferralView> Items, IReadOnlyDictionary<string, int> CountsByStatus);

    internal sealed record StartRequest(string? Note, string? RowVersion);

    internal sealed record DeductionRequest(string? Month, decimal? Amount, string? Reason);

    internal sealed record CloseRequest(string? Outcome, string? Note, DeductionRequest? Deduction, string? RowVersion);

    public static void Map(IEndpointRouteBuilder api)
    {
        api.MapGet("/hr/referrals", ListAsync)
            .RequirePermission(HrPermissions.Module, PermissionAction.View)
            .WithName("ListHrReferrals")
            .WithSummary("The cases referred to HR (status, employeeId), newest first, with counts per status");

        api.MapGet("/hr/referrals/{referralId:int}", GetAsync)
            .RequirePermission(HrPermissions.Module, PermissionAction.View)
            .WithName("GetHrReferral")
            .WithSummary("One referral");

        api.MapPost("/hr/referrals/{referralId:int}/start-investigation", StartAsync)
            .RequirePermission(HrPermissions.Module, PermissionAction.Edit)
            .WithName("StartHrInvestigation")
            .WithSummary("HR takes the case (hr Edit)");

        api.MapPost("/hr/referrals/{referralId:int}/close", CloseAsync)
            .RequirePermission(HrPermissions.Module, PermissionAction.Approve)
            .WithName("CloseHrReferral")
            .WithSummary("Close a referral with an outcome (hr Approve): NoAction, Warning, or DeductionApplied with a payroll deduction raised through the ordinary flow");
    }

    internal static async Task<Results<Ok<ListResponse>, ValidationProblem, ProblemHttpResult>> ListAsync(
        HttpContext httpContext, HrDbContext db, IUserDirectory users, CancellationToken cancellationToken, string? status = null, int? employeeId = null)
    {
        if (HrCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var wanted = Input.OptionalName<HrReferralStatus>(status, errors, "status");
        if (errors.HasErrors)
        {
            return HrProblems.Validation(errors);
        }

        var query = db.Referrals.AsNoTracking().Where(referral => employeeId == null || referral.EmployeeId == employeeId);
        var counts = await query.GroupBy(referral => referral.Status).Select(group => new { group.Key, Count = group.Count() }).ToListAsync(cancellationToken);
        var rows = await query.Where(referral => wanted == null || referral.Status == wanted)
            .Join(db.Employees, referral => referral.EmployeeId, employee => employee.Id, (referral, employee) => new { referral, employee })
            .OrderByDescending(row => row.referral.Id).Take(200).ToListAsync(cancellationToken);
        var people = await users.FindAsync(rows.SelectMany(row => new[] { row.referral.ReferredBy, row.referral.InvestigatedBy, row.referral.ClosedBy }).OfType<int>().Distinct().ToList(), cancellationToken);

        return TypedResults.Ok(new ListResponse(
            rows.Select(row => View(row.referral, row.employee, people, caller.SeesMoney)).ToList(),
            counts.ToDictionary(entry => entry.Key.ToString(), entry => entry.Count)));
    }

    internal static async Task<Results<Ok<HrReferralView>, ProblemHttpResult>> GetAsync(
        int referralId, HttpContext httpContext, HrDbContext db, IUserDirectory users, CancellationToken cancellationToken)
    {
        if (HrCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var view = await LoadAsync(db, users, referralId, caller.SeesMoney, cancellationToken);
        return view is null ? HrProblems.ReferralNotFound() : TypedResults.Ok(view);
    }

    internal static async Task<Results<Ok<HrReferralView>, ValidationProblem, ProblemHttpResult>> StartAsync(
        int referralId, StartRequest request, HttpContext httpContext, HrDbContext db, IUserDirectory users, CompanyClock clock, HrLog log, CancellationToken cancellationToken)
    {
        if (HrCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var note = Input.OptionalText(request.Note, HrReferralConfiguration.NoteMaxLength, errors, "note", "The note");
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (errors.HasErrors)
        {
            return HrProblems.Validation(errors);
        }

        var referral = await db.Referrals.SingleOrDefaultAsync(candidate => candidate.Id == referralId, cancellationToken);
        if (referral is null)
        {
            return HrProblems.ReferralNotFound();
        }

        if (!referral.CanMoveTo(HrReferralStatus.UnderInvestigation))
        {
            return HrProblems.WrongStatus($"Only an open referral can be investigated. This one is {referral.Status}.");
        }

        referral.StartInvestigation(caller.UserId, clock.UtcNow, note);
        db.Entry(referral).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        log.ReferralMoved(caller.UserId, referral.Id, referral.Status.ToString(), null);
        return TypedResults.Ok((await LoadAsync(db, users, referralId, caller.SeesMoney, cancellationToken))!);
    }

    /// <summary>
    /// Closes the case. With <c>DeductionApplied</c> the payroll input is added in the same transaction, under the
    /// payroll lock, with every rule of <see cref="PayrollInputs"/>: an open month, an employee who works in it, an
    /// amount and a reason. The sheet's review approves it later; no historical sheet changes.
    /// </summary>
    internal static async Task<Results<Ok<HrReferralView>, ValidationProblem, ProblemHttpResult>> CloseAsync(
        int referralId, CloseRequest request, HttpContext httpContext, HrDbContext db, IUserDirectory users, CompanyClock clock, HrLog log, CancellationToken cancellationToken)
    {
        if (HrCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var outcome = Input.RequiredName<HrReferralOutcome>(request.Outcome, errors, "outcome");
        var note = Input.RequiredText(request.Note, HrReferralConfiguration.NoteMaxLength, errors, "note", "The closing note");
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);

        DateOnly? month = null;
        decimal? amount = null;
        string? reason = null;
        if (outcome == HrReferralOutcome.DeductionApplied)
        {
            if (request.Deduction is null)
            {
                errors.Add("deduction", "A deduction needs its month, amount and reason.");
            }
            else
            {
                month = PayrollInputs.ParseMonth(request.Deduction.Month, errors, "deduction.month");
                amount = NumberInput.RequiredMoney(request.Deduction.Amount, errors, "deduction.amount", "The deduction amount");
                reason = Input.RequiredText(request.Deduction.Reason, PayrollInputConfiguration.ReasonMaxLength, errors, "deduction.reason", "The deduction reason");
            }
        }
        else if (request.Deduction is not null)
        {
            errors.Add("deduction", "Only the DeductionApplied outcome takes a deduction.");
        }

        if (errors.HasErrors)
        {
            return HrProblems.Validation(errors);
        }

        // A deduction is a payroll input: the same permissions as adding one by hand.
        if (outcome == HrReferralOutcome.DeductionApplied && !(caller.Can(PermissionAction.Create) && caller.SeesMoney))
        {
            return HrProblems.DeductionNeedsMoney();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await AppLock.TryAcquireAsync(db, AppLock.Payroll, cancellationToken))
        {
            return HrProblems.TryAgainLater();
        }

        var referral = await db.Referrals.SingleOrDefaultAsync(candidate => candidate.Id == referralId, cancellationToken);
        if (referral is null)
        {
            return HrProblems.ReferralNotFound();
        }

        if (!referral.CanMoveTo(HrReferralStatus.Closed))
        {
            return HrProblems.WrongStatus($"This referral is already {referral.Status}.");
        }

        int? deductionId = null;
        if (outcome == HrReferralOutcome.DeductionApplied)
        {
            var employee = await db.Employees.AsNoTracking().SingleAsync(candidate => candidate.Id == referral.EmployeeId, cancellationToken);
            if (!Employee.WorksIn(employee.HireDate, employee.EndDate, month!.Value, SalaryCalculator.LastDay(month.Value)))
            {
                return employee.Status == EmployeeStatus.Ended ? HrProblems.Ended() : HrProblems.Validation(Error("deduction.month", "The employee doesn't work in this month."));
            }

            if (await PayrollMonths.LockedAsync(db, month.Value, cancellationToken) is { } locked)
            {
                return HrProblems.Locked(PayrollMonths.Format(locked));
            }

            var input = new PayrollInput
            {
                EmployeeId = employee.Id,
                Month = month.Value,
                Kind = PayrollInputKind.Deduction,
                Amount = amount!.Value,
                Reason = $"{referral.Number}: {reason}",
                CreatedAt = clock.UtcNow,
                CreatedBy = caller.UserId,
            };
            db.PayrollInputs.Add(input);
            await db.SaveChangesAsync(cancellationToken);
            deductionId = input.Id;
            log.InputAdded(caller.UserId, employee.Id, input.Id, input.Kind.ToString());
        }

        referral.Close(outcome!.Value, caller.UserId, clock.UtcNow, note!, deductionId);
        db.Entry(referral).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        await transaction.CommitAsync(cancellationToken);
        log.ReferralMoved(caller.UserId, referral.Id, referral.Status.ToString(), outcome.ToString());
        return TypedResults.Ok((await LoadAsync(db, users, referralId, caller.SeesMoney, cancellationToken))!);
    }

    private static async Task<HrReferralView?> LoadAsync(HrDbContext db, IUserDirectory users, int referralId, bool seesMoney, CancellationToken cancellationToken)
    {
        var row = await db.Referrals.AsNoTracking().Where(referral => referral.Id == referralId)
            .Join(db.Employees, referral => referral.EmployeeId, employee => employee.Id, (referral, employee) => new { referral, employee })
            .SingleOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return null;
        }

        var people = await users.FindAsync(new[] { row.referral.ReferredBy, row.referral.InvestigatedBy, row.referral.ClosedBy }.OfType<int>().Distinct().ToList(), cancellationToken);
        return View(row.referral, row.employee, people, seesMoney);
    }

    private static HrReferralView View(HrReferral referral, Employee employee, IReadOnlyDictionary<int, UserSummary> people, bool seesMoney) => new(
        referral.Id, referral.Number, referral.SourceType, referral.SourceId, referral.EmployeeId, employee.EmployeeNo, employee.NameAr, employee.NameEn, referral.VehicleId,
        referral.Subject, referral.Details, seesMoney ? referral.Amount : null, referral.ReferenceNo, referral.Status.ToString(), referral.ReferredAt,
        HrPersonRef.From(referral.ReferredBy, people), referral.InvestigationStartedAt, HrPersonRef.From(referral.InvestigatedBy, people), referral.InvestigationNote,
        referral.Outcome?.ToString(), referral.ClosingNote, referral.ClosedAt, HrPersonRef.From(referral.ClosedBy, people), referral.DeductionInputId,
        RowVersions.Format(referral.RowVersion));

    private static ValidationErrors Error(string field, string message)
    {
        var errors = new ValidationErrors();
        errors.Add(field, message);
        return errors;
    }
}
