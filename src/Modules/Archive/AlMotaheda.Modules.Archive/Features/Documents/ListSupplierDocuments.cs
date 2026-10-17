using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Archive.Data;
using AlMotaheda.Modules.Archive.Security;
using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Archive.Features.Documents;

/// <summary>
/// <c>GET /api/suppliers/{id}/documents</c>: the documents linked to a supplier on the projects the caller may see
/// (docs/cost-flow §2.5). Each document is stored once, on its project; this list only reads the links, so a file is
/// never copied to show it in two places.
/// </summary>
internal static class ListSupplierDocuments
{
    internal sealed record Response(IReadOnlyList<DocumentView> Items, int Page, int PageSize, int TotalCount);

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/suppliers/{supplierId:int}/documents", HandleAsync)
            .RequirePermission(ArchiveCaller.Module, PermissionAction.View)
            .WithName("ListSupplierDocuments")
            .WithSummary("The documents linked to a supplier on the projects you can see, newest first");

    internal static async Task<Results<Ok<Response>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int supplierId,
        HttpContext httpContext,
        ArchiveDbContext db,
        IVisibleProjects visible,
        IProjectDirectory projects,
        IUserDirectory users,
        CancellationToken cancellationToken,
        int page = 1,
        int pageSize = Paging.DefaultPageSize)
    {
        if (ArchiveCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        Paging.Validate(page, pageSize, errors);
        if (errors.HasErrors)
        {
            return ApiProblem.Validation(errors.ToDictionary());
        }

        var scope = await visible.IdsAsync(httpContext.User, cancellationToken);
        var query = DocumentQueries.OfSupplier(db, supplierId, scope);
        var totalCount = await query.CountAsync(cancellationToken);
        var rows = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);

        // A completed project's documents are read-only for everyone except an Executive, like its own list.
        var completed = (await projects.FindAsync(rows.Select(row => row.ProjectId).Distinct().ToList(), cancellationToken))
            .Values.Where(project => project.IsCompleted).Select(project => project.Id).ToHashSet();
        var isExecutive = httpContext.User.GetRoleCode() == AccessRoles.Executive;

        var items = new List<DocumentView>();
        foreach (var group in rows.GroupBy(row => completed.Contains(row.ProjectId) && !isExecutive))
        {
            items.AddRange(await DocumentView.FromAsync(group.ToList(), users, caller, group.Key, cancellationToken));
        }

        var order = rows.Select(row => row.Id).ToList();
        return TypedResults.Ok(new Response(items.OrderBy(item => order.IndexOf(item.Id)).ToList(), page, pageSize, totalCount));
    }
}
