using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.HR.Common;
using AlMotaheda.Modules.HR.Data;
using AlMotaheda.Modules.HR.Data.Configurations;
using AlMotaheda.Modules.HR.Features.Payroll;
using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.HR.Features.CostAllocation;

/// <summary>An employee's working days on a project in a month, as every endpoint returns it.</summary>
internal sealed record ProjectDaysView(
    int Id,
    int EmployeeId,
    string EmployeeNo,
    string EmployeeNameAr,
    string EmployeeNameEn,
    int ProjectId,
    string? ProjectCode,
    string? ProjectNameAr,
    string? ProjectNameEn,
    int? ProjectItemId,
    string Month,
    decimal Days,
    string? Notes,
    string Status,
    DateTime CreatedAt,
    HrPersonRef? Recorder,
    DateTime? ReviewedAt,
    HrPersonRef? Reviewer,
    string? ReviewNote,
    string RowVersion);

internal static class ProjectDaysViews
{
    public static async Task<IReadOnlyList<ProjectDaysView>> BuildAsync(
        HrDbContext db, IUserDirectory users, IProjectDirectory projects, IReadOnlyList<ProjectWorkDays> entries, CancellationToken cancellationToken)
    {
        var employeeIds = entries.Select(entry => entry.EmployeeId).Distinct().ToList();
        var employees = await db.Employees.AsNoTracking().Where(employee => employeeIds.Contains(employee.Id))
            .Select(employee => new { employee.Id, employee.EmployeeNo, employee.NameAr, employee.NameEn })
            .ToDictionaryAsync(employee => employee.Id, cancellationToken);
        var projectRefs = await projects.FindAsync(entries.Select(entry => entry.ProjectId).Distinct().ToList(), cancellationToken);
        var peopleIds = entries.SelectMany(entry => new[] { entry.CreatedBy, entry.ReviewedBy ?? 0 }).Where(id => id > 0).Distinct().ToList();
        var people = peopleIds.Count == 0 ? new Dictionary<int, UserSummary>() : await users.FindAsync(peopleIds, cancellationToken);

        return entries.Select(entry =>
        {
            var employee = employees[entry.EmployeeId];
            projectRefs.TryGetValue(entry.ProjectId, out var project);
            return new ProjectDaysView(
                entry.Id, entry.EmployeeId, employee.EmployeeNo, employee.NameAr, employee.NameEn, entry.ProjectId, project?.Code, project?.NameAr,
                project?.NameEn, entry.ProjectItemId, PayrollMonths.Format(entry.Month), entry.Days, entry.Notes, entry.Status.ToString(), entry.CreatedAt,
                HrPersonRef.From(entry.CreatedBy, people), entry.ReviewedAt, HrPersonRef.From(entry.ReviewedBy, people), entry.ReviewNote,
                RowVersions.Format(entry.RowVersion));
        }).ToList();
    }
}

/// <summary>The checks shared by recording and fixing an entry.</summary>
internal static class ProjectDaysRules
{
    /// <summary>Half days are the smallest unit, like daily labor.</summary>
    public static decimal? Days(decimal? value, ValidationErrors errors)
    {
        if (value is not { } days || days <= 0 || days > 31 || days * 2 != decimal.Round(days * 2))
        {
            errors.Add("days", "The days must be more than 0 and at most 31, in whole or half days.");
            return null;
        }

        return days;
    }

