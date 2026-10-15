using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Archive.Common;
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
/// <c>GET /api/projects/{pid}/documents</c>: one page of a project's archive, newest first, with the count of each
/// category for the whole project (the filter chips).
/// </summary>
/// <remarks>
/// Filters: <c>category</c>, <c>kind</c>, <c>search</c> (title, description and file name), <c>from</c> and <c>to</c>
/// (upload date, at the company), <c>uploadedBy</c>, and <c>entityType</c> with <c>entityId</c> (the documents linked
/// to one record). Every filter, the order and the paging run in SQL.
/// </remarks>
internal static class ListDocuments
{
    internal sealed record Response(
        IReadOnlyList<DocumentView> Items, int Page, int PageSize, int TotalCount, IReadOnlyList<DocumentCategoryCountView> CategoryCounts);

    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapGet("/{projectId:int}/documents", HandleAsync)
            .RequirePermission(ArchiveCaller.Module, PermissionAction.View)
            .WithName("ListDocuments")
            .WithSummary("List a project's documents, newest first (filters: category, kind, search, from, to, uploadedBy, "
                         + "entityType with entityId), with the count of each category");

    internal static async Task<Results<Ok<Response>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId,
        HttpContext httpContext,
        ArchiveDbContext db,
        IProjectAccess access,
        IUserDirectory users,
        ArchiveCalendar calendar,
        CancellationToken cancellationToken,
        string? category = null,
        string? kind = null,
        string? search = null,
        string? from = null,
        string? to = null,
        int? uploadedBy = null,
        string? entityType = null,
        int? entityId = null,
        int page = 1,
        int pageSize = Paging.DefaultPageSize)
    {
        if (ArchiveCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        Paging.Validate(page, pageSize, errors);

        var categoryFilter = Choice.Optional<DocumentCategory>(category, errors, "category");
        var kindFilter = Choice.Optional<DocumentKind>(kind, errors, "kind");
        var entityTypeFilter = Choice.Optional<LinkedEntityType>(entityType, errors, "entityType");

        var fromDate = Input.OptionalDate(from, errors, "from", "from");
        var toDate = Input.OptionalDate(to, errors, "to", "to");
        if (fromDate is { } f && toDate is { } t && t < f)
        {
            errors.Add("to", "to must not be before from.");
        }

        if (uploadedBy is < 1)
        {
            errors.Add("uploadedBy", "uploadedBy is not valid.");
        }

        if (entityId is < 1)
        {
            errors.Add("entityId", "entityId is not valid.");
        }

        if ((Input.Clean(entityType) is null) != (entityId is null))
        {
            errors.Add(entityId is null ? "entityId" : "entityType", "entityType and entityId go together.");
        }

        var searchText = Input.Clean(search);
        if (searchText is { Length: > 200 })
        {
            errors.Add("search", "search must be at most 200 characters.");
        }

        if (errors.HasErrors)
        {
            return ArchiveProblems.Validation(errors);
        }

        var project = await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken);
        if (project is null)
        {
            return ArchiveProblems.ProjectNotFound();
        }

        var filter = new DocumentFilter(
            categoryFilter,
            kindFilter,
            searchText,
            fromDate is { } start ? calendar.StartOfDayUtc(start) : null,
            toDate is { } end ? calendar.StartOfDayUtc(end.AddDays(1)) : null,
            uploadedBy,
            entityTypeFilter,
            entityId);

        var totalCount = await DocumentQueries.Matching(db, projectId, filter).CountAsync(cancellationToken);
        var rows = await DocumentQueries.Filtered(db, projectId, filter)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        var items = await DocumentView.FromAsync(rows, users, caller, project.WritesBlocked, cancellationToken);

        var counts = await DocumentQueries.CategoryCounts(db, projectId).ToListAsync(cancellationToken);
        var categoryCounts = counts
            .OrderBy(count => count.Category)
            .Select(count => new DocumentCategoryCountView(count.Category.ToString(), count.Count))
            .ToList();

        return TypedResults.Ok(new Response(items, page, pageSize, totalCount, categoryCounts));
    }
}
