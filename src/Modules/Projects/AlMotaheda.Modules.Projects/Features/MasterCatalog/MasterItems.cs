using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Data.Configurations;
using AlMotaheda.Modules.Projects.Features.Catalog;
using AlMotaheda.Modules.Projects.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Features.MasterCatalog;

/// <summary>
/// <c>GET /api/catalog/items</c>: the company's master catalog (management area, never a project's overview), with
/// where each controlled price stands today.
/// </summary>
internal static class ListMasterItems
{
    internal sealed record Response(IReadOnlyList<MasterItemView> Items, int Page, int PageSize, int TotalCount);

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/catalog/items", HandleAsync)
            .RequirePermission(CatalogPermissions.Module, PermissionAction.View)
            .WithName("ListMasterItems")
            .WithSummary("List the master catalog (search: names; kind; category; status) with each item's controlled price");

    internal static async Task<Results<Ok<Response>, ValidationProblem>> HandleAsync(
        ProjectsDbContext db,
        IUserDirectory users,
        CompanyClock clock,
        CancellationToken cancellationToken,
        string? search = null,
        string? kind = null,
        string? category = null,
        string? status = null,
        int page = 1,
        int pageSize = Paging.DefaultPageSize)
    {
        var errors = new ValidationErrors();
        Paging.Validate(page, pageSize, errors);
        var kindFilter = Input.OptionalName<MasterItemKind>(kind, errors, "kind");
        var statusFilter = Input.OptionalName<MasterItemStatus>(status, errors, "status");
        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        var query = MasterQueries.Filtered(db, Input.Clean(search), kindFilter, Input.Clean(category), statusFilter);
        var totalCount = await query.CountAsync(cancellationToken);
        var headers = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);

        var ids = headers.Select(header => header.Id).ToList();
        var prices = await MasterQueries.Prices(db, ids).ToListAsync(cancellationToken);
        var people = await PlanningPeople.FindAsync(users, prices.Where(price => price.Status == ItemPriceStatus.Pending).Select(price => price.RequestedBy), cancellationToken);
        var byItem = prices.ToLookup(price => price.CatalogItemId);

        return TypedResults.Ok(new Response(
            headers.Select(header => MasterItemView.From(header, byItem[header.Id], clock.Today, people)).ToList(),
            page,
            pageSize,
            totalCount));
    }
}

/// <summary><c>GET /api/catalog/items/{id}</c>: one master item with its whole price history, newest first.</summary>
internal static class GetMasterItem
{
    internal sealed record Response(MasterItemView Item, IReadOnlyList<MasterPriceChangeView> PriceHistory);

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/catalog/items/{masterItemId:int}", HandleAsync)
            .RequirePermission(CatalogPermissions.Module, PermissionAction.View)
            .WithName("GetMasterItem")
            .WithSummary("Get a master catalog item with its price history");

    internal static async Task<Results<Ok<Response>, ProblemHttpResult>> HandleAsync(
        int masterItemId, ProjectsDbContext db, IUserDirectory users, CompanyClock clock, CancellationToken cancellationToken)
    {
        var response = await LoadAsync(db, users, masterItemId, clock.Today, cancellationToken);
        return response is null ? CostFlowProblems.MasterItemNotFound() : TypedResults.Ok(response);
    }

    internal static async Task<Response?> LoadAsync(ProjectsDbContext db, IUserDirectory users, int masterItemId, DateOnly today, CancellationToken cancellationToken)
    {
        var header = await MasterQueries.One(db, masterItemId).SingleOrDefaultAsync(cancellationToken);
        if (header is null)
        {
            return null;
        }

        var prices = await MasterQueries.Prices(db, [masterItemId]).ToListAsync(cancellationToken);
        var people = await PlanningPeople.FindAsync(users, PriceChangeView.People(prices), cancellationToken);
        var history = prices
            .OrderByDescending(price => price.EffectiveFrom)
            .ThenByDescending(price => price.Id)
            .Select(price => MasterPriceChangeView.From(price, header.NameAr, header.NameEn, people))
            .ToList();

        return new Response(MasterItemView.From(header, prices, today, people), history);
    }
}

/// <summary>
/// <c>POST /api/catalog/items</c>: add a definition to the master catalog (catalog Create). An optional first price
/// is approved at once only when the caller may decide prices (catalog Approve); otherwise it waits as a request,
/// so nobody without that right sets a controlled price.
/// </summary>
internal static class CreateMasterItem
{
    internal sealed record Request(
        string? NameAr,
        string? NameEn,
        string? Kind,
        string? Category,
        string? Unit,
        string? Description,
        decimal? Price,
        string? EffectiveFrom,
        string? Reason) : IMasterItemRequest;

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/catalog/items", HandleAsync)
            .RequirePermission(CatalogPermissions.Module, PermissionAction.Create)
            .WithName("CreateMasterItem")
            .WithSummary("Add a master catalog item, optionally with its first price (approved at once only for a price approver)");

