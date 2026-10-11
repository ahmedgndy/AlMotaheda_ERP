using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Features.Members;

/// <summary>
/// <c>GET /api/projects/{id}/members</c>: the project's team (manager, site engineers, team members), for the team
/// card and the team table. Past members too with <c>includeEnded=true</c>.
/// </summary>
internal static class ListProjectMembers
{
    internal sealed record Response(IReadOnlyList<MemberView> Items);

    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapGet("/{projectId:int}/members", HandleAsync)
            .RequirePermission(ProjectPermissions.Module, PermissionAction.View)
            .WithName("ListProjectMembers")
            .WithSummary("List a project's team: the manager first, then by name");

    internal static async Task<Results<Ok<Response>, ProblemHttpResult>> HandleAsync(
        int projectId,
        HttpContext httpContext,
        ProjectsDbContext db,
        ProjectAccess access,
        IUserDirectory users,
        CancellationToken cancellationToken,
        bool includeEnded = false)
    {
        if (ProjectCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        if (!await access.VisibleProjects(caller).AnyAsync(project => project.Id == projectId, cancellationToken))
        {
            return ProjectProblems.ProjectNotFound();
        }

        var members = await db.ProjectMembers
            .AsNoTracking()
            .Where(member => member.ProjectId == projectId && (includeEnded || member.EndDate == null))
            .ToListAsync(cancellationToken);

        var people = await users.FindAsync(members.Select(member => member.UserId).ToList(), cancellationToken);

        var items = members
            .Select(member => MemberView.From(member, people, showEndDetails: caller.IsExecutive))
            .OrderBy(view => view.EndDate is null ? 0 : 1)
            .ThenBy(view => view.Responsibility == nameof(MemberResponsibility.ProjectManager) ? 0 : 1)
            .ThenBy(view => view.NameEn, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(view => view.EndDate)
            .ThenBy(view => view.MemberId)
            .ToList();

        return TypedResults.Ok(new Response(items));
    }
}
