using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Data.Configurations;
using AlMotaheda.Modules.Projects.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Features.Prices;

/// <summary>
/// <c>GET /api/price-items</c>: the company price list (Price Control, "ضبط الأسعار"), with today's general range of
/// each item. catalog View. Filters: <c>search</c> (names), <c>classification</c> (a category code), <c>status</c>; paging.
/// </summary>
internal static class ListPriceItems
{
    internal sealed record Response(IReadOnlyList<PriceItemRow> Items, int Page, int PageSize, int TotalCount);

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/price-items", HandleAsync)
            .RequirePermission(PricePermissions.Module, PermissionAction.View)
            .WithName("ListPriceItems")
            .WithSummary("The company price list with today's reference range of each item (search, classification, status, paging)");

    internal static async Task<Results<Ok<Response>, ValidationProblem>> HandleAsync(
        ProjectsDbContext db,
        IUserDirectory users,
        CompanyClock clock,
        CancellationToken cancellationToken,
        string? search = null,
        string? classification = null,
        string? status = null,
        int page = 1,
        int pageSize = Paging.DefaultPageSize)
    {
        var errors = new ValidationErrors();
        var wantedCategory = Input.Clean(classification);
        var parsedStatus = Input.OptionalName<PriceItemStatus>(status, errors, "status");
        Paging.Validate(page, pageSize, errors);
        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        var query = db.PriceItems.AsNoTracking();
        if (Input.Clean(search) is { } text)
        {
            query = query.Where(item => item.NameAr.Contains(text) || item.NameEn.Contains(text));
        }

        if (wantedCategory is not null)
        {
            query = query.Where(item => item.Category.Code == wantedCategory);
        }

        if (parsedStatus is { } wantedStatus)
        {
            query = query.Where(item => item.Status == wantedStatus);
        }

        var total = await query.CountAsync(cancellationToken);
        var rows = await PriceQueries.WithCurrentAsync(
            db,
            await PriceQueries.Items(db, query.OrderBy(item => item.NameEn).ThenBy(item => item.Id).Skip((page - 1) * pageSize).Take(pageSize), clock.Today)
                .ToListAsync(cancellationToken),
            cancellationToken);

        var people = await PlanningPeople.FindAsync(
            users, rows.Where(row => row.Current is not null).SelectMany(row => new[] { row.Current!.RequestedBy, row.Current.DecidedBy ?? row.Current.RequestedBy }), cancellationToken);
        return TypedResults.Ok(new Response(rows.Select(row => row.View(people, clock.Today)).ToList(), page, pageSize, total));
    }
}

/// <summary><c>GET /api/price-items/{id}</c>: one price list item with its whole range history, newest first. catalog View.</summary>
internal static class GetPriceItem
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/price-items/{priceItemId:int}", HandleAsync)
            .RequirePermission(PricePermissions.Module, PermissionAction.View)
            .WithName("GetPriceItem")
            .WithSummary("One price list item with every range: general and supplier-specific, approved, pending and rejected");

    internal static async Task<Results<Ok<PriceItemDetail>, ProblemHttpResult>> HandleAsync(
        int priceItemId, ProjectsDbContext db, IUserDirectory users, CompanyClock clock, CancellationToken cancellationToken)
    {
        var detail = await PriceQueries.DetailAsync(db, users, priceItemId, clock.Today, cancellationToken);
        return detail is null ? PriceProblems.PriceItemNotFound() : TypedResults.Ok(detail);
    }
}

/// <summary>
/// <c>POST /api/price-items</c>: add an item to the price list with its first general range, approved at once (the
/// rule of the project catalog it replaces). catalog Create. Later changes go through a request an Executive decides.
/// </summary>
internal static class CreatePriceItem
{
    internal sealed record Request(
        string? NameAr,
        string? NameEn,
        string? Classification,
        string? Unit,
        string? Notes,
        decimal? MinPrice,
        decimal? MaxPrice,
        string? Currency,
        string? EffectiveFrom,
        string? EffectiveTo,
        string? Conditions,
        string? Kind = null,
        decimal? ReferencePrice = null,
        decimal? TolerancePercent = null,
        string? SourceNote = null,
        string? CategoryCode = null);

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/price-items", HandleAsync)
            .RequirePermission(PricePermissions.Module, PermissionAction.Create)
            .WithName("CreatePriceItem")
            .WithSummary("Add an item or service to the company price list with its first reference range (approved at once)");

