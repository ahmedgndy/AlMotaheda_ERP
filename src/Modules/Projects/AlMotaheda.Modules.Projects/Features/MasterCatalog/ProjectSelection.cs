using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Data.Configurations;
using AlMotaheda.Modules.Projects.Features.Catalog;
using AlMotaheda.Modules.Projects.Features.Items;
using AlMotaheda.Modules.Projects.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Features.MasterCatalog;

/// <summary>One master item as a project sees it when choosing what it uses.</summary>
/// <param name="ProjectRecordId">The project item or catalog item made when it was selected; <c>null</c> when not selected.</param>
internal sealed record SelectionRow(
    int MasterItemId,
    string NameAr,
    string NameEn,
    string Kind,
    string Target,
    string Category,
    string Unit,
    bool Selected,
    int? ProjectRecordId);

/// <summary>
/// <c>GET /api/projects/{pid}/catalog-selection</c>: the active master items, each marked selected or not on this
/// project, for the "configure the project" screen.
/// </summary>
internal static class GetProjectSelection
{
    internal sealed record Response(IReadOnlyList<SelectionRow> Items, int Page, int PageSize, int TotalCount);

    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapGet("/{projectId:int}/catalog-selection", HandleAsync)
            .RequirePermission(CatalogPermissions.Module, PermissionAction.View)
            .WithName("GetProjectCatalogSelection")
            .WithSummary("List the active master catalog items, marked selected or not on this project (search; kind)");

    internal static async Task<Results<Ok<Response>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId,
        HttpContext httpContext,
        ProjectsDbContext db,
        IProjectAccess access,
        CancellationToken cancellationToken,
        string? search = null,
        string? kind = null,
        int page = 1,
        int pageSize = Paging.DefaultPageSize)
    {
        var errors = new ValidationErrors();
        Paging.Validate(page, pageSize, errors);
        var kindFilter = Input.OptionalName<MasterItemKind>(kind, errors, "kind");
        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        if (await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken) is null)
        {
            return ProjectProblems.ProjectNotFound();
        }

        var query = MasterQueries.Filtered(db, Input.Clean(search), kindFilter, category: null, MasterItemStatus.Active);
        var totalCount = await query.CountAsync(cancellationToken);
        var headers = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);

        var ids = headers.Select(header => header.Id).ToList();
        var links = await ProjectLinksAsync(db, projectId, ids, cancellationToken);

        return TypedResults.Ok(new Response(
            headers.Select(header => new SelectionRow(
                header.Id,
                header.NameAr,
                header.NameEn,
                header.Kind.ToString(),
                MasterItem.IsWork(header.Kind) ? MasterItemView.ProjectItemTarget : MasterItemView.CatalogItemTarget,
                header.Category,
                header.Unit,
                links.ContainsKey(header.Id),
                links.TryGetValue(header.Id, out var recordId) ? recordId : null)).ToList(),
            page,
            pageSize,
            totalCount));
    }

    /// <summary>Master item id → the project record it became, for the given master items.</summary>
    internal static async Task<Dictionary<int, int>> ProjectLinksAsync(
        ProjectsDbContext db, int projectId, IReadOnlyCollection<int> masterItemIds, CancellationToken cancellationToken)
    {
        var catalog = await db.CatalogItems.AsNoTracking()
            .Where(item => item.ProjectId == projectId && item.MasterItemId != null && masterItemIds.Contains(item.MasterItemId.Value))
            .Select(item => new { MasterItemId = item.MasterItemId!.Value, item.Id })
            .ToListAsync(cancellationToken);
        var items = await db.ProjectItems.AsNoTracking()
            .Where(item => item.ProjectId == projectId && item.MasterItemId != null && masterItemIds.Contains(item.MasterItemId.Value))
            .Select(item => new { MasterItemId = item.MasterItemId!.Value, item.Id })
            .ToListAsync(cancellationToken);

        return catalog.Concat(items).ToDictionary(link => link.MasterItemId, link => link.Id);
    }
}

/// <summary>
/// <c>POST /api/projects/{pid}/catalog-selection</c>: the project starts using some master items. A work or composite
/// item becomes a project item ("بند"); any other kind becomes a priced project catalog item. Each keeps a link to its
/// master definition, so the master catalog is never copied, and its reference price comes from the master until the
/// project has an approved price of its own. Selecting an item twice changes nothing.
/// </summary>
/// <remarks>Catalog Create, and only an Executive or the project's manager (the rule of project items).</remarks>
internal static class SelectMasterItems
{
    internal sealed record Request(IReadOnlyList<int>? MasterItemIds);

    internal sealed record Selected(int MasterItemId, string Target, int ProjectRecordId, string NameAr, string NameEn);

    internal sealed record Response(IReadOnlyList<Selected> Added, IReadOnlyList<int> AlreadySelected);

    private const int MaxPerRequest = 200;

    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapPost("/{projectId:int}/catalog-selection", HandleAsync)
            .RequirePermission(CatalogPermissions.Module, PermissionAction.Create)
            .WithName("SelectMasterItems")
            .WithSummary("Use master catalog items on this project (Executive or the project's manager). 409 item_name_taken, catalog_item_exists");

