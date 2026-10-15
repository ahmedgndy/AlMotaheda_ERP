using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Data.Configurations;
using AlMotaheda.Modules.Projects.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Features.ReportRequests;

/// <summary>
/// The body of every move. <c>responseText</c> is for submit; <c>note</c> is required to return or cancel.
/// </summary>
internal sealed record ReportRequestMoveRequest(string? ResponseText, string? Note, string? RowVersion);

/// <summary><c>POST /api/projects/{pid}/report-requests/{id}/start</c>: the recipient starts working on it.</summary>
internal static class StartReportRequest
{
    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapPost("/{projectId:int}/report-requests/{requestId:int}/start", HandleAsync)
            .RequirePermission(ReportPermissions.Module, PermissionAction.View)
            .WithName("StartReportRequest")
            .WithSummary("Start working on a report request sent to you");

    internal static Task<Results<Ok<ReportRequestView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId, int requestId, ReportRequestMoveRequest request, HttpContext httpContext, ProjectsDbContext db, IProjectAccess access,
        IUserDirectory users, CompanyClock clock, ProjectsLog log, CancellationToken cancellationToken) =>
        ReportRequestMove.HandleAsync(RequestAction.Start, projectId, requestId, request, httpContext, db, access, users, clock, log, cancellationToken);
}

/// <summary><c>POST /api/projects/{pid}/report-requests/{id}/submit</c>: the recipient sends the answer (again after a return).</summary>
internal static class SubmitReportRequest
{
    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapPost("/{projectId:int}/report-requests/{requestId:int}/submit", HandleAsync)
            .RequirePermission(ReportPermissions.Module, PermissionAction.View)
            .WithName("SubmitReportRequest")
            .WithSummary("Send the answer to a report request sent to you (responseText is required)");

    internal static Task<Results<Ok<ReportRequestView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId, int requestId, ReportRequestMoveRequest request, HttpContext httpContext, ProjectsDbContext db, IProjectAccess access,
        IUserDirectory users, CompanyClock clock, ProjectsLog log, CancellationToken cancellationToken) =>
        ReportRequestMove.HandleAsync(RequestAction.Submit, projectId, requestId, request, httpContext, db, access, users, clock, log, cancellationToken);
}

/// <summary><c>POST /api/projects/{pid}/report-requests/{id}/complete</c>: the requester accepts the answer. Final.</summary>
internal static class CompleteReportRequest
{
    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapPost("/{projectId:int}/report-requests/{requestId:int}/complete", HandleAsync)
            .RequirePermission(ReportPermissions.Module, PermissionAction.Approve)
            .WithName("CompleteReportRequest")
            .WithSummary("Accept the answer to your report request (or any, as an Executive)");

    internal static Task<Results<Ok<ReportRequestView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId, int requestId, ReportRequestMoveRequest request, HttpContext httpContext, ProjectsDbContext db, IProjectAccess access,
        IUserDirectory users, CompanyClock clock, ProjectsLog log, CancellationToken cancellationToken) =>
        ReportRequestMove.HandleAsync(RequestAction.Complete, projectId, requestId, request, httpContext, db, access, users, clock, log, cancellationToken);
}

/// <summary><c>POST /api/projects/{pid}/report-requests/{id}/return</c>: the requester sends the answer back, with a note.</summary>
internal static class ReturnReportRequest
{
    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapPost("/{projectId:int}/report-requests/{requestId:int}/return", HandleAsync)
            .RequirePermission(ReportPermissions.Module, PermissionAction.Approve)
            .WithName("ReturnReportRequest")
            .WithSummary("Send the answer back for correction, with a note (the requester, or an Executive)");

    internal static Task<Results<Ok<ReportRequestView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId, int requestId, ReportRequestMoveRequest request, HttpContext httpContext, ProjectsDbContext db, IProjectAccess access,
        IUserDirectory users, CompanyClock clock, ProjectsLog log, CancellationToken cancellationToken) =>
        ReportRequestMove.HandleAsync(RequestAction.Return, projectId, requestId, request, httpContext, db, access, users, clock, log, cancellationToken);
}

/// <summary><c>POST /api/projects/{pid}/report-requests/{id}/cancel</c>: the requester withdraws an open request, with a note.</summary>
internal static class CancelReportRequest
{
    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapPost("/{projectId:int}/report-requests/{requestId:int}/cancel", HandleAsync)
            .RequirePermission(ReportPermissions.Module, PermissionAction.View)
            .WithName("CancelReportRequest")
            .WithSummary("Withdraw your open report request, with a note");