    internal static async Task<Results<Created<PriceItemDetail>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        Request request,
        HttpContext httpContext,
        ProjectsDbContext db,
        IUserDirectory users,
        CompanyClock clock,
        ProjectsLog log,
        CancellationToken cancellationToken)
    {
        if (httpContext.User.GetUserId() is not { } userId)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var fields = PriceItemFields.Validate(request.NameAr, request.NameEn, request.CategoryCode ?? request.Classification, request.Notes, errors);
        // The first range is always the general one: supplier-specific ranges are added as changes.
        var range = RangeFields.Validate(
            new RangeInput(null, request.MinPrice, request.MaxPrice, request.EffectiveFrom, request.EffectiveTo, request.Conditions, request.Currency,
                request.Kind, request.ReferencePrice, request.TolerancePercent, request.SourceNote),
            clock.Today, errors);
        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        // The category and the unit are the configured ones (looked up once the form itself is right).
        var unit = await PriceItemFields.UnitAsync(db, request.Unit, errors, cancellationToken);
        fields = await fields!.ResolveCategoryAsync(db, errors, cancellationToken);
        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        if (await fields!.NameTakenAsync(db, exceptId: null, cancellationToken))
        {
            return PriceProblems.NameTaken();
        }

        var now = clock.UtcNow;
        var item = new PriceItem
        {
            NameAr = fields.NameAr,
            NameEn = fields.NameEn,
            Unit = unit!,
            Status = PriceItemStatus.Active,
            CreatedAt = now,
            CreatedBy = userId,
        };
        fields.ApplyTo(item);
        var first = range!.ToRange(PriceRangeStatus.Approved, userId, now, reason: null);
        item.Ranges.Add(first);
        db.PriceItems.Add(item);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (PriceItemFields.IsNameRace(exception))
        {
            return PriceProblems.NameTaken();
        }

        log.PriceItemCreated(userId, item.Id, first.Id);
        var detail = await PriceQueries.DetailAsync(db, users, item.Id, clock.Today, cancellationToken);
        return TypedResults.Created($"/api/price-items/{item.Id}", detail!);
    }
}

/// <summary>
/// <c>PUT /api/price-items/{id}</c>: edit the names, classification, notes and status. Never the unit or a price:
/// prices change by request only. catalog Create, like editing a project catalog item.
/// </summary>
internal static class UpdatePriceItem
{
    internal sealed record Request(string? NameAr, string? NameEn, string? Classification, string? Notes, string? Status, string? RowVersion, string? CategoryCode = null);

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPut("/price-items/{priceItemId:int}", HandleAsync)
            .RequirePermission(PricePermissions.Module, PermissionAction.Create)
            .WithName("UpdatePriceItem")
            .WithSummary("Edit a price list item's names, classification, notes and status (not its unit or prices)");

    internal static async Task<Results<Ok<PriceItemDetail>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int priceItemId,
        Request request,
        HttpContext httpContext,
        ProjectsDbContext db,
        IUserDirectory users,
        CompanyClock clock,
        ProjectsLog log,
        CancellationToken cancellationToken)
    {
        if (httpContext.User.GetUserId() is not { } userId)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var fields = PriceItemFields.Validate(request.NameAr, request.NameEn, request.CategoryCode ?? request.Classification, request.Notes, errors);
        var status = Input.RequiredName<PriceItemStatus>(request.Status, errors, "status");
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        fields = await fields!.ResolveCategoryAsync(db, errors, cancellationToken);
        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        var item = await db.PriceItems.SingleOrDefaultAsync(candidate => candidate.Id == priceItemId, cancellationToken);
        if (item is null)
        {
            return PriceProblems.PriceItemNotFound();
        }

        if (await fields!.NameTakenAsync(db, item.Id, cancellationToken))
        {
            return PriceProblems.NameTaken();
        }

        var oldStatus = item.Status;
        fields.ApplyTo(item);
        item.Status = status!.Value;
        item.UpdatedAt = clock.UtcNow;
        item.UpdatedBy = userId;
        db.Entry(item).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }
        catch (DbUpdateException exception) when (PriceItemFields.IsNameRace(exception))
        {
            return PriceProblems.NameTaken();
        }

        log.PriceItemUpdated(userId, item.Id, oldStatus.ToString(), item.Status.ToString());
        return TypedResults.Ok((await PriceQueries.DetailAsync(db, users, item.Id, clock.Today, cancellationToken))!);
    }
}

