using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Fleet.Common;
using AlMotaheda.Modules.Fleet.Data;
using AlMotaheda.Modules.Fleet.Data.Configurations;
using AlMotaheda.Modules.Fleet.Security;
using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Fleet.Features.Repairs;

/// <summary>One repair request, as every endpoint returns it. It holds no amount: the repair's cost is its own record.</summary>
internal sealed record RepairRequestView(
    int Id,
    int ProjectId,
    int EquipmentId,
    string AssetNo,
    string EquipmentNameAr,
    string EquipmentNameEn,
    string Problem,
    string Priority,
    string Status,
    DateTime RequestedAt,
    FleetPerson Requester,
    DateTime? ReviewedAt,
    FleetPerson? Reviewer,
    string? ReviewNote,
    DateTime? CompletedAt,
    FleetPerson? CompletedBy,
    string? Result,
    int? EquipmentCostId,
    DateTime? CancelledAt,
    FleetPerson? CancelledBy,
    string? CancelReason,
    string RowVersion);

internal sealed record RepairRequestRow(
    int Id, int ProjectId, int EquipmentId, string AssetNo, string EquipmentNameAr, string EquipmentNameEn, string Problem, RepairPriority Priority,
    RepairRequestStatus Status, DateTime RequestedAt, int RequestedBy, DateTime? ReviewedAt, int? ReviewedBy, string? ReviewNote,
    DateTime? CompletedAt, int? CompletedBy, string? Result, int? EquipmentCostId, DateTime? CancelledAt, int? CancelledBy,
    string? CancelReason, byte[] RowVersion)
{
    public RepairRequestView ToView(IReadOnlyDictionary<int, UserSummary> people) => new(
        Id, ProjectId, EquipmentId, AssetNo, EquipmentNameAr, EquipmentNameEn, Problem, Priority.ToString(), Status.ToString(), RequestedAt,
        FleetPerson.From(RequestedBy, people), ReviewedAt, FleetPerson.FromOptional(ReviewedBy, people), ReviewNote, CompletedAt,
        FleetPerson.FromOptional(CompletedBy, people), Result, EquipmentCostId, CancelledAt, FleetPerson.FromOptional(CancelledBy, people),
        CancelReason, RowVersions.Format(RowVersion));

    public static async Task<IReadOnlyList<RepairRequestView>> ViewsAsync(IReadOnlyList<RepairRequestRow> rows, IUserDirectory users, CancellationToken cancellationToken)
    {
        var people = await Lookups.UsersAsync(
            users, rows.SelectMany(row => new[] { (int?)row.RequestedBy, row.ReviewedBy, row.CompletedBy, row.CancelledBy }), cancellationToken);
        return rows.Select(row => row.ToView(people)).ToList();
    }
}

/// <summary>
/// The repair request queries. Filters run on the table before the projection, so they translate to SQL (a filter on
/// the projected row can't).
/// </summary>
internal static class RepairRequestQueries
{
    public static IQueryable<RepairRequest> Of(FleetDbContext db, int projectId) =>
        db.RepairRequests.AsNoTracking().Where(request => request.ProjectId == projectId);

    public static IQueryable<RepairRequestRow> Project(IQueryable<RepairRequest> requests) =>
        requests.Select(request => new RepairRequestRow(
                request.Id, request.ProjectId, request.EquipmentId, request.Equipment.AssetNo, request.Equipment.NameAr, request.Equipment.NameEn,
                request.Problem, request.Priority, request.Status, request.RequestedAt, request.RequestedBy, request.ReviewedAt, request.ReviewedBy,
                request.ReviewNote, request.CompletedAt, request.CompletedBy, request.Result, request.EquipmentCostId, request.CancelledAt,
                request.CancelledBy, request.CancelReason, request.RowVersion));

    public static async Task<RepairRequestView?> LoadAsync(FleetDbContext db, IUserDirectory users, int projectId, int requestId, CancellationToken cancellationToken)
    {
        var row = await Project(Of(db, projectId).Where(request => request.Id == requestId)).SingleOrDefaultAsync(cancellationToken);
        return row is null ? null : (await RepairRequestRow.ViewsAsync([row], users, cancellationToken))[0];
    }
}

