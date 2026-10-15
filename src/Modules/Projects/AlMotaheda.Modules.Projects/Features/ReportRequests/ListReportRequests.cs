using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Features.ReportRequests;

/// <summary>
/// <c>GET /api/projects/{pid}/report-requests</c>: one page of the project's report requests the caller may see,
/// newest first. An Executive or a Manager sees all of them; anyone else only what they sent or received.
/// </summary>
internal static class ListReportRequests
{
    internal sealed record Response(IReadOnlyList<ReportRequestView> Items, int Page, int PageSize, int TotalCount);

    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapGet("/{projectId:int}/report-requests", HandleAsync)
            .RequirePermission(ReportPermissions.Module, PermissionAction.View)
            .WithName("ListReportRequests")
            .WithSummary("List the report requests of a project you may see, newest first (filters: status, mine=true)");

    internal static async Task<Results<Ok<Response>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId,
        HttpContext httpContext,
        ProjectsDbContext db,
        IProjectAccess access,
        IUserDirectory users,
        CompanyClock clock,
        CancellationToken cancellationToken,
        string? status = null,
        bool mine = false,
        int page = 1,
        int pageSize = Paging.DefaultPageSize)
    {
        if (ProjectCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        Paging.Validate(page, pageSize, errors);

        ReportRequestStatus? statusFilter = null;
        if (Input.Clean(status) is { } statusText)
        {
            if (Input.TryParseName<ReportRequestStatus>(statusText, out var parsed))
            {
                statusFilter = parsed;
            }
            else
            {
                errors.Add("status", $"status must be one of: {string.Join(", ", Enum.GetNames<ReportRequestStatus>())}.");
            }
        }

        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        if (await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken) is null)
        {
            return ProjectProblems.ProjectNotFound();
        }

        var query = ReportRequestQueries.Filtered(db, projectId, ReportRequestView.Viewer(caller), new ReportRequestFilter(statusFilter, mine));
        var totalCount = await query.CountAsync(cancellationToken);
        var rows = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        var people = await PlanningPeople.FindAsync(users, ReportRequestView.People(rows), cancellationToken);

        var mayApprove = httpContext.User.HasPermission(ReportPermissions.Module, PermissionAction.Approve);
        var items = rows.Select(row => ReportRequestView.From(row, caller, mayApprove, clock.Today, people, linked: null)).ToList();

        return TypedResults.Ok(new Response(items, page, pageSize, totalCount));
    }
}