/// <summary>
/// <c>POST /api/price-items/{id}/ranges</c>: ask for a new reference range, general or for one supplier, from a date
/// (optionally until a date). It waits for an Executive; until then the current range keeps applying. catalog Edit.
/// </summary>
internal static class RequestPriceRange
{
    internal sealed record Request(
        int? SupplierId, decimal? MinPrice, decimal? MaxPrice, string? Currency, string? EffectiveFrom, string? EffectiveTo, string? Conditions, string? Reason,
        string? Kind = null, decimal? ReferencePrice = null, decimal? TolerancePercent = null, string? SourceNote = null);

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/price-items/{priceItemId:int}/ranges", HandleAsync)
            .RequirePermission(PricePermissions.Module, PermissionAction.Edit)
            .WithName("RequestPriceRange")
            .WithSummary("Request a new reference price range (general or supplier-specific); an Executive approves it");

    internal static async Task<Results<Created<PriceRangeView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int priceItemId,
        Request request,
        HttpContext httpContext,
        ProjectsDbContext db,
        IUserDirectory users,
        CompanyClock clock,
        ProjectsLog log,
        CancellationToken cancellationToken)
    {
        if (httpContext.User.GetUserId() is not { } userId)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var range = RangeFields.Validate(
            new RangeInput(request.SupplierId, request.MinPrice, request.MaxPrice, request.EffectiveFrom, request.EffectiveTo, request.Conditions, request.Currency,
                request.Kind, request.ReferencePrice, request.TolerancePercent, request.SourceNote),
            clock.Today, errors);
        var reason = Input.RequiredText(request.Reason, PriceRangeConfiguration.TextMaxLength, errors, "reason", "The reason");
        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        await range!.CheckSupplierAsync(db, errors, cancellationToken);
        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await PriceItemLock.TryAcquireAsync(db, priceItemId, cancellationToken))
        {
            return ProjectProblems.TryAgainLater();
        }

        var item = await db.PriceItems.AsNoTracking().Where(candidate => candidate.Id == priceItemId)
            .Select(candidate => new { candidate.Status }).SingleOrDefaultAsync(cancellationToken);
        if (item is null)
        {
            return PriceProblems.PriceItemNotFound();
        }

        if (item.Status != PriceItemStatus.Active)
        {
            return PriceProblems.ItemInactive();
        }

        if (await db.PriceRanges.AnyAsync(
                other => other.PriceItemId == priceItemId && other.SupplierId == range.SupplierId && other.Status == PriceRangeStatus.Pending,
                cancellationToken))
        {
            return PriceProblems.ChangePending();
        }

        var row = range.ToRange(PriceRangeStatus.Pending, userId, clock.UtcNow, reason);
        row.PriceItemId = priceItemId;
        db.PriceRanges.Add(row);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, PriceRangeConfiguration.PendingIndex))
        {
            return PriceProblems.ChangePending();
        }

        await transaction.CommitAsync(cancellationToken);
        log.PriceRangeRequested(userId, priceItemId, row.Id);

        var view = await PriceRangeLoader.LoadAsync(db, users, row.Id, cancellationToken);
        return TypedResults.Created($"/api/price-items/{priceItemId}", view!);
    }
}

/// <summary><c>GET /api/price-ranges</c>: ranges by status (default <c>Pending</c>: the Executive's queue), newest request first. catalog View.</summary>
internal static class ListPriceRanges
{
    internal sealed record Response(IReadOnlyList<PriceRangeView> Items, int Page, int PageSize, int TotalCount);

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/price-ranges", HandleAsync)
            .RequirePermission(PricePermissions.Module, PermissionAction.View)
            .WithName("ListPriceRanges")
            .WithSummary("Price range requests by status (default Pending: the approval queue)");