/// <summary>
/// <c>POST /api/projects/{pid}/repair-requests</c>: ask for a machine on this project to be repaired. Allowed to the
/// machine's responsible engineer on the project, and to anyone with equipment Create or Edit. The machine must be
/// on the project now. Saved as <c>Open</c>, waiting for someone with equipment Approve.
/// </summary>
internal static class CreateRepairRequest
{
    internal sealed record Request(int? EquipmentId, string? Problem, string? Priority);

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/projects/{projectId:int}/repair-requests", HandleAsync)
            .RequirePermission(FleetPermissions.Module, PermissionAction.View)
            .WithName("CreateRepairRequest")
            .WithSummary("Ask for a repair of a machine on this project (its responsible engineer, or equipment Create/Edit). 409 never_on_project");

    internal static async Task<Results<Created<RepairRequestView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId,
        Request request,
        HttpContext httpContext,
        FleetDbContext db,
        IProjectAccess access,
        IUserDirectory users,
        CompanyClock clock,
        FleetLog log,
        CancellationToken cancellationToken)
    {
        if (FleetCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        if (request.EquipmentId is not > 0)
        {
            errors.Add("equipmentId", "equipmentId is required.");
        }

        var problem = Input.RequiredText(request.Problem, RepairRequestConfiguration.ProblemMaxLength, errors, "problem", "The problem");
        var priority = Input.OptionalName<RepairPriority>(request.Priority, errors, "priority") ?? RepairPriority.Normal;
        if (errors.HasErrors)
        {
            return FleetProblems.Validation(errors);
        }

        var project = await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken);
        if (project is null)
        {
            return FleetProblems.ProjectNotFound();
        }

        if (project.WritesBlocked)
        {
            return FleetProblems.CompletedProject();
        }

        if (!await db.Equipment.AnyAsync(equipment => equipment.Id == request.EquipmentId, cancellationToken))
        {
            return FleetProblems.EquipmentNotFound();
        }

        var today = clock.Today;
        var stay = await db.EquipmentAssignments.AsNoTracking()
            .Where(assignment => assignment.ProjectId == projectId && assignment.EquipmentId == request.EquipmentId
                && assignment.StartDate <= today && (assignment.EndDate == null || assignment.EndDate >= today))
            .Select(assignment => new { assignment.ResponsibleUserId })
            .FirstOrDefaultAsync(cancellationToken);
        if (stay is null)
        {
            return FleetProblems.NotOnProject();
        }

        if (!caller.CanRecordCosts && stay.ResponsibleUserId != caller.UserId)
        {
            log.ActionRefused(caller.UserId, nameof(CreateRepairRequest), "not the machine's responsible engineer and no equipment Create or Edit");
            return FleetProblems.NotTheResponsible();
        }

        var repair = new RepairRequest
        {
            EquipmentId = request.EquipmentId!.Value,
            ProjectId = projectId,
            Problem = problem!,
            Priority = priority,
            Status = RepairRequestStatus.Open,
            RequestedAt = clock.UtcNow,
            RequestedBy = caller.UserId,
        };
        db.RepairRequests.Add(repair);
        await db.SaveChangesAsync(cancellationToken);
        log.RepairRequested(caller.UserId, projectId, repair.EquipmentId, repair.Id);

        var view = await RepairRequestQueries.LoadAsync(db, users, projectId, repair.Id, cancellationToken);
        return TypedResults.Created($"/api/projects/{projectId}/repair-requests/{repair.Id}", view!);
    }
}

/// <summary><c>GET /api/projects/{pid}/repair-requests</c> and <c>/{id}</c>: the project's repair requests, newest first.</summary>
internal static class ListRepairRequests
{
    internal sealed record Response(IReadOnlyList<RepairRequestView> Items, int Page, int PageSize, int TotalCount);

