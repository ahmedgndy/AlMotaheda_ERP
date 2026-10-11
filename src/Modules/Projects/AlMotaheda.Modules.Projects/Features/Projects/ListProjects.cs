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

namespace AlMotaheda.Modules.Projects.Features.Projects;

/// <summary>
/// <c>GET /api/projects</c>: one page of the projects the caller may see, for the project cards, the counters and
/// the project pickers on other screens.
/// </summary>
/// <remarks>
/// Executive and Accountant see every project; everyone else sees the projects they are on. The scope is applied in
/// the SQL query, never by filtering in memory.
/// </remarks>
internal static class ListProjects
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 100;
    public const int MaxPage = 100_000;

    internal sealed record Item(
        int Id,
        string Code,
        string NameAr,
        string NameEn,
        string Status,
        string? ClientNameAr,
        string ClientNameEn,
        string? LocationAr,
        string? LocationEn,
        DateOnly StartDate,
        DateOnly EndDate,
        PersonRef? Manager,
        int MemberCount);

    /// <param name="CountsByStatus">
    /// How many projects in the caller's scope have each status, with the same search and assignedTo filters
    /// but before the status filter. For the counters and the status filter.
    /// </param>
    internal sealed record Response(
        IReadOnlyList<Item> Items,
        int Page,
        int PageSize,
        int TotalCount,
        IReadOnlyDictionary<string, int> CountsByStatus);

    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapGet("/", HandleAsync)
            .RequirePermission(ProjectPermissions.Module, PermissionAction.View)
            .WithName("ListProjects")
            .WithSummary("List the projects you may see (search: code, name or client; status: repeat for several; "
                         + "assignedTo=me: only projects you are on; sort: code, name, startDate, endDate or status)");

    internal static async Task<Results<Ok<Response>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        HttpContext httpContext,
        ProjectsDbContext db,
        ProjectAccess access,
        IUserDirectory users,
        CancellationToken cancellationToken,
        string? search = null,
        string[]? status = null,
        string? assignedTo = null,
        string? sort = null,
        int page = 1,
        int pageSize = DefaultPageSize)
    {
        if (ProjectCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        if (page is < 1 or > MaxPage)
        {
            errors.Add("page", $"page must be between 1 and {MaxPage}.");
        }

        if (pageSize is < 1 or > MaxPageSize)
        {
            errors.Add("pageSize", $"pageSize must be between 1 and {MaxPageSize}.");
        }

        var statuses = new List<ProjectStatus>();
        foreach (var value in status ?? [])
        {
            if (Input.TryParseName<ProjectStatus>(value, out var parsed))
            {
                statuses.Add(parsed);
            }
            else
            {
                errors.Add("status", $"status must be one of: {string.Join(", ", Enum.GetNames<ProjectStatus>())}.");
                break;
            }
        }

        var onlyMine = false;
        if (Input.Clean(assignedTo) is { } assigned)
        {
            if (string.Equals(assigned, "me", StringComparison.OrdinalIgnoreCase))
            {
                onlyMine = true;
            }
            else
            {
                errors.Add("assignedTo", "assignedTo can only be \"me\".");
            }
        }

        sort = Input.Clean(sort)?.ToLowerInvariant() ?? "code";
        if (sort is not ("code" or "name" or "startdate" or "enddate" or "status"))
        {
            errors.Add("sort", "sort must be code, name, startDate, endDate or status.");
        }

        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        var query = access.VisibleProjects(caller).AsNoTracking();

        if (Input.Clean(search) is { } text)
        {
            // The database collation ignores case, so "mall" finds "BDR-MALL-011".
            query = query.Where(project =>
                project.Code.Contains(text)
                || project.NameEn.Contains(text)
                || project.NameAr.Contains(text)
                || project.ClientNameEn.Contains(text)
                || (project.ClientNameAr != null && project.ClientNameAr.Contains(text)));
        }

        if (onlyMine)
        {
            var me = caller.UserId;
            query = query.Where(project => project.Members.Any(member => member.UserId == me && member.EndDate == null));
        }

        var counts = await query
            .GroupBy(project => project.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);

        if (statuses.Count > 0)
        {
            query = query.Where(project => statuses.Contains(project.Status));
        }

        var totalCount = await query.CountAsync(cancellationToken);

        query = sort switch
        {
            "name" => query.OrderBy(project => project.NameEn).ThenBy(project => project.Id),
            "startdate" => query.OrderByDescending(project => project.StartDate).ThenBy(project => project.Id),
            "enddate" => query.OrderBy(project => project.EndDate).ThenBy(project => project.Id),
            "status" => query.OrderBy(project => project.Status).ThenBy(project => project.Code),
            _ => query.OrderBy(project => project.Code),
        };

        var rows = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(project => new
            {
                project.Id,
                project.Code,
                project.NameAr,
                project.NameEn,
                project.Status,
                project.ClientNameAr,
                project.ClientNameEn,
                project.LocationAr,
                project.LocationEn,
                project.StartDate,
                project.EndDate,
                ManagerUserId = project.Members
                    .Where(member => member.EndDate == null && member.Responsibility == MemberResponsibility.ProjectManager)
                    .Select(member => (int?)member.UserId)
                    .FirstOrDefault(),
                MemberCount = project.Members.Count(member => member.EndDate == null),
            })
            .ToListAsync(cancellationToken);

        // One query for every manager's name on the page, never one per card.
        var managerIds = rows.Where(row => row.ManagerUserId is not null).Select(row => row.ManagerUserId!.Value).ToList();
        var people = await users.FindAsync(managerIds, cancellationToken);

        var items = rows.Select(row => new Item(
                row.Id,
                row.Code,
                row.NameAr,
                row.NameEn,
                row.Status.ToString(),
                row.ClientNameAr,
                row.ClientNameEn,
                row.LocationAr,
                row.LocationEn,
                row.StartDate,
                row.EndDate,
                row.ManagerUserId is { } managerId ? PersonRef.From(managerId, people) : null,
                row.MemberCount))
            .ToList();

        var countsByStatus = Enum.GetValues<ProjectStatus>().ToDictionary(
            value => value.ToString(),
            value => counts.FirstOrDefault(count => count.Status == value)?.Count ?? 0);

        return TypedResults.Ok(new Response(items, page, pageSize, totalCount, countsByStatus));
    }
}