    internal static async Task<Results<Ok<Response>, ValidationProblem>> HandleAsync(
        ProjectsDbContext db,
        IUserDirectory users,
        CancellationToken cancellationToken,
        string? status = null,
        int page = 1,
        int pageSize = Paging.DefaultPageSize)
    {
        var errors = new ValidationErrors();
        var parsed = Input.OptionalName<PriceRangeStatus>(status, errors, "status") ?? PriceRangeStatus.Pending;
        Paging.Validate(page, pageSize, errors);
        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        var query = db.PriceRanges.AsNoTracking().Where(range => range.Status == parsed);
        var total = await query.CountAsync(cancellationToken);
        var rows = await PriceQueries.Ranges(query.OrderByDescending(range => range.RequestedAt).ThenByDescending(range => range.Id)
                .Skip((page - 1) * pageSize).Take(pageSize))
            .ToListAsync(cancellationToken);
        var people = await PlanningPeople.FindAsync(users, rows.SelectMany(row => new[] { row.RequestedBy, row.DecidedBy ?? row.RequestedBy }), cancellationToken);
        return TypedResults.Ok(new Response(rows.Select(row => row.View(people)).ToList(), page, pageSize, total));
    }
}

/// <summary>The body of approve and reject. The note is required to reject.</summary>
internal sealed record PriceRangeDecisionRequest(string? Note);

/// <summary><c>POST /api/price-ranges/{id}/approve</c>: the range applies from its effective date (a future date schedules it).</summary>
internal static class ApprovePriceRange
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/price-ranges/{rangeId:int}/approve", HandleAsync)
            .RequirePermission(PricePermissions.Module, PermissionAction.Approve)
            .WithName("ApprovePriceRange")
            .WithSummary("Approve a pending price range (an Executive, never your own request)");

    internal static Task<Results<Ok<PriceRangeView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int rangeId, PriceRangeDecisionRequest request, HttpContext httpContext, ProjectsDbContext db, IUserDirectory users,
        ExecutiveGate executives, CompanyClock clock, ProjectsLog log, CancellationToken cancellationToken) =>
        PriceRangeDecision.HandleAsync(PriceRangeStatus.Approved, rangeId, request, httpContext, db, users, executives, clock, log, cancellationToken);
}

/// <summary><c>POST /api/price-ranges/{id}/reject</c>: the request is closed; the current range keeps applying.</summary>
internal static class RejectPriceRange
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/price-ranges/{rangeId:int}/reject", HandleAsync)
            .RequirePermission(PricePermissions.Module, PermissionAction.Approve)
            .WithName("RejectPriceRange")
            .WithSummary("Reject a pending price range with a note (an Executive, never your own request)");

    internal static Task<Results<Ok<PriceRangeView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int rangeId, PriceRangeDecisionRequest request, HttpContext httpContext, ProjectsDbContext db, IUserDirectory users,
        ExecutiveGate executives, CompanyClock clock, ProjectsLog log, CancellationToken cancellationToken) =>
        PriceRangeDecision.HandleAsync(PriceRangeStatus.Rejected, rangeId, request, httpContext, db, users, executives, clock, log, cancellationToken);
}

/// <summary>
/// The one decision flow behind approve and reject: catalog Approve and an active Executive (checked in the
/// database, not only in the token), never on your own request, only while pending, under the item's lock.
/// </summary>
internal static class PriceRangeDecision
{
    public static async Task<Results<Ok<PriceRangeView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        PriceRangeStatus target,
        int rangeId,
        PriceRangeDecisionRequest request,
        HttpContext httpContext,
        ProjectsDbContext db,
        IUserDirectory users,
        ExecutiveGate executives,
        CompanyClock clock,
        ProjectsLog log,
        CancellationToken cancellationToken)
    {
        if (httpContext.User.GetUserId() is not { } userId)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var note = target == PriceRangeStatus.Approved
            ? Input.OptionalText(request.Note, PriceRangeConfiguration.TextMaxLength, errors, "note", "The note")
            : Input.RequiredText(request.Note, PriceRangeConfiguration.TextMaxLength, errors, "note", "The note");
        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        if (await executives.CheckAsync(httpContext.User, $"PriceRange{target}", cancellationToken) is { } refusal)
        {
            return refusal;
        }

        var itemId = await db.PriceRanges.AsNoTracking().Where(range => range.Id == rangeId).Select(range => (int?)range.PriceItemId)
            .SingleOrDefaultAsync(cancellationToken);
        if (itemId is null)
        {
            return PriceProblems.PriceRangeNotFound();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await PriceItemLock.TryAcquireAsync(db, itemId.Value, cancellationToken))
        {
            return ProjectProblems.TryAgainLater();
        }

        var range = await db.PriceRanges.SingleAsync(candidate => candidate.Id == rangeId, cancellationToken);
        if (range.RequestedBy == userId)
        {
            log.ActionRefused(userId, $"PriceRange{target}", "deciding own request");
            return WorkspaceProblems.ReviewerIsRecorder();
        }

        if (range.Status != PriceRangeStatus.Pending)
        {
            return WorkspaceProblems.WrongStatus($"Only a pending price range can be decided. This one is {range.Status}.");
        }

        if (target == PriceRangeStatus.Approved
            && await db.PriceRanges.AnyAsync(
                other => other.PriceItemId == range.PriceItemId && other.SupplierId == range.SupplierId
                         && other.Status == PriceRangeStatus.Approved && other.EffectiveFrom == range.EffectiveFrom,
                cancellationToken))
        {
            return PriceProblems.DateTaken();
        }

        range.Decide(target, userId, clock.UtcNow, note);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, PriceRangeConfiguration.ApprovedIndex))
        {
            return PriceProblems.DateTaken();
        }

        await transaction.CommitAsync(cancellationToken);
        log.PriceRangeDecided(userId, range.PriceItemId, range.Id, target.ToString());
        return TypedResults.Ok((await PriceRangeLoader.LoadAsync(db, users, range.Id, cancellationToken))!);
    }
}