    internal static async Task<Results<Ok<Response>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId,
        Request request,
        HttpContext httpContext,
        ProjectsDbContext db,
        IProjectAccess access,
        CompanyClock clock,
        ProjectsLog log,
        CancellationToken cancellationToken)
    {
        if (ProjectCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var ids = request.MasterItemIds?.Distinct().ToList() ?? [];
        if (ids.Count is 0 or > MaxPerRequest || ids.Any(id => id < 1))
        {
            errors.Add("masterItemIds", $"Send between 1 and {MaxPerRequest} master item ids.");
        }

        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        var project = await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken);
        if (project is null)
        {
            return ProjectProblems.ProjectNotFound();
        }

        if (ItemRules.ManageProblem(project) is { } refusal)
        {
            log.ActionRefused(caller.UserId, nameof(SelectMasterItems), "not an Executive or the project's manager, or project completed");
            return refusal;
        }

        var masters = await db.MasterItems.AsNoTracking().Where(item => ids.Contains(item.Id)).ToListAsync(cancellationToken);
        foreach (var missing in ids.Where(id => masters.All(item => item.Id != id)))
        {
            errors.Add("masterItemIds", $"Master item {missing} was not found.");
        }

        foreach (var inactive in masters.Where(item => item.Status != MasterItemStatus.Active))
        {
            errors.Add("masterItemIds", $"Master item {inactive.Id} is inactive.");
        }

        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await ProjectLock.TryAcquireAsync(db, projectId, cancellationToken))
        {
            return ProjectProblems.TryAgainLater();
        }

        var existing = await GetProjectSelection.ProjectLinksAsync(db, projectId, ids, cancellationToken);
        var toAdd = masters.Where(item => !existing.ContainsKey(item.Id)).OrderBy(item => item.Id).ToList();

        var work = toAdd.Where(item => MasterItem.IsWork(item.Kind)).ToList();
        var priced = toAdd.Where(item => !MasterItem.IsWork(item.Kind)).ToList();

        // A project record of the same name, made on the project only, must be renamed first: never two "Cement".
        var workNames = work.SelectMany(item => new[] { item.NameAr, item.NameEn }).ToList();
        if (work.Count > 0 && await db.ProjectItems.AnyAsync(item => item.ProjectId == projectId && (workNames.Contains(item.NameAr) || workNames.Contains(item.NameEn)), cancellationToken))
        {
            return CoreProblems.ItemNameAlreadyUsed();
        }

        var pricedNames = priced.SelectMany(item => new[] { item.NameAr, item.NameEn }).ToList();
        if (priced.Count > 0 && await db.CatalogItems.AnyAsync(item => item.ProjectId == projectId && (pricedNames.Contains(item.NameAr) || pricedNames.Contains(item.NameEn)), cancellationToken))
        {
            return PlanningProblems.CatalogNameTaken();
        }

        var now = clock.UtcNow;
        var projectItems = work.Select(master => (master, record: new ProjectItem
        {
            ProjectId = projectId,
            NameAr = master.NameAr,
            NameEn = master.NameEn,
            Category = master.Category,
            Unit = master.Unit,
            Description = master.Description,
            MasterItemId = master.Id,
            CreatedAt = now,
            CreatedBy = caller.UserId,
        })).ToList();
        var catalogItems = priced.Select(master => (master, record: new CatalogItem
        {
            ProjectId = projectId,
            NameAr = master.NameAr,
            NameEn = master.NameEn,
            Category = Enum.Parse<CatalogCategory>(master.Category),
            Unit = master.Unit,
            MasterItemId = master.Id,
            CreatedAt = now,
            CreatedBy = caller.UserId,
        })).ToList();

        db.ProjectItems.AddRange(projectItems.Select(pair => pair.record));
        db.CatalogItems.AddRange(catalogItems.Select(pair => pair.record));

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (ItemRules.IsNameRace(exception) || CatalogItemFields.IsNameRace(exception))
        {
            return CoreProblems.ItemNameAlreadyUsed();
        }
        catch (DbUpdateException exception) when (
            SqlViolations.IsUnique(exception, ProjectItemConfiguration.MasterIndex) || SqlViolations.IsUnique(exception, CatalogItemConfiguration.MasterIndex))
        {
            return ProjectProblems.TryAgainLater();
        }

        await transaction.CommitAsync(cancellationToken);
        log.MasterItemsSelected(caller.UserId, projectId, catalogItems.Count, projectItems.Count);

        var added = projectItems
            .Select(pair => new Selected(pair.master.Id, MasterItemView.ProjectItemTarget, pair.record.Id, pair.record.NameAr, pair.record.NameEn))
            .Concat(catalogItems.Select(pair => new Selected(pair.master.Id, MasterItemView.CatalogItemTarget, pair.record.Id, pair.record.NameAr, pair.record.NameEn)))
            .OrderBy(selected => selected.MasterItemId)
            .ToList();

        return TypedResults.Ok(new Response(added, existing.Keys.Order().ToList()));
    }
}
