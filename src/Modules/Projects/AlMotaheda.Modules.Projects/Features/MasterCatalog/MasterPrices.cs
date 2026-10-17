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
/// <c>POST /api/catalog/items/{id}/price-changes</c>: ask for a new controlled price (catalog Edit: the Accountant, a
/// Manager, an Executive). Nobody changes an approved price directly: until the request is approved the current
/// price stays, and supplies already recorded keep the price they copied.
/// </summary>
internal static class RequestMasterPriceChange
{
    /// <param name="EffectiveFrom">The first day the new price applies (<c>yyyy-MM-dd</c>); today when empty.</param>
    internal sealed record Request(decimal? Price, string? EffectiveFrom, string? Reason);

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/catalog/items/{masterItemId:int}/price-changes", HandleAsync)
            .RequirePermission(CatalogPermissions.Module, PermissionAction.Edit)
            .WithName("RequestMasterPriceChange")
            .WithSummary("Ask for a master price change with a reason (one pending request per item). 409 price_change_pending, same_price, price_date_taken");

    internal static async Task<Results<Created<MasterPriceChangeView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
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
        var price = NumberInput.RequiredMoney(request.Price, errors, "price", "The new price");
        var effectiveFrom = Input.OptionalDate(request.EffectiveFrom, errors, "effectiveFrom", "The effective date") ?? clock.Today;
        var reason = Input.RequiredText(request.Reason, ItemPriceConfiguration.ReasonMaxLength, errors, "reason", "The reason");
        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        var item = await db.MasterItems.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == masterItemId, cancellationToken);
        if (item is null)
        {
            return CostFlowProblems.MasterItemNotFound();
        }

        if (item.Status != MasterItemStatus.Active)
        {
            return CostFlowProblems.Inactive();
        }

        var prices = await MasterQueries.Prices(db, [masterItemId]).ToListAsync(cancellationToken);
        var position = CatalogPricing.Position(prices, clock.Today);

        if (position.Pending is not null)
        {
            return PlanningProblems.PendingChangeExists();
        }

        if (position.Current?.Price == price)
        {
            return PlanningProblems.PriceUnchanged();
        }

        if (prices.Any(row => row.Status == ItemPriceStatus.Approved && row.EffectiveFrom == effectiveFrom))
        {
            return PlanningProblems.PriceDateAlreadyUsed();
        }

        var change = new MasterItemPrice
        {
            MasterItemId = item.Id,
            Price = price!.Value,
            PreviousPrice = position.Current?.Price,
            EffectiveFrom = effectiveFrom,
            Status = ItemPriceStatus.Pending,
            Reason = reason,
            RequestedAt = clock.UtcNow,
            RequestedBy = caller.UserId,
        };
        db.MasterItemPrices.Add(change);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, MasterItemPriceConfiguration.PendingIndex))
        {
            return PlanningProblems.PendingChangeExists();
        }

        log.MasterPriceChangeRequested(caller.UserId, item.Id, change.Id);

        var people = await PlanningPeople.FindAsync(users, [caller.UserId], cancellationToken);
        return TypedResults.Created(
            $"/api/catalog/price-changes/{change.Id}",
            MasterPriceChangeView.From(MasterPriceChangeView.Row(change), item.NameAr, item.NameEn, people));
    }
}

/// <summary>
/// <c>GET /api/catalog/price-changes</c>: master price requests, newest first; with <c>status=Pending</c>, the
/// Financial Manager's approval queue. First prices are not requests and are not listed.
/// </summary>
internal static class ListMasterPriceChanges
{
    internal sealed record Response(IReadOnlyList<MasterPriceChangeView> Items, int Page, int PageSize, int TotalCount);

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/catalog/price-changes", HandleAsync)
            .RequirePermission(CatalogPermissions.Module, PermissionAction.View)
            .WithName("ListMasterPriceChanges")
            .WithSummary("List master price change requests, newest first (status: Pending, Approved or Rejected)");