    public static void Map(IEndpointRouteBuilder api)
    {
        api.MapGet("/projects/{projectId:int}/repair-requests", HandleAsync)
            .RequirePermission(FleetPermissions.Module, PermissionAction.View)
            .WithName("ListRepairRequests")
            .WithSummary("List a project's repair requests, newest first (status; equipmentId)");

        api.MapGet("/projects/{projectId:int}/repair-requests/{requestId:int}", GetAsync)
            .RequirePermission(FleetPermissions.Module, PermissionAction.View)
            .WithName("GetRepairRequest")
            .WithSummary("Get one repair request");
    }

    internal static async Task<Results<Ok<Response>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId,
        HttpContext httpContext,
        FleetDbContext db,
        IProjectAccess access,
        IUserDirectory users,
        CancellationToken cancellationToken,
        string? status = null,
        int? equipmentId = null,
        int page = 1,
        int pageSize = Paging.DefaultPageSize)
    {
        var errors = new ValidationErrors();
        Paging.Validate(page, pageSize, errors);
        var statusFilter = Input.OptionalName<RepairRequestStatus>(status, errors, "status");
        if (errors.HasErrors)
        {
            return FleetProblems.Validation(errors);
        }

        if (await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken) is null)
        {
            return FleetProblems.ProjectNotFound();
        }

        var query = RepairRequestQueries.Of(db, projectId);
        if (statusFilter is { } wanted)
        {
            query = query.Where(request => request.Status == wanted);
        }

        if (equipmentId is { } machine)
        {
            query = query.Where(request => request.EquipmentId == machine);
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var rows = await RepairRequestQueries.Project(query.OrderByDescending(request => request.RequestedAt).ThenByDescending(request => request.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)).ToListAsync(cancellationToken);

        return TypedResults.Ok(new Response(await RepairRequestRow.ViewsAsync(rows, users, cancellationToken), page, pageSize, totalCount));
    }

    internal static async Task<Results<Ok<RepairRequestView>, ProblemHttpResult>> GetAsync(
        int projectId, int requestId, HttpContext httpContext, FleetDbContext db, IProjectAccess access, IUserDirectory users, CancellationToken cancellationToken)
    {
        if (await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken) is null)
        {
            return FleetProblems.ProjectNotFound();
        }

        var view = await RepairRequestQueries.LoadAsync(db, users, projectId, requestId, cancellationToken);
        return view is null ? FleetProblems.RepairRequestNotFound() : TypedResults.Ok(view);
    }
}

/// <summary>
/// The moves of a repair request: <c>approve</c> / <c>reject</c> (equipment Approve, never the requester; a reason to
/// reject), <c>complete</c> (equipment Edit, with the result and optionally the repair's equipment cost) and
/// <c>cancel</c> (the requester or equipment Edit, with a reason). None of them records or pays a cost.
/// </summary>
internal static class MoveRepairRequest
{
    internal sealed record Request(string? Note, string? Result, int? EquipmentCostId, string? RowVersion);

    private enum Move
    {
        Approve,
        Reject,
        Complete,
        Cancel,
    }

    public static void Map(IEndpointRouteBuilder api)
    {
        Route(api, "approve", Move.Approve, PermissionAction.Approve, "Approve a repair request (equipment Approve, never your own). 409 own_record, invalid_status");
        Route(api, "reject", Move.Reject, PermissionAction.Approve, "Reject a repair request with a reason (equipment Approve, never your own)");
        Route(api, "complete", Move.Complete, PermissionAction.Edit, "Record that an approved repair is done, with its result and optionally its equipment cost");
        Route(api, "cancel", Move.Cancel, PermissionAction.View, "Cancel an open or approved repair request with a reason (the requester or equipment Edit)");
    }

    private static void Route(IEndpointRouteBuilder api, string path, Move move, PermissionAction needs, string summary) =>
        api.MapPost($"/projects/{{projectId:int}}/repair-requests/{{requestId:int}}/{path}",
                (int projectId, int requestId, Request request, HttpContext httpContext, FleetDbContext db, IProjectAccess access, IUserDirectory users,
                    CompanyClock clock, FleetLog log, CancellationToken cancellationToken) =>
                    HandleAsync(move, projectId, requestId, request, httpContext, db, access, users, clock, log, cancellationToken))
            .RequirePermission(FleetPermissions.Module, needs)
            .WithName($"{move}RepairRequest")
            .WithSummary(summary);