    /// <summary>
    /// The days of one employee in one month, on every project, can't be more than the month's working days: the
    /// salary can't be charged more than once over. An approved entry about to be corrected is not counted twice.
    /// </summary>
    public static async Task<ProblemHttpResult?> FitsMonthAsync(
        HrDbContext db, int employeeId, int projectId, DateOnly month, decimal days, int? exceptEntryId, bool replacesApproved, CancellationToken cancellationToken)
    {
        var policy = await db.CostAllocationPolicies.AsNoTracking().SingleAsync(cancellationToken);
        var limit = policy.WorkingDaysPerMonth ?? DateTime.DaysInMonth(month.Year, month.Month);

        var others = await db.ProjectWorkDays.AsNoTracking()
            .Where(entry => entry.EmployeeId == employeeId && entry.Month == month && entry.Id != exceptEntryId
                && (entry.Status == ProjectDaysStatus.Recorded || entry.Status == ProjectDaysStatus.Returned || entry.Status == ProjectDaysStatus.Approved)
                && !(replacesApproved && entry.ProjectId == projectId && entry.Status == ProjectDaysStatus.Approved))
            .SumAsync(entry => (decimal?)entry.Days, cancellationToken) ?? 0;

        return others + days > limit ? HrProblems.DaysOverMonth(limit) : null;
    }

    /// <summary>The project must exist and be open; the item, when given, must be an active item of that project.</summary>
    public static async Task CheckProjectAsync(
        IProjectDirectory projects, IProjectItems items, int projectId, int? itemId, ValidationErrors errors, CancellationToken cancellationToken)
    {
        var found = await projects.FindAsync([projectId], cancellationToken);
        if (!found.TryGetValue(projectId, out var project))
        {
            errors.Add("projectId", "The project was not found.");
            return;
        }

        if (project.IsCompleted)
        {
            errors.Add("projectId", "The project is completed.");
        }

        if (itemId is { } wanted)
        {
            var item = (await items.FindAsync(projectId, [wanted], cancellationToken)).GetValueOrDefault(wanted);
            if (item is null || !item.IsActive)
            {
                errors.Add("projectItemId", "The project item was not found on this project, or is inactive.");
            }
        }
    }
}

/// <summary>
/// <c>GET /api/hr/project-days</c>: employees' working days on projects (filters: month, employee, project, status).
/// hr View; no amounts here.
/// </summary>
internal static class ListProjectDays
{
    internal sealed record Response(IReadOnlyList<ProjectDaysView> Items, int Page, int PageSize, int TotalCount);

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/hr/project-days", HandleAsync)
            .RequirePermission(HrPermissions.Module, PermissionAction.View)
            .WithName("ListProjectDays")
            .WithSummary("List employees' working days on projects (month yyyy-MM; employeeId; projectId; status)");

    internal static async Task<Results<Ok<Response>, ValidationProblem>> HandleAsync(
        HrDbContext db,
        IUserDirectory users,
        IProjectDirectory projects,
        CancellationToken cancellationToken,
        string? month = null,
        int? employeeId = null,
        int? projectId = null,
        string? status = null,
        int page = 1,
        int pageSize = Paging.DefaultPageSize)
    {
        var errors = new ValidationErrors();
        Paging.Validate(page, pageSize, errors);
        DateOnly? monthFilter = month is null ? null : PayrollInputs.ParseMonth(month, errors);
        var statusFilter = Input.OptionalName<ProjectDaysStatus>(status, errors, "status");
        if (errors.HasErrors)
        {
            return HrProblems.Validation(errors);
        }

        var query = db.ProjectWorkDays.AsNoTracking();
        if (monthFilter is { } wantedMonth)
        {
            query = query.Where(entry => entry.Month == wantedMonth);
        }

        if (employeeId is { } wantedEmployee)
        {
            query = query.Where(entry => entry.EmployeeId == wantedEmployee);
        }

        if (projectId is { } wantedProject)
        {
            query = query.Where(entry => entry.ProjectId == wantedProject);
        }

        if (statusFilter is { } wantedStatus)
        {
            query = query.Where(entry => entry.Status == wantedStatus);
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var entries = await query.OrderByDescending(entry => entry.Month).ThenBy(entry => entry.EmployeeId).ThenBy(entry => entry.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);

        return TypedResults.Ok(new Response(await ProjectDaysViews.BuildAsync(db, users, projects, entries, cancellationToken), page, pageSize, totalCount));
    }
}

/// <summary>
/// <c>POST /api/hr/project-days</c>: record an employee's working days on a project in a month (hr Create), from the
/// site attendance. Saved as Recorded, waiting for hr Approve. When the employee, project and month already have an
/// approved entry, this is a correction: it replaces that entry when approved.
/// </summary>
internal static class RecordProjectDays
{
    internal sealed record Request(int? EmployeeId, int? ProjectId, string? Month, decimal? Days, int? ProjectItemId, string? Notes);

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/hr/project-days", HandleAsync)
            .RequirePermission(HrPermissions.Module, PermissionAction.Create)
            .WithName("RecordProjectDays")
            .WithSummary("Record an employee's working days on a project in a month (Recorded, waiting for review). 409 days_over_month, days_pending");