/// <summary>
/// <c>GET /api/price-items/{id}/check</c>: how a unit price compares with the reference range that applies on a date
/// (for a supplier, when given). The supply form calls it to warn before saving; the save computes the same answer
/// again on the server. projects Create or catalog View.
/// </summary>
internal static class CheckPrice
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/price-items/{priceItemId:int}/check", HandleAsync)
            .RequireAnyPermission((ProjectPermissions.Module, PermissionAction.Create), (PricePermissions.Module, PermissionAction.View))
            .WithName("CheckPrice")
            .WithSummary("Compare a unit price with the reference range on a date: within, above or below, with the difference per unit and in total");

    internal static async Task<Results<Ok<PriceCheckView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int priceItemId,
        ProjectsDbContext db,
        CompanyClock clock,
        CancellationToken cancellationToken,
        decimal? unitPrice = null,
        decimal? quantity = null,
        string? date = null,
        int? supplierId = null)
    {
        var errors = new ValidationErrors();
        var price = NumberInput.RequiredMoney(unitPrice, errors, "unitPrice", "The unit price");
        var amount = quantity is null ? 1 : NumberInput.RequiredQuantity(quantity, errors, "quantity", "The quantity");
        var day = Input.OptionalDate(date, errors, "date", "The date") ?? clock.Today;
        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        var item = await db.PriceItems.AsNoTracking().Where(candidate => candidate.Id == priceItemId)
            .Select(candidate => new { candidate.NameAr, candidate.NameEn }).SingleOrDefaultAsync(cancellationToken);
        if (item is null)
        {
            return PriceProblems.PriceItemNotFound();
        }

        var range = await PriceRanges.ApplicableAsync(db, priceItemId, supplierId, day, cancellationToken);
        return TypedResults.Ok(PriceCheckView.Of(priceItemId, item.NameAr, item.NameEn, range, price!.Value, amount!.Value, null));
    }
}

internal static class PriceRangeLoader
{
    public static async Task<PriceRangeView?> LoadAsync(ProjectsDbContext db, IUserDirectory users, int rangeId, CancellationToken cancellationToken)
    {
        var row = await PriceQueries.Ranges(db.PriceRanges.AsNoTracking().Where(range => range.Id == rangeId)).SingleOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return null;
        }

        var people = await PlanningPeople.FindAsync(users, [row.RequestedBy, row.DecidedBy ?? row.RequestedBy], cancellationToken);
        return row.View(people);
    }
}

/// <summary>Wires the company price list (price management). See docs/project-workspace/08-cost-management.md.</summary>
internal static class PriceFeatures
{
    public static void Map(IEndpointRouteBuilder api)
    {
        ListPriceItems.Map(api);
        GetPriceItem.Map(api);
        CreatePriceItem.Map(api);
        UpdatePriceItem.Map(api);
        RequestPriceRange.Map(api);
        ListPriceRanges.Map(api);
        ApprovePriceRange.Map(api);
        RejectPriceRange.Map(api);
        CheckPrice.Map(api);
        PriceControl.PriceCatalogEndpoints.Map(api);
        PriceControl.PriceExceptionEndpoints.Map(api);
    }
}