    private static async Task<Results<Ok<RepairRequestView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        Move move,
        int projectId,
        int requestId,
        Request request,
        HttpContext httpContext,
        FleetDbContext db,
        IProjectAccess access,
        IUserDirectory users,
        CompanyClock clock,
        FleetLog log,
        CancellationToken cancellationToken)
    {
        if (FleetCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var note = move is Move.Reject or Move.Cancel
            ? Input.RequiredText(request.Note, RepairRequestConfiguration.NoteMaxLength, errors, "note", "The reason")
            : Input.OptionalText(request.Note, RepairRequestConfiguration.NoteMaxLength, errors, "note", "The note");
        var result = move == Move.Complete
            ? Input.RequiredText(request.Result, RepairRequestConfiguration.ProblemMaxLength, errors, "result", "The result")
            : null;
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (errors.HasErrors)
        {
            return FleetProblems.Validation(errors);
        }

        var project = await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken);
        if (project is null)
        {
            return FleetProblems.ProjectNotFound();
        }

        if (project.WritesBlocked)
        {
            return FleetProblems.CompletedProject();
        }

        var repair = await db.RepairRequests.SingleOrDefaultAsync(candidate => candidate.Id == requestId && candidate.ProjectId == projectId, cancellationToken);
        if (repair is null)
        {
            return FleetProblems.RepairRequestNotFound();
        }

        if (move is Move.Approve or Move.Reject && repair.RequestedBy == caller.UserId)
        {
            log.ActionRefused(caller.UserId, $"{move}RepairRequest", "deciding own request");
            return FleetProblems.ReviewerIsRecorder();
        }

        if (move == Move.Cancel && repair.RequestedBy != caller.UserId && !caller.Can(PermissionAction.Edit))
        {
            return ApiProblem.ForbiddenResult("Only the requester or someone with equipment Edit can cancel a repair request.");
        }

        var allowed = move switch
        {
            Move.Approve or Move.Reject => repair.Status == RepairRequestStatus.Open,
            Move.Complete => repair.Status == RepairRequestStatus.Approved,
            _ => repair.Status is RepairRequestStatus.Open or RepairRequestStatus.Approved,
        };
        if (!allowed)
        {
            return FleetProblems.WrongStatus($"A {repair.Status} repair request can't be moved this way.");
        }

        if (move == Move.Complete && request.EquipmentCostId is { } costId)
        {
            // The repair's cost is an ordinary cost of the same machine on this project, of a repair kind.
            var fits = await db.EquipmentCosts.AsNoTracking().AnyAsync(
                cost => cost.Id == costId && cost.ProjectId == projectId && cost.EquipmentId == repair.EquipmentId
                    && cost.Status != EquipmentCostStatus.Rejected
                    && (cost.Type == EquipmentCostType.Repair || cost.Type == EquipmentCostType.Maintenance || cost.Type == EquipmentCostType.SpareParts),
                cancellationToken);
            if (!fits)
            {
                errors.Add("equipmentCostId", "The cost must be a repair, maintenance or spare-parts cost of this machine on this project.");
                return FleetProblems.Validation(errors);
            }
        }

        var now = clock.UtcNow;
        switch (move)
        {
            case Move.Approve:
                repair.Review(RepairRequestStatus.Approved, caller.UserId, now, note);
                break;
            case Move.Reject:
                repair.Review(RepairRequestStatus.Rejected, caller.UserId, now, note);
                break;
            case Move.Complete:
                repair.Complete(caller.UserId, now, result!, request.EquipmentCostId);
                break;
            default:
                repair.Cancel(caller.UserId, now, note!);
                break;
        }

        db.Entry(repair).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        log.RepairRequestMoved(caller.UserId, projectId, repair.Id, repair.Status.ToString());
        return TypedResults.Ok((await RepairRequestQueries.LoadAsync(db, users, projectId, repair.Id, cancellationToken))!);
    }
}