    internal static async Task<Results<Created<GetMasterItem.Response>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        Request request,
        HttpContext httpContext,
        ProjectsDbContext db,
        IUserDirectory users,
        PermissionGate gate,
        CompanyClock clock,
        ProjectsLog log,
        CancellationToken cancellationToken)
    {
        if (ProjectCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var fields = MasterItemFields.Validate(request, errors);
        decimal? price = request.Price is null ? null : NumberInput.RequiredMoney(request.Price, errors, "price", "The price");
        var effectiveFrom = Input.OptionalDate(request.EffectiveFrom, errors, "effectiveFrom", "The effective date") ?? clock.Today;
        var reason = Input.OptionalText(request.Reason, ItemPriceConfiguration.ReasonMaxLength, errors, "reason", "The reason");
        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        if (await fields!.NameTakenAsync(db, exceptId: null, cancellationToken))
        {
            return CostFlowProblems.NameTaken();
        }

        // The token says the caller may decide prices; the database confirms it before the price counts.
        var approvesAtOnce = price is not null
            && httpContext.User.HasPermission(CatalogPermissions.Module, PermissionAction.Approve)
            && await gate.CheckAsync(httpContext.User, CatalogPermissions.Module, PermissionAction.Approve, nameof(CreateMasterItem), cancellationToken) is null;

        var now = clock.UtcNow;
        var item = new MasterItem
        {
            NameAr = fields.NameAr,
            NameEn = fields.NameEn,
            Kind = fields.Kind,
            Category = fields.Category,
            Unit = fields.Unit,
            Description = fields.Description,
            CreatedAt = now,
            CreatedBy = caller.UserId,
        };

        if (price is { } firstPrice)
        {
            var row = new MasterItemPrice
            {
                Price = firstPrice,
                EffectiveFrom = effectiveFrom,
                Status = approvesAtOnce ? ItemPriceStatus.Approved : ItemPriceStatus.Pending,
                Reason = reason ?? "First price",
                RequestedAt = now,
                RequestedBy = caller.UserId,
            };
            if (approvesAtOnce)
            {
                row.DecidedAt = now;
                row.DecidedBy = caller.UserId;
                row.DecisionNote = "First price, set by a price approver";
            }

            item.Prices.Add(row);
        }

        db.MasterItems.Add(item);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (MasterItemFields.IsNameRace(exception))
        {
            return CostFlowProblems.NameTaken();
        }

        log.MasterItemSaved(caller.UserId, item.Id, "Created");
        var response = await GetMasterItem.LoadAsync(db, users, item.Id, clock.Today, cancellationToken);
        return TypedResults.Created($"/api/catalog/items/{item.Id}", response!);
    }
}

/// <summary>
/// <c>PUT /api/catalog/items/{id}</c>: edit a definition (catalog Create, like the project catalog). Its kind and unit
/// are fixed once a project selected it; projects keep the names they copied when they selected it.
/// </summary>
internal static class UpdateMasterItem
{
    internal sealed record Request(
        string? NameAr,
        string? NameEn,
        string? Kind,
        string? Category,
        string? Unit,
        string? Description,
        string? Status,
        string? RowVersion) : IMasterItemRequest;

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPut("/catalog/items/{masterItemId:int}", HandleAsync)
            .RequirePermission(CatalogPermissions.Module, PermissionAction.Create)
            .WithName("UpdateMasterItem")
            .WithSummary("Edit a master catalog item (kind and unit fixed once a project uses it). 409 master_item_exists, master_item_in_use");

    internal static async Task<Results<Ok<GetMasterItem.Response>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int masterItemId,
        Request request,
        HttpContext httpContext,
        ProjectsDbContext db,
        IUserDirectory users,
        CompanyClock clock,
        ProjectsLog log,
        CancellationToken cancellationToken)
    {
        if (ProjectCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var fields = MasterItemFields.Validate(request, errors);
        var status = Input.OptionalName<MasterItemStatus>(request.Status, errors, "status");
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        var item = await db.MasterItems.SingleOrDefaultAsync(candidate => candidate.Id == masterItemId, cancellationToken);
        if (item is null)
        {
            return CostFlowProblems.MasterItemNotFound();
        }

        if ((fields!.Kind != item.Kind || fields.Unit != item.Unit) && await MasterQueries.InUseAsync(db, item.Id, cancellationToken))
        {
            return CostFlowProblems.InUse(fields.Kind != item.Kind ? "kind" : "unit");
        }

        if (await fields.NameTakenAsync(db, item.Id, cancellationToken))
        {
            return CostFlowProblems.NameTaken();
        }

        item.NameAr = fields.NameAr;
        item.NameEn = fields.NameEn;
        item.Kind = fields.Kind;
        item.Category = fields.Category;
        item.Unit = fields.Unit;
        item.Description = fields.Description;
        item.Status = status ?? item.Status;
        item.UpdatedAt = clock.UtcNow;
        item.UpdatedBy = caller.UserId;
        db.Entry(item).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }
        catch (DbUpdateException exception) when (MasterItemFields.IsNameRace(exception))
        {
            return CostFlowProblems.NameTaken();
        }

        log.MasterItemSaved(caller.UserId, item.Id, "Updated");
        return TypedResults.Ok((await GetMasterItem.LoadAsync(db, users, item.Id, clock.Today, cancellationToken))!);
    }
}
