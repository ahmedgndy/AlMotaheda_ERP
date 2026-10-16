using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.HR.Common;
using AlMotaheda.Modules.HR.Data;
using AlMotaheda.Modules.HR.Data.Configurations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.HR.Features.Payroll;

/// <summary><c>GET /api/hr/salary-sheets</c> (hr View) and <c>GET /api/hr/salary-sheets/{id}</c>: newest month first.</summary>
internal static class ReadSalarySheets
{
    internal sealed record Response(IReadOnlyList<SalarySheetSummary> Items, int Page, int PageSize, int TotalCount);

    public static void Map(IEndpointRouteBuilder api)
    {
        api.MapGet("/hr/salary-sheets", ListAsync)
            .RequirePermission(HrPermissions.Module, PermissionAction.View)
            .WithName("ListSalarySheets")
            .WithSummary("List the salary sheets, newest month first (optional status)");

        api.MapGet("/hr/salary-sheets/{sheetId:int}", GetAsync)
            .RequirePermission(HrPermissions.Module, PermissionAction.View)
            .WithName("GetSalarySheet")
            .WithSummary("Get one salary sheet with its lines");
    }

    internal static async Task<Results<Ok<Response>, ValidationProblem, ProblemHttpResult>> ListAsync(
        HttpContext httpContext, HrDbContext db, CancellationToken cancellationToken,
        string? status = null, int page = 1, int pageSize = Paging.DefaultPageSize)
    {
        if (HrCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        Paging.Validate(page, pageSize, errors);
        var statusFilter = Input.OptionalName<SalarySheetStatus>(status, errors, "status");
        if (errors.HasErrors)
        {
            return HrProblems.Validation(errors);
        }

        var query = db.SalarySheets.AsNoTracking();
        if (statusFilter is { } wanted)
        {
            query = query.Where(sheet => sheet.Status == wanted);
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var sheets = await query.OrderByDescending(sheet => sheet.Month)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(new Response(sheets.Select(sheet => SalarySheetSummary.From(sheet, caller.SeesMoney)).ToList(), page, pageSize, totalCount));
    }

    internal static async Task<Results<Ok<SalarySheetView>, ProblemHttpResult>> GetAsync(
        int sheetId, HttpContext httpContext, HrDbContext db, IUserDirectory users, CancellationToken cancellationToken)
    {
        if (HrCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var view = await SalarySheetView.LoadAsync(db, users, sheetId, caller.SeesMoney, cancellationToken);
        return view is null ? HrProblems.SheetNotFound() : TypedResults.Ok(view);
    }
}

/// <summary>
/// HR's side of the sheet (hr Create or Edit, and See money):
/// <list type="bullet">
///   <item><c>POST /api/hr/salary-sheets</c> <c>{ month }</c>: prepare the month (Draft, lines calculated). One sheet
///   per month; not for a month that hasn't started.</item>
///   <item><c>POST /api/hr/salary-sheets/{id}/recalculate</c>: refresh the lines from the current salaries and inputs
///   (Draft or Returned).</item>
///   <item><c>POST /api/hr/salary-sheets/{id}/submit</c>: recalculate one last time and send it to the Accountant.</item>
/// </list>
/// 409 missing_salary, negative_net, empty_sheet: nothing is saved, the title names the employees to fix.
/// </summary>
internal static class PrepareSalarySheet
{
    internal sealed record PrepareRequest(string? Month);

    internal sealed record StepRequest(string? RowVersion);

    public static void Map(IEndpointRouteBuilder api)
    {
        api.MapPost("/hr/salary-sheets", PrepareAsync)
            .RequirePermission(HrPermissions.Module, PermissionAction.Create)
            .RequirePermission(HrPermissions.Module, PermissionAction.SeeMoney)
            .WithName("PrepareSalarySheet")
            .WithSummary("Prepare a month's salary sheet. 409 sheet_exists, missing_salary, negative_net, empty_sheet");

        api.MapPost("/hr/salary-sheets/{sheetId:int}/recalculate", (int sheetId, StepRequest request, HttpContext httpContext, HrDbContext db,
                IUserDirectory users, CompanyClock clock, HrLog log, CancellationToken cancellationToken) =>
                StepAsync(sheetId, request, submit: false, httpContext, db, users, clock, log, cancellationToken))
            .RequirePermission(HrPermissions.Module, PermissionAction.Edit)
            .RequirePermission(HrPermissions.Module, PermissionAction.SeeMoney)
            .WithName("RecalculateSalarySheet")
            .WithSummary("Recalculate a Draft or Returned salary sheet. 409 invalid_status, missing_salary, negative_net, empty_sheet");

        api.MapPost("/hr/salary-sheets/{sheetId:int}/submit", (int sheetId, StepRequest request, HttpContext httpContext, HrDbContext db,
                IUserDirectory users, CompanyClock clock, HrLog log, CancellationToken cancellationToken) =>
                StepAsync(sheetId, request, submit: true, httpContext, db, users, clock, log, cancellationToken))
            .RequirePermission(HrPermissions.Module, PermissionAction.Edit)
            .RequirePermission(HrPermissions.Module, PermissionAction.SeeMoney)
            .WithName("SubmitSalarySheet")
            .WithSummary("Recalculate and submit a salary sheet to the Accountant. 409 invalid_status, missing_salary, negative_net, empty_sheet");
    }

    internal static async Task<Results<Created<SalarySheetView>, ValidationProblem, ProblemHttpResult>> PrepareAsync(
        PrepareRequest request, HttpContext httpContext, HrDbContext db, IUserDirectory users, CompanyClock clock, HrLog log,
        CancellationToken cancellationToken)
    {
        if (HrCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var month = PayrollInputs.ParseMonth(request.Month, errors);
        if (month > PayrollMonths.FirstDay(clock.Today))
        {
            errors.Add("month", "A month that hasn't started can't be prepared.");
        }

        if (errors.HasErrors)
        {
            return HrProblems.Validation(errors);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await AppLock.TryAcquireAsync(db, AppLock.Payroll, cancellationToken))
        {
            return HrProblems.TryAgainLater();
        }

        if (await db.SalarySheets.AnyAsync(sheet => sheet.Month == month, cancellationToken))
        {
            return HrProblems.SheetTaken();
        }

        var result = await SalarySheetCalculation.RunAsync(db, month!.Value, cancellationToken);
        if (Blocked(result) is { } problem)
        {
            return problem;
        }

        var now = clock.UtcNow;
        var sheet = new SalarySheet { Month = month.Value, CreatedAt = now, CreatedBy = caller.UserId };
        sheet.Calculate(result.Lines, caller.UserId, now);
        db.SalarySheets.Add(sheet);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, SalarySheetConfiguration.MonthIndex))
        {
            return HrProblems.SheetTaken();
        }

        await transaction.CommitAsync(cancellationToken);
        log.SheetCalculated(caller.UserId, sheet.Id, sheet.EmployeeCount);

        return TypedResults.Created($"/api/hr/salary-sheets/{sheet.Id}", await SalarySheetView.FromAsync(sheet, users, seesMoney: true, cancellationToken));
    }

    internal static async Task<Results<Ok<SalarySheetView>, ValidationProblem, ProblemHttpResult>> StepAsync(
        int sheetId, StepRequest request, bool submit, HttpContext httpContext, HrDbContext db, IUserDirectory users, CompanyClock clock,
        HrLog log, CancellationToken cancellationToken)
    {
        if (HrCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
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

        var sheet = await db.SalarySheets.Include(candidate => candidate.Lines)
            .SingleOrDefaultAsync(candidate => candidate.Id == sheetId, cancellationToken);
        if (sheet is null)
        {
            return HrProblems.SheetNotFound();
        }

        if (!sheet.IsEditable)
        {
            return HrProblems.WrongStatus($"Only a Draft or Returned salary sheet can be changed or submitted. This one is {sheet.Status}.");
        }

        var result = await SalarySheetCalculation.RunAsync(db, sheet.Month, cancellationToken);
        if (Blocked(result) is { } problem)
        {
            return problem;
        }

        var now = clock.UtcNow;
        sheet.Calculate(result.Lines, caller.UserId, now);
        if (submit)
        {
            sheet.Submit(caller.UserId, now);
        }

        db.Entry(sheet).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        await transaction.CommitAsync(cancellationToken);
        log.SheetCalculated(caller.UserId, sheet.Id, sheet.EmployeeCount);
        if (submit)
        {
            log.SheetMoved(caller.UserId, sheet.Id, sheet.Status.ToString());
        }

        return TypedResults.Ok(await SalarySheetView.FromAsync(sheet, users, seesMoney: true, cancellationToken));
    }

    private static ProblemHttpResult? Blocked(SalaryCalculator.Result result) =>
        result.MissingSalary.Count > 0 ? HrProblems.NoSalary(result.MissingSalary)
        : result.NegativeNet.Count > 0 ? HrProblems.BelowZero(result.NegativeNet)
        : result.Lines.Count == 0 ? HrProblems.Empty()
        : null;
}

/// <summary>
/// The Accountant's review (hr Approve and See money): <c>POST /api/hr/salary-sheets/{id}/approve</c> <c>{ note? }</c>
/// or <c>/return</c> <c>{ reason }</c>. Only a Submitted sheet; never by the person who submitted it (409 own_record).
/// </summary>
internal static class ReviewSalarySheet
{
    internal sealed record Request(string? Note, string? Reason, string? RowVersion);

    public static void Map(IEndpointRouteBuilder api)
    {
        api.MapPost("/hr/salary-sheets/{sheetId:int}/approve", (int sheetId, Request request, HttpContext httpContext, HrDbContext db,
                IUserDirectory users, CompanyClock clock, HrLog log, CancellationToken cancellationToken) =>
                HandleAsync(sheetId, request, approve: true, httpContext, db, users, clock, log, cancellationToken))
            .RequirePermission(HrPermissions.Module, PermissionAction.Approve)
            .RequirePermission(HrPermissions.Module, PermissionAction.SeeMoney)
            .WithName("ApproveSalarySheet")
            .WithSummary("Approve a submitted salary sheet (hr Approve). 409 invalid_status, own_record");

        api.MapPost("/hr/salary-sheets/{sheetId:int}/return", (int sheetId, Request request, HttpContext httpContext, HrDbContext db,
                IUserDirectory users, CompanyClock clock, HrLog log, CancellationToken cancellationToken) =>
                HandleAsync(sheetId, request, approve: false, httpContext, db, users, clock, log, cancellationToken))
            .RequirePermission(HrPermissions.Module, PermissionAction.Approve)
            .RequirePermission(HrPermissions.Module, PermissionAction.SeeMoney)
            .WithName("ReturnSalarySheet")
            .WithSummary("Return a submitted salary sheet to HR with a reason (hr Approve). 409 invalid_status, own_record");
    }

    internal static async Task<Results<Ok<SalarySheetView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int sheetId, Request request, bool approve, HttpContext httpContext, HrDbContext db, IUserDirectory users, CompanyClock clock,
        HrLog log, CancellationToken cancellationToken)
    {
        if (HrCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var note = approve
            ? Input.OptionalText(request.Note, SalarySheetConfiguration.NoteMaxLength, errors, "note", "The note")
            : Input.RequiredText(request.Reason, SalarySheetConfiguration.NoteMaxLength, errors, "reason", "The reason");
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (errors.HasErrors)
        {
            return HrProblems.Validation(errors);
        }

        var sheet = await db.SalarySheets.Include(candidate => candidate.Lines)
            .SingleOrDefaultAsync(candidate => candidate.Id == sheetId, cancellationToken);
        if (sheet is null)
        {
            return HrProblems.SheetNotFound();
        }

        if (sheet.Status != SalarySheetStatus.Submitted)
        {
            return HrProblems.WrongStatus($"Only a submitted salary sheet can be reviewed. This one is {sheet.Status}.");
        }

        if (sheet.SubmittedBy == caller.UserId)
        {
            return HrProblems.ReviewerIsSubmitter();
        }

        sheet.Review(approve, caller.UserId, clock.UtcNow, note);
        db.Entry(sheet).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        log.SheetMoved(caller.UserId, sheet.Id, sheet.Status.ToString());
        return TypedResults.Ok(await SalarySheetView.FromAsync(sheet, users, seesMoney: true, cancellationToken));
    }
}
