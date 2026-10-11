using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
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
/// <c>GET /api/projects/memberships?userId=</c>: the projects one user is on, for the user page in Accounts, the
/// engineer panel and "My projects".
/// </summary>
/// <remarks>
/// Needs projects View. Anyone may read their own list; reading someone else's also needs accounts View. Either
/// way it shows only projects the caller may open right now, so it never reveals a project the caller can't see
/// (for example one they were removed from).
/// </remarks>
internal static class ListUserProjects
{
    internal sealed record Item(
        int MemberId,
        int ProjectId,
        string Code,
        string NameAr,
        string NameEn,
        string ProjectStatus,
        string Responsibility,
        DateOnly StartDate,
        DateOnly? EndDate);

    internal sealed record Response(IReadOnlyList<Item> Items);

    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapGet("/memberships", HandleAsync)
            .RequirePermission(ProjectPermissions.Module, PermissionAction.View)
            .WithName("ListUserProjects")
            .WithSummary("List the projects a user is on, among those you may see (yourself, or anyone with accounts View)");

    internal static async Task<Results<Ok<Response>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        HttpContext httpContext,
        ProjectsDbContext db,
        ProjectAccess access,
        CancellationToken cancellationToken,
        int? userId = null,
        bool includeEnded = false)
    {
        if (ProjectCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        if (userId is null or < 1)
        {
            var errors = new ValidationErrors();
            errors.Add("userId", "userId is required.");
            return ProjectProblems.Validation(errors);
        }

        if (userId != caller.UserId && !caller.Principal.HasPermission(ProjectPermissions.AccountsModule, PermissionAction.View))
        {
            return ApiProblem.ForbiddenResult();
        }

        var projects = access.VisibleProjects(caller);

        var rows = await db.ProjectMembers
            .AsNoTracking()
            .Where(member => member.UserId == userId && (includeEnded || member.EndDate == null))
            .Join(projects, member => member.ProjectId, project => project.Id, (member, project) => new { member, project })
            .OrderBy(row => row.member.EndDate == null ? 0 : 1)
            .ThenBy(row => row.project.Code)
            .ThenByDescending(row => row.member.StartDate)
            .Select(row => new
            {
                MemberId = row.member.Id,
                ProjectId = row.project.Id,
                row.project.Code,
                row.project.NameAr,
                row.project.NameEn,
                row.project.Status,
                row.member.Responsibility,
                row.member.StartDate,
                row.member.EndDate,
            })
            .ToListAsync(cancellationToken);

        var items = rows
            .Select(row => new Item(
                row.MemberId,
                row.ProjectId,
                row.Code,
                row.NameAr,
                row.NameEn,
                row.Status.ToString(),
                row.Responsibility.ToString(),
                row.StartDate,
                row.EndDate))
            .ToList();

        return TypedResults.Ok(new Response(items));
    }
}
