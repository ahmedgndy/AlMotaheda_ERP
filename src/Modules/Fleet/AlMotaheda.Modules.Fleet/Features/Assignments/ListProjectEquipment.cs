using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Fleet.Common;
using AlMotaheda.Modules.Fleet.Data;
using AlMotaheda.Modules.Fleet.Security;
using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Fleet.Features.Assignments;

/// <summary>
/// <c>GET /api/projects/{pid}/equipment</c>: the machines on a project now, or with <c>includeEnded=true</c> every
/// stay a machine had on it. Only this project's assignments are read.
/// </summary>
internal static class ListProjectEquipment
{
    internal sealed record Response(IReadOnlyList<EquipmentAssignmentView> Items, int Page, int PageSize, int TotalCount);

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/projects/{projectId:int}/equipment", HandleAsync)
            .RequirePermission(FleetPermissions.Module, PermissionAction.View)
            .WithName("ListProjectEquipment")
            .WithSummary("The machines on a project now (includeEnded=true adds the ended stays: the project's history)");

    internal static async Task<Results<Ok<Response>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId,
        HttpContext httpContext,
        FleetDbContext db,
        IProjectAccess access,
        IUserDirectory users,
        CancellationToken cancellationToken,
        bool includeEnded = false,
        int page = 1,
        int pageSize = Paging.DefaultPageSize)
    {
        if (FleetCaller.From(httpContext.User) is null)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        Paging.Validate(page, pageSize, errors);
        if (errors.HasErrors)
        {
            return FleetProblems.Validation(errors);
        }

        var project = await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken);
        if (project is null)
        {
            return FleetProblems.ProjectNotFound();
        }

        var query = AssignmentQueries.OfProject(db, projectId, includeEnded);
        var totalCount = await query.CountAsync(cancellationToken);
        var rows = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        var items = await EquipmentAssignmentView.FromAsync(rows, project, users, cancellationToken);

        return TypedResults.Ok(new Response(items, page, pageSize, totalCount));
    }
}
