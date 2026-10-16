using System.Text.Json;
using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Procurement.Common;
using AlMotaheda.Modules.Procurement.Data;
using AlMotaheda.Modules.Procurement.Data.Configurations;
using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Procurement.Features.Orders;

internal sealed record OrderSummary(int Id, string Number, int RequisitionId, int ProjectId, int SupplierId, string Status, int Revision, DateTime CreatedAt);

/// <summary><c>GET /api/procurement/purchase-orders</c> and <c>GET /api/procurement/purchase-orders/{id}</c> (procurement View).</summary>
internal static class ReadOrders
{
    internal sealed record Response(IReadOnlyList<OrderSummary> Items, int Page, int PageSize, int TotalCount);

    public static void Map(IEndpointRouteBuilder api)
    {
        api.MapGet("/procurement/purchase-orders", ListAsync)
            .RequirePermission(ProcurementPermissions.Module, PermissionAction.View)
            .WithName("ListPurchaseOrders")
            .WithSummary("List purchase orders, newest first (status, projectId, supplierId; paging)");

        api.MapGet("/procurement/purchase-orders/{orderId:int}", GetAsync)
            .RequirePermission(ProcurementPermissions.Module, PermissionAction.View)
            .WithName("GetPurchaseOrder")
            .WithSummary("Get one purchase order with received and invoiced quantities and its revisions");
    }