    internal static async Task<Results<Created<ProjectDaysView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        Request request,
        HttpContext httpContext,
        HrDbContext db,
        IUserDirectory users,
        IProjectDirectory projects,
        IProjectItems items,
        CompanyClock clock,
        HrLog log,
        CancellationToken cancellationToken)
    {
        if (HrCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        if (request.EmployeeId is not > 0)
        {
            errors.Add("employeeId", "employeeId is required.");
        }

        if (request.ProjectId is not > 0)
        {
            errors.Add("projectId", "projectId is required.");
        }

        var month = PayrollInputs.ParseMonth(request.Month, errors);
        if (month > PayrollMonths.FirstDay(clock.Today))
        {
            errors.Add("month", "Days can't be recorded for a month that hasn't started.");
        }

        var days = ProjectDaysRules.Days(request.Days, errors);
        var notes = Input.OptionalText(request.Notes, ProjectWorkDaysConfiguration.NoteMaxLength, errors, "notes", "The notes");
        if (errors.HasErrors)
        {
            return HrProblems.Validation(errors);
        }

        var employee = await db.Employees.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == request.EmployeeId, cancellationToken);
        if (employee is null)
        {
            return HrProblems.EmployeeNotFound();
        }

        if (!Employee.WorksIn(employee.HireDate, employee.EndDate, month!.Value, SalaryCalculator.LastDay(month.Value)))
        {
            return HrProblems.Validation(Error("month", "The employee doesn't work in this month."));
        }

        await ProjectDaysRules.CheckProjectAsync(projects, items, request.ProjectId!.Value, request.ProjectItemId, errors, cancellationToken);
        if (errors.HasErrors)
        {
            return HrProblems.Validation(errors);
        }

        var replacesApproved = await db.ProjectWorkDays.AnyAsync(
            entry => entry.EmployeeId == employee.Id && entry.ProjectId == request.ProjectId && entry.Month == month && entry.Status == ProjectDaysStatus.Approved,
            cancellationToken);
        if (await ProjectDaysRules.FitsMonthAsync(db, employee.Id, request.ProjectId.Value, month.Value, days!.Value, null, replacesApproved, cancellationToken) is { } over)
        {
            return over;
        }

        var entry = new ProjectWorkDays
        {
            EmployeeId = employee.Id,
            ProjectId = request.ProjectId.Value,
            ProjectItemId = request.ProjectItemId,
            Month = month.Value,
            Days = days.Value,
            Notes = notes,
            Status = ProjectDaysStatus.Recorded,
            CreatedAt = clock.UtcNow,
            CreatedBy = caller.UserId,
        };
        db.ProjectWorkDays.Add(entry);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, ProjectWorkDaysConfiguration.PendingIndex))
        {
            return HrProblems.DaysPending();
        }

        log.ProjectDaysRecorded(caller.UserId, entry.Id, entry.EmployeeId, entry.ProjectId);
        var view = (await ProjectDaysViews.BuildAsync(db, users, projects, [entry], cancellationToken))[0];
        return TypedResults.Created($"/api/hr/project-days/{entry.Id}", view);
    }

    private static ValidationErrors Error(string field, string message)
    {
        var errors = new ValidationErrors();
        errors.Add(field, message);
        return errors;
    }
}

