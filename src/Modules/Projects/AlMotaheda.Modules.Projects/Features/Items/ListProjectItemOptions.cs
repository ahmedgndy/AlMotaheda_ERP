using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Features.Items;

/// <summary>
/// <c>GET /api/projects/{pid}/items/options</c>: the project's items as a plain list (names, category, unit, status)
/// with no money, for choosing an item on a form.
/// </summary>
/// <remarks>
/// The items table (<c>GET …/items</c>) needs projects See money because every row carries cost, paid and
/// outstanding. A site engineer records supplies, daily reports and labor without See money and still has to name the
/// item the work belongs to; this list gives exactly that and nothing else. projects View and project scope apply.
/// </remarks>
internal static class ListProjectItemOptions
{
    /// <param name="ParentItemId">The main item of a sub-item; <c>null</c> for a main item. A picker shows "main › sub".</param>
    internal sealed record ItemOption(int Id, string NameAr, string NameEn, string Category, string Unit, string Status, int? ParentItemId);

    internal sealed record Response(IReadOnlyList<ItemOption> Items);

    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapGet("/{projectId:int}/items/options", HandleAsync)
            .RequirePermission(ProjectPermissions.Module, PermissionAction.View)
            .WithName("ListProjectItemOptions")
            .WithSummary("The project items without money, for pickers (search: names; includeInactive)");

    internal static async Task<Results<Ok<Response>, ProblemHttpResult>> HandleAsync(
        int projectId,
        HttpContext httpContext,
        IProjectAccess access,
        ProjectsDbContext db,
        CancellationToken cancellationToken,
        string? search = null,
        bool includeInactive = false)
    {
        if (ProjectCaller.From(httpContext.User) is null)
        {
            return ApiProblem.ForbiddenResult();
        }

        if (await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken) is null)
        {
            return ProjectProblems.ProjectNotFound();
        }

        var text = Input.Clean(search);
        var query = db.ProjectItems.AsNoTracking().Where(item => item.ProjectId == projectId);
        if (!includeInactive)
        {
            query = query.Where(item => item.Status == ProjectItemStatus.Active);
        }

        if (text is not null)
        {
            query = query.Where(item => item.NameAr.Contains(text) || item.NameEn.Contains(text));
        }

        var rows = await query
            .OrderBy(item => item.NameEn)
            .Select(item => new { item.Id, item.NameAr, item.NameEn, item.Category, item.Unit, item.Status, item.ParentItemId })
            .ToListAsync(cancellationToken);

        // Each main item followed by its sub-items, like the items table.
        var byParent = rows.Where(item => item.ParentItemId is not null).ToLookup(item => item.ParentItemId!.Value);
        var listed = rows.Select(item => item.Id).ToHashSet();
        var ordered = rows.Where(item => item.ParentItemId is null)
            .SelectMany(main => new[] { main }.Concat(byParent[main.Id]))
            .Concat(rows.Where(item => item.ParentItemId is { } parent && !listed.Contains(parent)));

        return TypedResults.Ok(new Response(ordered
            .Select(item => new ItemOption(item.Id, item.NameAr, item.NameEn, item.Category, item.Unit, item.Status.ToString(), item.ParentItemId))
            .ToList()));
    }
}