    internal static async Task<Results<Ok<Response>, ValidationProblem, ProblemHttpResult>> ListAsync(
        HttpContext httpContext, ProcurementDbContext db, IVisibleProjects projects, CancellationToken cancellationToken,
        string? status = null, int? projectId = null, int? supplierId = null, int page = 1, int pageSize = Paging.DefaultPageSize)
    {
        if (ProcurementCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        Paging.Validate(page, pageSize, errors);
        var wanted = Input.OptionalName<PurchaseOrderStatus>(status, errors, "status");
        if (errors.HasErrors)
        {
            return ProcurementProblems.Validation(errors);
        }

        var query = db.PurchaseOrders.AsNoTracking();
        if (await caller.VisibleProjectsAsync(projects, cancellationToken) is { } visible)
        {
            query = query.Where(order => visible.Contains(order.ProjectId));
        }

        if (wanted is { } wantedStatus)
        {
            query = query.Where(order => order.Status == wantedStatus);
        }

        if (projectId is { } wantedProject)
        {
            query = query.Where(order => order.ProjectId == wantedProject);
        }

        if (supplierId is { } wantedSupplier)
        {
            query = query.Where(order => order.SupplierId == wantedSupplier);
        }

        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderByDescending(order => order.Id).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(order => new OrderSummary(order.Id, order.Number, order.RequisitionId, order.ProjectId, order.SupplierId, order.Status.ToString(), order.Revision, order.CreatedAt))
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(new Response(rows, page, pageSize, total));
    }

    internal static async Task<Results<Ok<OrderView>, ProblemHttpResult>> GetAsync(
        int orderId, HttpContext httpContext, ProcurementDbContext db, IProjectAccess access, ProcurementViews views, CancellationToken cancellationToken)
    {
        if (ProcurementCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var projectId = await db.PurchaseOrders.AsNoTracking().Where(order => order.Id == orderId).Select(order => (int?)order.ProjectId).SingleOrDefaultAsync(cancellationToken);
        if (await DocumentScope.ProjectAsync(access, httpContext.User, projectId, cancellationToken) is null)
        {
            return ProcurementProblems.NotFound("purchase order");
        }

        return TypedResults.Ok((await views.OrderAsync(orderId, caller.SeesMoney, cancellationToken))!);
    }
}

/// <summary>
/// <c>POST /api/procurement/purchase-orders</c> (procurement Edit): order from an approved requisition. When the
/// Executive asked for quotations, the order comes from the selected quotation (its supplier and prices; any price
/// sent is ignored), and can't be made before one is selected. Otherwise Purchasing picks the supplier and prices.
/// A line can't order more than its requisition line still needs.
/// </summary>
internal static class CreateOrder
{
    internal sealed record LineRequest(int? RequisitionLineId, decimal? Quantity, decimal? UnitPrice);

    internal sealed record Request(int? RequisitionId, int? SupplierId, string? ExpectedDelivery, string? PaymentTerms, string? Notes, IReadOnlyList<LineRequest>? Lines);

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/procurement/purchase-orders", HandleAsync)
            .RequirePermission(ProcurementPermissions.Module, PermissionAction.Edit)
            .WithName("CreatePurchaseOrder")
            .WithSummary("Issue a purchase order from an approved requisition. 409 quotations_pending, exceeds_requisition, supplier_inactive");

    internal static async Task<Results<Created<OrderView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        Request request, HttpContext httpContext, ProcurementDbContext db, IProjectAccess access, ISupplierDirectory suppliers, ProcurementViews views,
        CompanyClock clock, ProcurementLog log, CancellationToken cancellationToken)
    {
        if (ProcurementCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        if (request.RequisitionId is not > 0)
        {
            errors.Add("requisitionId", "The requisition is required.");
        }

        var expected = Input.OptionalDate(request.ExpectedDelivery, errors, "expectedDelivery", "The expected delivery date");
        var terms = Input.OptionalText(request.PaymentTerms, ProcurementColumns.TermsMaxLength, errors, "paymentTerms", "The payment terms");
        var notes = Input.OptionalText(request.Notes, ProcurementColumns.NoteMaxLength, errors, "notes", "Notes");
        var lines = request.Lines ?? [];
        if (lines.Count == 0)
        {
            errors.Add("lines", "Order at least one line.");
        }

        for (var index = 0; index < lines.Count; index++)
        {
            if (lines[index].RequisitionLineId is not > 0)
            {
                errors.Add($"lines[{index}].requisitionLineId", "The requisition line is required.");
            }

            NumberInput.RequiredQuantity(lines[index].Quantity, errors, $"lines[{index}].quantity", "The quantity");
            NumberInput.OptionalMoney(lines[index].UnitPrice, errors, $"lines[{index}].unitPrice", "The unit price");
        }

        if (lines.Select(line => line.RequisitionLineId).Distinct().Count() != lines.Count)
        {
            errors.Add("lines", "Each requisition line is ordered once per order.");
        }

        if (errors.HasErrors)
        {
            return ProcurementProblems.Validation(errors);
        }

        var requisition = await db.Requisitions.AsNoTracking().Include(candidate => candidate.Lines)
            .SingleOrDefaultAsync(candidate => candidate.Id == request.RequisitionId, cancellationToken);
        var project = await DocumentScope.ProjectAsync(access, httpContext.User, requisition?.ProjectId, cancellationToken);
        if (requisition is null || project is null)
        {
            return ProcurementProblems.NotFound("requisition");
        }

        if (project.WritesBlocked)
        {
            return ProcurementProblems.CompletedProject();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await AppLock.TryAcquireAsync(db, AppLock.Project(project.ProjectId), cancellationToken))
        {
            return ProcurementProblems.TryAgainLater();
        }

        var tracked = await db.Requisitions.SingleAsync(candidate => candidate.Id == requisition.Id, cancellationToken);
        if (!tracked.IsOpenForOrders)
        {
            return ProcurementProblems.WrongStatus($"Only an approved requisition can be ordered. This one is {tracked.Status}.");
        }

        // When quotations were asked for, the selected quotation is the source of the order.
        var quotationSet = await db.QuotationRequests.AsNoTracking().SingleOrDefaultAsync(set => set.RequisitionId == requisition.Id, cancellationToken);
        Quotation? quotation = null;
        if (quotationSet is not null)
        {
            if (quotationSet.Status != QuotationRequestStatus.Selected)
            {
                return ProcurementProblems.WaitingForQuotations();
            }

            quotation = await db.Quotations.AsNoTracking().Include(candidate => candidate.Lines)
                .SingleAsync(candidate => candidate.Id == quotationSet.SelectedQuotationId, cancellationToken);
        }
        else if (request.SupplierId is not > 0)
        {
            return ProcurementProblems.Validation("supplierId", "The supplier is required.");
        }

        var supplierId = quotation?.SupplierId ?? request.SupplierId!.Value;
        if (!(await suppliers.FindAsync([supplierId], cancellationToken)).TryGetValue(supplierId, out var supplier) || !supplier.IsActive)
        {
            return ProcurementProblems.InactiveSupplier();
        }

        var prLines = requisition.Lines.ToDictionary(line => line.Id);
        var ordered = await ChainQuantities.OrderedAsync(db, prLines.Keys.ToList(), cancellationToken);
        var order = new PurchaseOrder
        {
            RequisitionId = requisition.Id,
            ProjectId = requisition.ProjectId,
            SupplierId = supplierId,
            QuotationId = quotation?.Id,
            ExpectedDelivery = expected,
            PaymentTerms = terms ?? quotation?.PaymentTerms,
            Notes = notes,
            CreatedAt = clock.UtcNow,
            CreatedBy = caller.UserId,
        };

        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            if (!prLines.TryGetValue(line.RequisitionLineId!.Value, out var prLine))
            {
                return ProcurementProblems.Validation($"lines[{index}].requisitionLineId", "The line isn't on this requisition.");
            }

            if (line.Quantity > ProcurementRules.StillNeeded(prLine.Quantity, ordered.GetValueOrDefault(prLine.Id)))
            {
                return ProcurementProblems.MoreThanRequested(prLine.Description);
            }

            decimal price;
            if (quotation is not null)
            {
                var quoted = quotation.Lines.SingleOrDefault(candidate => candidate.RequisitionLineId == prLine.Id);
                if (quoted is null)
                {
                    return ProcurementProblems.Validation($"lines[{index}].requisitionLineId", "The selected quotation doesn't quote this line.");
                }

                price = quoted.UnitPrice;
            }
            else if (line.UnitPrice is { } given)
            {
                price = given;
            }
            else
            {
                return ProcurementProblems.Validation($"lines[{index}].unitPrice", "The unit price is required.");
            }

            order.Lines.Add(new PurchaseOrderLine
            {
                RequisitionLineId = prLine.Id,
                CatalogItemId = prLine.CatalogItemId,
                ProjectItemId = prLine.ProjectItemId,
                Description = prLine.Description,
                Unit = prLine.Unit,
                Quantity = line.Quantity!.Value,
                UnitPrice = price,
            });
        }

        db.PurchaseOrders.Add(order);
        var fullyOrdered = prLines.Values.All(prLine =>
            ordered.GetValueOrDefault(prLine.Id) + order.Lines.Where(line => line.RequisitionLineId == prLine.Id).Sum(line => line.Quantity) >= prLine.Quantity);
        tracked.MarkOrdered(fullyOrdered, caller.UserId, clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        log.OrderIssued(caller.UserId, requisition.Id, order.Id);
        return TypedResults.Created($"/api/procurement/purchase-orders/{order.Id}", (await views.OrderAsync(order.Id, caller.SeesMoney, cancellationToken))!);
    }
}

/// <summary>
/// <c>POST /api/procurement/purchase-orders/{id}/revise</c> <c>{ reason, lines: [{ lineId, quantity, unitPrice }] }</c>
/// (procurement Edit): every change to an issued order is a numbered revision that keeps the previous lines. A
/// quantity can't go below what was received nor above what the requisition allows; a price can't change once the
/// line is invoiced. <c>POST …/cancel</c> <c>{ reason }</c>: only while nothing was received.
/// </summary>
internal static class ChangeOrder
{
    internal sealed record LineRequest(int? LineId, decimal? Quantity, decimal? UnitPrice);

    internal sealed record ReviseRequest(string? Reason, IReadOnlyList<LineRequest>? Lines, string? RowVersion);

    internal sealed record CancelRequest(string? Reason, string? RowVersion);

    private sealed record PreviousLine(int LineId, decimal Quantity, decimal UnitPrice);

    public static void Map(IEndpointRouteBuilder api)
    {
        api.MapPost("/procurement/purchase-orders/{orderId:int}/revise", ReviseAsync)
            .RequirePermission(ProcurementPermissions.Module, PermissionAction.Edit)
            .WithName("RevisePurchaseOrder")
            .WithSummary("Revise an order's quantities or prices with a reason. 409 below_received, exceeds_requisition, price_already_invoiced");

        api.MapPost("/procurement/purchase-orders/{orderId:int}/cancel", CancelAsync)
            .RequirePermission(ProcurementPermissions.Module, PermissionAction.Edit)
            .WithName("CancelPurchaseOrder")
            .WithSummary("Cancel an order with nothing received. 409 has_receipts");
    }

    internal static async Task<Results<Ok<OrderView>, ValidationProblem, ProblemHttpResult>> ReviseAsync(
        int orderId, ReviseRequest request, HttpContext httpContext, ProcurementDbContext db, IProjectAccess access, ProcurementViews views,
        CompanyClock clock, ProcurementLog log, CancellationToken cancellationToken)
    {
        if (ProcurementCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var reason = Input.RequiredText(request.Reason, ProcurementColumns.NoteMaxLength, errors, "reason", "The reason");
        var lines = request.Lines ?? [];
        if (lines.Count == 0)
        {
            errors.Add("lines", "Revise at least one line.");
        }

        for (var index = 0; index < lines.Count; index++)
        {
            if (lines[index].LineId is not > 0)
            {
                errors.Add($"lines[{index}].lineId", "The order line is required.");
            }

            if (lines[index].Quantity is not { } quantity || quantity < 0 || quantity > NumberInput.MaxQuantity || decimal.Round(quantity, 3) != quantity)
            {
                errors.Add($"lines[{index}].quantity", "The quantity must be zero or more, with at most three decimals.");
            }

            NumberInput.RequiredMoney(lines[index].UnitPrice, errors, $"lines[{index}].unitPrice", "The unit price");
        }

        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (errors.HasErrors)
        {
            return ProcurementProblems.Validation(errors);
        }

        var (order, project, problem) = await LoadAsync(db, access, httpContext, orderId, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await AppLock.TryAcquireAsync(db, AppLock.Project(project!.ProjectId), cancellationToken))
        {
            return ProcurementProblems.TryAgainLater();
        }

        if (!order!.IsOpen && order.Status != PurchaseOrderStatus.Received)
        {
            return ProcurementProblems.WrongStatus($"A {order.Status} order can't be revised.");
        }

        var lineIds = order.Lines.Select(line => line.Id).ToList();
        var received = await ChainQuantities.ReceivedAsync(db, lineIds, cancellationToken);
        var invoiced = await ChainQuantities.InvoicedAsync(db, lineIds, cancellationToken);
        var prLineIds = order.Lines.Select(line => line.RequisitionLineId).ToList();
        var prLines = await db.RequisitionLines.AsNoTracking().Where(line => prLineIds.Contains(line.Id)).ToDictionaryAsync(line => line.Id, cancellationToken);
        var orderedEverywhere = await ChainQuantities.OrderedAsync(db, prLineIds, cancellationToken);

        var previous = JsonSerializer.Serialize(order.Lines.OrderBy(line => line.Id).Select(line => new PreviousLine(line.Id, line.Quantity, line.UnitPrice)),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        foreach (var change in lines)
        {
            var line = order.Lines.SingleOrDefault(candidate => candidate.Id == change.LineId);
            if (line is null)
            {
                return ProcurementProblems.Validation("lines", $"Line {change.LineId} isn't on this order.");
            }

            if (change.Quantity < received.GetValueOrDefault(line.Id))
            {
                return ProcurementProblems.LessThanReceived(line.Description);
            }

            var elsewhere = orderedEverywhere.GetValueOrDefault(line.RequisitionLineId) - line.Quantity;
            if (change.Quantity > ProcurementRules.StillNeeded(prLines[line.RequisitionLineId].Quantity, elsewhere))
            {
                return ProcurementProblems.MoreThanRequested(line.Description);
            }

            if (change.UnitPrice != line.UnitPrice && invoiced.GetValueOrDefault(line.Id) > 0)
            {
                return ProcurementProblems.PriceLocked(line.Description);
            }
        }

        var now = clock.UtcNow;
        order.Revise(previous, reason!, caller.UserId, now);
        foreach (var change in lines)
        {
            var line = order.Lines.Single(candidate => candidate.Id == change.LineId);
            line.Quantity = change.Quantity!.Value;
            line.UnitPrice = change.UnitPrice!.Value;
        }

        order.Received(
            order.Lines.Any(line => received.GetValueOrDefault(line.Id) > 0),
            order.Lines.All(line => received.GetValueOrDefault(line.Id) >= line.Quantity));
        await ReopenRequisitionAsync(db, order, caller.UserId, now, cancellationToken);

        db.Entry(order).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        await transaction.CommitAsync(cancellationToken);
        log.OrderRevised(caller.UserId, order.Id, order.Revision);
        return TypedResults.Ok((await views.OrderAsync(order.Id, caller.SeesMoney, cancellationToken))!);
    }

    internal static async Task<Results<Ok<OrderView>, ValidationProblem, ProblemHttpResult>> CancelAsync(
        int orderId, CancelRequest request, HttpContext httpContext, ProcurementDbContext db, IProjectAccess access, ProcurementViews views,
        CompanyClock clock, ProcurementLog log, CancellationToken cancellationToken)
    {
        if (ProcurementCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var reason = Input.RequiredText(request.Reason, ProcurementColumns.NoteMaxLength, errors, "reason", "The reason");
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (errors.HasErrors)
        {
            return ProcurementProblems.Validation(errors);
        }

        var (order, project, problem) = await LoadAsync(db, access, httpContext, orderId, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await AppLock.TryAcquireAsync(db, AppLock.Project(project!.ProjectId), cancellationToken))
        {
            return ProcurementProblems.TryAgainLater();
        }

        if (order!.Status != PurchaseOrderStatus.Issued || (await ChainQuantities.ReceivedAsync(db, order.Lines.Select(line => line.Id).ToList(), cancellationToken)).Count > 0)
        {
            return order.Status == PurchaseOrderStatus.Cancelled ? ProcurementProblems.WrongStatus("The order is already cancelled.") : ProcurementProblems.Received();
        }

        var now = clock.UtcNow;
        order.Cancel(caller.UserId, now, reason!);
        await ReopenRequisitionAsync(db, order, caller.UserId, now, cancellationToken);

        db.Entry(order).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        await transaction.CommitAsync(cancellationToken);
        log.OrderCancelled(caller.UserId, order.Id);
        return TypedResults.Ok((await views.OrderAsync(order.Id, caller.SeesMoney, cancellationToken))!);
    }

    private static async Task<(PurchaseOrder? Order, ProjectAccessInfo? Project, ProblemHttpResult? Problem)> LoadAsync(
        ProcurementDbContext db, IProjectAccess access, HttpContext httpContext, int orderId, CancellationToken cancellationToken)
    {
        var order = await db.PurchaseOrders.Include(candidate => candidate.Lines).Include(candidate => candidate.Revisions)
            .SingleOrDefaultAsync(candidate => candidate.Id == orderId, cancellationToken);
        var project = await DocumentScope.ProjectAsync(access, httpContext.User, order?.ProjectId, cancellationToken);
        if (order is null || project is null)
        {
            return (null, null, ProcurementProblems.NotFound("purchase order"));
        }

        return project.WritesBlocked ? (order, project, ProcurementProblems.CompletedProject()) : (order, project, null);
    }

    /// <summary>After an order shrinks or is cancelled, its requisition is open for orders again if something is still needed.</summary>
    private static async Task ReopenRequisitionAsync(
        ProcurementDbContext db, PurchaseOrder order, int userId, DateTime now, CancellationToken cancellationToken)
    {
        var requisition = await db.Requisitions.Include(candidate => candidate.Lines).SingleAsync(candidate => candidate.Id == order.RequisitionId, cancellationToken);
        if (requisition.Status is not (RequisitionStatus.Approved or RequisitionStatus.Ordered))
        {
            return;
        }

        // Saved quantities of the other orders, plus this order's pending ones.
        var prLineIds = requisition.Lines.Select(prLine => prLine.Id).ToList();
        var ordered = await db.PurchaseOrderLines.AsNoTracking()
            .Where(line => line.OrderId != order.Id && prLineIds.Contains(line.RequisitionLineId))
            .Join(db.PurchaseOrders.Where(other => other.Status != PurchaseOrderStatus.Cancelled), line => line.OrderId, other => other.Id, (line, _) => line)
            .GroupBy(line => line.RequisitionLineId)
            .Select(group => new { group.Key, Quantity = group.Sum(line => line.Quantity) })
            .ToDictionaryAsync(entry => entry.Key, entry => entry.Quantity, cancellationToken);
        var mine = order.Status == PurchaseOrderStatus.Cancelled ? [] : order.Lines;

        var fully = requisition.Lines.All(prLine =>
            ordered.GetValueOrDefault(prLine.Id) + mine.Where(line => line.RequisitionLineId == prLine.Id).Sum(line => line.Quantity) >= prLine.Quantity);
        requisition.MarkOrdered(fully, userId, now);
    }
}