/// <summary>
/// <c>PUT /api/hr/project-days/{id}</c>: its recorder fixes a recorded or returned entry and sends it again.
/// </summary>
internal static class FixProjectDays
{
    internal sealed record Request(decimal? Days, int? ProjectItemId, string? Notes, string? RowVersion);

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPut("/hr/project-days/{entryId:int}", HandleAsync)
            .RequirePermission(HrPermissions.Module, PermissionAction.Create)
            .WithName("FixProjectDays")
            .WithSummary("Fix a recorded or returned project-days entry (its recorder only); it waits for review again");

    internal static async Task<Results<Ok<ProjectDaysView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int entryId,
        Request request,
        HttpContext httpContext,
        HrDbContext db,
        IUserDirectory users,
        IProjectDirectory projects,
        IProjectItems items,
        CompanyClock clock,
        HrLog log,
        CancellationToken cancellationToken)
    {
        if (HrCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var days = ProjectDaysRules.Days(request.Days, errors);
        var notes = Input.OptionalText(request.Notes, ProjectWorkDaysConfiguration.NoteMaxLength, errors, "notes", "The notes");
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (errors.HasErrors)
        {
            return HrProblems.Validation(errors);
        }

        var entry = await db.ProjectWorkDays.SingleOrDefaultAsync(candidate => candidate.Id == entryId, cancellationToken);
        if (entry is null)
        {
            return HrProblems.NotFound("project days entry");
        }

        if (entry.CreatedBy != caller.UserId)
        {
            return ApiProblem.ForbiddenResult("Only the person who recorded these days can fix them.");
        }

        if (entry.Status is not (ProjectDaysStatus.Recorded or ProjectDaysStatus.Returned))
        {
            return HrProblems.WrongStatus($"Only recorded or returned days can be fixed. These are {entry.Status}.");
        }

        await ProjectDaysRules.CheckProjectAsync(projects, items, entry.ProjectId, request.ProjectItemId, errors, cancellationToken);
        if (errors.HasErrors)
        {
            return HrProblems.Validation(errors);
        }

        var replacesApproved = await db.ProjectWorkDays.AnyAsync(
            other => other.EmployeeId == entry.EmployeeId && other.ProjectId == entry.ProjectId && other.Month == entry.Month && other.Status == ProjectDaysStatus.Approved,
            cancellationToken);
        if (await ProjectDaysRules.FitsMonthAsync(db, entry.EmployeeId, entry.ProjectId, entry.Month, days!.Value, entry.Id, replacesApproved, cancellationToken) is { } over)
        {
            return over;
        }

        entry.Days = days.Value;
        entry.ProjectItemId = request.ProjectItemId;
        entry.Notes = notes;
        entry.Resubmit(caller.UserId, clock.UtcNow);
        db.Entry(entry).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        log.ProjectDaysMoved(caller.UserId, entry.Id, entry.Status.ToString());
        return TypedResults.Ok((await ProjectDaysViews.BuildAsync(db, users, projects, [entry], cancellationToken))[0]);
    }
}

/// <summary>
/// <c>POST /api/hr/project-days/{id}/approve</c>, <c>/return</c>, <c>/reject</c>: hr Approve (the Accountant), never by
/// its recorder; a reason to return or reject. Approving a correction supersedes the entry it corrects.
/// </summary>
internal static class ReviewProjectDays
{
    internal sealed record Request(string? Note, string? RowVersion);

    public static void Map(IEndpointRouteBuilder api)
    {
        foreach (var (path, outcome) in new[] { ("approve", ProjectDaysStatus.Approved), ("return", ProjectDaysStatus.Returned), ("reject", ProjectDaysStatus.Rejected) })
        {
            api.MapPost($"/hr/project-days/{{entryId:int}}/{path}",
                    (int entryId, Request request, HttpContext httpContext, HrDbContext db, IUserDirectory users, IProjectDirectory projects,
                        CompanyClock clock, HrLog log, CancellationToken cancellationToken) =>
                        HandleAsync(outcome, entryId, request, httpContext, db, users, projects, clock, log, cancellationToken))
                .RequirePermission(HrPermissions.Module, PermissionAction.Approve)
                .WithName($"{char.ToUpperInvariant(path[0])}{path[1..]}ProjectDays")
                .WithSummary($"Review project days: {path} (hr Approve, never your own). 409 own_record, invalid_status, days_over_month");
        }
    }