    internal static Task<Results<Ok<ReportRequestView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId, int requestId, ReportRequestMoveRequest request, HttpContext httpContext, ProjectsDbContext db, IProjectAccess access,
        IUserDirectory users, CompanyClock clock, ProjectsLog log, CancellationToken cancellationToken) =>
        ReportRequestMove.HandleAsync(RequestAction.Cancel, projectId, requestId, request, httpContext, db, access, users, clock, log, cancellationToken);
}

/// <summary>
/// The one flow behind every move: validate the body, find the project and the request as the caller may see them
/// (404), check who may make the move (403) and from which status (409, <see cref="ReportRequestFlow"/>), then save
/// with the row version, so two moves can't both land.
/// </summary>
internal static class ReportRequestMove
{
    public static async Task<Results<Ok<ReportRequestView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        RequestAction action,
        int projectId,
        int requestId,
        ReportRequestMoveRequest request,
        HttpContext httpContext,
        ProjectsDbContext db,
        IProjectAccess access,
        IUserDirectory users,
        CompanyClock clock,
        ProjectsLog log,
        CancellationToken cancellationToken)
    {
        if (ProjectCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var responseText = action == RequestAction.Submit
            ? Input.RequiredText(request.ResponseText, ReportRequestConfiguration.ResponseMaxLength, errors, "responseText", "The answer")
            : null;
        var note = action is RequestAction.Return or RequestAction.Cancel
            ? Input.RequiredText(request.Note, ReportRequestConfiguration.NoteMaxLength, errors, "note", "The note")
            : null;
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        var project = await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken);
        if (project is null)
        {
            return ProjectProblems.ProjectNotFound();
        }

        var viewer = ReportRequestView.Viewer(caller);
        var row = await db.ReportRequests
            .Where(candidate => candidate.Id == requestId && candidate.ProjectId == projectId)
            .Where(candidate => viewer.SeesEveryRequest || candidate.RequesterUserId == caller.UserId || candidate.RecipientUserId == caller.UserId)
            .SingleOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return PlanningProblems.ReportRequestNotFound();
        }

        switch (ReportRequestFlow.Decide(action, row.Status, row.RequesterUserId, row.RecipientUserId, caller.UserId, caller.IsExecutive))
        {
            case RequestDecision.NotRecipient:
                log.ActionRefused(caller.UserId, $"ReportRequest{action}", "not the recipient");
                return PlanningProblems.OnlyTheRecipient();
            case RequestDecision.NotRequester:
                log.ActionRefused(caller.UserId, $"ReportRequest{action}", "not the requester");
                return PlanningProblems.OnlyTheRequester();
            case RequestDecision.NotRequesterOrExecutive:
                log.ActionRefused(caller.UserId, $"ReportRequest{action}", "not the requester or an Executive");
                return PlanningProblems.OnlyTheRequesterOrExecutive();
        }

        if (project.WritesBlocked)
        {
            return ProjectProblems.CompletedProject();
        }

        if (ReportRequestFlow.Decide(action, row.Status, row.RequesterUserId, row.RecipientUserId, caller.UserId, caller.IsExecutive)
            == RequestDecision.WrongStatus)
        {
            return WorkspaceProblems.WrongStatus($"This report request is {row.Status}, so it can't be moved that way.");
        }

        var now = clock.UtcNow;
        var oldStatus = row.Status;
        row.Status = ReportRequestFlow.Target(action);
        switch (action)
        {
            case RequestAction.Start:
                row.StartedAt = now;
                break;
            case RequestAction.Submit:
                row.ResponseText = responseText;
                row.SubmittedAt = now;
                row.StartedAt ??= now;
                break;
            case RequestAction.Complete:
                row.ReviewedAt = now;
                row.ReviewedBy = caller.UserId;
                break;
            case RequestAction.Return:
                row.ReviewedAt = now;
                row.ReviewedBy = caller.UserId;
                row.ReturnNote = note;
                break;
            case RequestAction.Cancel:
                row.CancelledAt = now;
                row.CancelNote = note;
                break;
        }

        db.Entry(row).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        log.ReportRequestMoved(caller.UserId, projectId, row.Id, oldStatus.ToString(), row.Status.ToString());

        var view = await ReportRequestView.LoadAsync(db, users, caller, projectId, row.Id, clock.Today, cancellationToken);
        return view is null ? PlanningProblems.ReportRequestNotFound() : TypedResults.Ok(view);
    }
}