    internal static async Task<Results<Ok<Response>, ValidationProblem>> HandleAsync(
        ProjectsDbContext db,
        IUserDirectory users,
        CancellationToken cancellationToken,
        string? status = null,
        int page = 1,
        int pageSize = Paging.DefaultPageSize)
    {
        var errors = new ValidationErrors();
        Paging.Validate(page, pageSize, errors);
        var statusFilter = Input.OptionalName<ItemPriceStatus>(status, errors, "status");
        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        // Everything except a first price that a price approver set when adding the item (approved by its own author).
        var query = db.MasterItemPrices.AsNoTracking()
            .Where(price => price.PreviousPrice != null || price.Status != ItemPriceStatus.Approved || price.DecidedBy != price.RequestedBy);
        if (statusFilter is { } wanted)
        {
            query = query.Where(price => price.Status == wanted);
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var rows = await query
            .OrderByDescending(price => price.RequestedAt)
            .ThenByDescending(price => price.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(price => new
            {
                Row = new PriceRow(price.Id, price.MasterItemId, price.Price, price.PreviousPrice, price.EffectiveFrom, price.Status, price.Reason,
                    price.RequestedAt, price.RequestedBy, price.DecidedAt, price.DecidedBy, price.DecisionNote),
                price.MasterItem.NameAr,
                price.MasterItem.NameEn,
            })
            .ToListAsync(cancellationToken);

        var people = await PlanningPeople.FindAsync(users, PriceChangeView.People(rows.Select(row => row.Row)), cancellationToken);
        return TypedResults.Ok(new Response(
            rows.Select(row => MasterPriceChangeView.From(row.Row, row.NameAr, row.NameEn, people)).ToList(),
            page,
            pageSize,
            totalCount));
    }
}

/// <summary>The body of approve and reject. The note is required to reject.</summary>
internal sealed record MasterPriceDecisionRequest(string? Note);

/// <summary>
/// <c>POST /api/catalog/price-changes/{id}/approve</c> and <c>/reject</c>: catalog Approve, re-checked in the database
/// (the Financial Manager or an Executive), never on one's own request, only while pending. An approved price applies
/// from its effective date; nothing already recorded changes.
/// </summary>
internal static class DecideMasterPriceChange
{
    public static void Map(IEndpointRouteBuilder api)
    {
        api.MapPost("/catalog/price-changes/{priceId:int}/approve",
                (int priceId, MasterPriceDecisionRequest request, HttpContext httpContext, ProjectsDbContext db, IUserDirectory users, PermissionGate gate,
                    CompanyClock clock, ProjectsLog log, CancellationToken cancellationToken) =>
                    HandleAsync(ItemPriceStatus.Approved, priceId, request, httpContext, db, users, gate, clock, log, cancellationToken))
            .RequirePermission(CatalogPermissions.Module, PermissionAction.Approve)
            .WithName("ApproveMasterPriceChange")
            .WithSummary("Approve a pending master price change (catalog Approve, never your own request). 409 own_record, invalid_status");

        api.MapPost("/catalog/price-changes/{priceId:int}/reject",
                (int priceId, MasterPriceDecisionRequest request, HttpContext httpContext, ProjectsDbContext db, IUserDirectory users, PermissionGate gate,
                    CompanyClock clock, ProjectsLog log, CancellationToken cancellationToken) =>
                    HandleAsync(ItemPriceStatus.Rejected, priceId, request, httpContext, db, users, gate, clock, log, cancellationToken))
            .RequirePermission(CatalogPermissions.Module, PermissionAction.Approve)
            .WithName("RejectMasterPriceChange")
            .WithSummary("Reject a pending master price change with a note (catalog Approve, never your own request)");
    }

    internal static async Task<Results<Ok<MasterPriceChangeView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        ItemPriceStatus target,
        int priceId,
        MasterPriceDecisionRequest request,
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
        var note = target == ItemPriceStatus.Approved
            ? Input.OptionalText(request.Note, ItemPriceConfiguration.ReasonMaxLength, errors, "note", "The note")
            : Input.RequiredText(request.Note, ItemPriceConfiguration.ReasonMaxLength, errors, "note", "The note");
        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        if (await gate.CheckAsync(httpContext.User, CatalogPermissions.Module, PermissionAction.Approve, $"MasterPriceChange{target}", cancellationToken) is { } refused)
        {
            return refused;
        }

        var price = await db.MasterItemPrices.Include(candidate => candidate.MasterItem)
            .SingleOrDefaultAsync(candidate => candidate.Id == priceId, cancellationToken);
        if (price is null)
        {
            return PlanningProblems.PriceChangeNotFound();
        }

        if (price.RequestedBy == caller.UserId)
        {
            log.ActionRefused(caller.UserId, $"MasterPriceChange{target}", "deciding own request");
            return WorkspaceProblems.ReviewerIsRecorder();
        }

        if (price.Status != ItemPriceStatus.Pending)
        {
            return WorkspaceProblems.WrongStatus($"Only a pending price change can be decided. This one is {price.Status}.");
        }

        if (target == ItemPriceStatus.Approved
            && await db.MasterItemPrices.AnyAsync(
                other => other.MasterItemId == price.MasterItemId && other.Status == ItemPriceStatus.Approved && other.EffectiveFrom == price.EffectiveFrom,
                cancellationToken))
        {
            return PlanningProblems.PriceDateAlreadyUsed();
        }

        price.Decide(target, caller.UserId, clock.UtcNow, note);

        // Status is a concurrency token: when two deciders race, the second save finds the row no longer pending.
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, MasterItemPriceConfiguration.ApprovedIndex))
        {
            return PlanningProblems.PriceDateAlreadyUsed();
        }

        log.MasterPriceChangeDecided(caller.UserId, price.MasterItemId, price.Id, target.ToString());

        var people = await PlanningPeople.FindAsync(users, [price.RequestedBy, caller.UserId], cancellationToken);
        return TypedResults.Ok(MasterPriceChangeView.From(MasterPriceChangeView.Row(price), price.MasterItem.NameAr, price.MasterItem.NameEn, people));
    }
}