    internal static async Task<Results<Ok<ProjectDaysView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        ProjectDaysStatus outcome,
        int entryId,
        Request request,
        HttpContext httpContext,
        HrDbContext db,
        IUserDirectory users,
        IProjectDirectory projects,
        CompanyClock clock,
        HrLog log,
        CancellationToken cancellationToken)
    {
        if (HrCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var note = outcome == ProjectDaysStatus.Approved
            ? Input.OptionalText(request.Note, ProjectWorkDaysConfiguration.NoteMaxLength, errors, "note", "The note")
            : Input.RequiredText(request.Note, ProjectWorkDaysConfiguration.NoteMaxLength, errors, "note", "The reason");
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (errors.HasErrors)
        {
            return HrProblems.Validation(errors);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await AppLock.TryAcquireAsync(db, AppLock.Allocation, cancellationToken))
        {
            return HrProblems.TryAgainLater();
        }

        var entry = await db.ProjectWorkDays.SingleOrDefaultAsync(candidate => candidate.Id == entryId, cancellationToken);
        if (entry is null)
        {
            return HrProblems.NotFound("project days entry");
        }

        if (entry.CreatedBy == caller.UserId)
        {
            return HrProblems.ReviewerIsRecorder();
        }

        if (entry.Status != ProjectDaysStatus.Recorded)
        {
            return HrProblems.WrongStatus($"Only recorded days can be reviewed. These are {entry.Status}.");
        }

        var now = clock.UtcNow;
        if (outcome == ProjectDaysStatus.Approved)
        {
            if (await ProjectDaysRules.FitsMonthAsync(db, entry.EmployeeId, entry.ProjectId, entry.Month, entry.Days, entry.Id, replacesApproved: true, cancellationToken) is { } over)
            {
                return over;
            }

            // A correction: the approved entry it replaces is superseded first, so the "one approved" index holds.
            var replaced = await db.ProjectWorkDays.SingleOrDefaultAsync(
                other => other.EmployeeId == entry.EmployeeId && other.ProjectId == entry.ProjectId && other.Month == entry.Month
                    && other.Status == ProjectDaysStatus.Approved,
                cancellationToken);
            if (replaced is not null)
            {
                replaced.Supersede(caller.UserId, now);
                await db.SaveChangesAsync(cancellationToken);
            }
        }

        entry.Review(outcome, caller.UserId, now, note);
        db.Entry(entry).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        await transaction.CommitAsync(cancellationToken);
        log.ProjectDaysMoved(caller.UserId, entry.Id, entry.Status.ToString());
        return TypedResults.Ok((await ProjectDaysViews.BuildAsync(db, users, projects, [entry], cancellationToken))[0]);
    }
}

/// <summary>
/// <c>GET /api/hr/project-options</c> and <c>/{projectId}/items</c>: the open projects and their active items, for the
/// project-days form (hr Create). HR has no project access, so it gets names only: no figures, no records.
/// </summary>
internal static class ProjectOptions
{
    public static void Map(IEndpointRouteBuilder api)
    {
        api.MapGet("/hr/project-options", async (IProjectDirectory projects, CancellationToken cancellationToken) =>
                TypedResults.Ok(await projects.ListOpenAsync(cancellationToken)))
            .RequirePermission(HrPermissions.Module, PermissionAction.Create)
            .WithName("ListHrProjectOptions")
            .WithSummary("The open projects, to record employees' days on (names only)");

        api.MapGet("/hr/project-options/{projectId:int}/items", async (int projectId, IProjectDirectory projects, CancellationToken cancellationToken) =>
                TypedResults.Ok(await projects.ListActiveItemsAsync(projectId, cancellationToken)))
            .RequirePermission(HrPermissions.Module, PermissionAction.Create)
            .WithName("ListHrProjectItemOptions")
            .WithSummary("The active items of a project, to record employees' days on (names only)");
    }
}
