using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Workforce.Common;
using AlMotaheda.Modules.Workforce.Data;
using AlMotaheda.Modules.Workforce.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Workforce.Features.Assignments;

/// <summary><c>GET /api/projects/{pid}/workers</c>: the workers on a project now; with <c>includeEnded</c>, the history too.</summary>
internal static class ListProjectWorkers
{
    internal sealed record Response(IReadOnlyList<WorkerAssignmentView> Items, int Page, int PageSize, int TotalCount);

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/projects/{projectId:int}/workers", HandleAsync)
            .RequirePermission(WorkforcePermissions.Workers, PermissionAction.View)
            .WithName("ListProjectWorkers")
            .WithSummary("List the workers assigned to a project now (includeEnded for the history; paging)");

    internal static async Task<Results<Ok<Response>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId,
        HttpContext httpContext,
        WorkforceDbContext db,
        IProjectAccess access,
        CancellationToken cancellationToken,
        bool includeEnded = false,
        int page = 1,
        int pageSize = Paging.DefaultPageSize)
    {
        if (WorkforceCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        Paging.Validate(page, pageSize, errors);
        if (errors.HasErrors)
        {
            return WorkforceProblems.Validation(errors);
        }

        if (await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken) is null)
        {
            return WorkforceProblems.ProjectNotFound();
        }

        var query = AssignmentQueries.OfProject(db, projectId, includeEnded);
        var totalCount = await query.CountAsync(cancellationToken);
        var rows = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        var fullNationalId = caller.Can(PermissionAction.Edit);

        return TypedResults.Ok(new Response(
            rows.Select(row => WorkerAssignmentView.From(row, fullNationalId, caller.SeesMoney)).ToList(),
            page,
            pageSize,
            totalCount));
    }
}
