using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Procurement.Common;
using AlMotaheda.Modules.Procurement.Contracts;
using AlMotaheda.Modules.Procurement.Data;
using AlMotaheda.Modules.Procurement.Data.Configurations;
using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Procurement.Features.Invoices;

internal sealed record InvoiceSummary(int Id, string Number, int OrderId, int ProjectId, int SupplierId, string InvoiceNo, DateOnly InvoiceDate, string Status, decimal? Total);

/// <summary><c>GET /api/procurement/supplier-invoices</c> (status, projectId, supplierId; paging) and <c>GET …/{id}</c> (procurement View).</summary>
internal static class ReadInvoices
{
    internal sealed record Response(IReadOnlyList<InvoiceSummary> Items, int Page, int PageSize, int TotalCount);

    public static void Map(IEndpointRouteBuilder api)
    {
        api.MapGet("/procurement/supplier-invoices", ListAsync)
            .RequirePermission(ProcurementPermissions.Module, PermissionAction.View)
            .WithName("ListSupplierInvoices")
            .WithSummary("List supplier invoices, newest first; status=Mismatch is the Accountant's review queue");

        api.MapGet("/procurement/supplier-invoices/{invoiceId:int}", GetAsync)
            .RequirePermission(ProcurementPermissions.Module, PermissionAction.View)
            .WithName("GetSupplierInvoice")
            .WithSummary("Get one supplier invoice with its 3-way match, posted records and payment state");
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
        var wanted = Input.OptionalName<SupplierInvoiceStatus>(status, errors, "status");
        if (errors.HasErrors)
        {
            return ProcurementProblems.Validation(errors);
        }

        var query = db.SupplierInvoices.AsNoTracking();
        if (await caller.VisibleProjectsAsync(projects, cancellationToken) is { } visible)
        {
            query = query.Where(invoice => visible.Contains(invoice.ProjectId));
        }

        if (wanted is { } wantedStatus)
        {
            query = query.Where(invoice => invoice.Status == wantedStatus);
        }

        if (projectId is { } wantedProject)
        {
            query = query.Where(invoice => invoice.ProjectId == wantedProject);
        }

        if (supplierId is { } wantedSupplier)
        {
            query = query.Where(invoice => invoice.SupplierId == wantedSupplier);
        }

        var seesMoney = caller.SeesMoney;
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderByDescending(invoice => invoice.Id).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(invoice => new InvoiceSummary(invoice.Id, invoice.Number, invoice.OrderId, invoice.ProjectId, invoice.SupplierId, invoice.InvoiceNo,
                invoice.InvoiceDate, invoice.Status.ToString(), seesMoney ? invoice.Total : null))
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(new Response(rows, page, pageSize, total));
    }

    internal static async Task<Results<Ok<InvoiceView>, ProblemHttpResult>> GetAsync(
        int invoiceId, HttpContext httpContext, ProcurementDbContext db, IProjectAccess access, ProcurementViews views, CancellationToken cancellationToken)
    {
        if (ProcurementCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var projectId = await db.SupplierInvoices.AsNoTracking().Where(invoice => invoice.Id == invoiceId).Select(invoice => (int?)invoice.ProjectId).SingleOrDefaultAsync(cancellationToken);
        if (await DocumentScope.ProjectAsync(access, httpContext.User, projectId, cancellationToken) is null)
        {
            return ProcurementProblems.NotFound("supplier invoice");
        }

        return TypedResults.Ok((await views.InvoiceAsync(invoiceId, caller.SeesMoney, cancellationToken))!);
    }
}

/// <summary>
/// <c>POST /api/procurement/purchase-orders/{id}/invoices</c> <c>{ invoiceNo, invoiceDate, deliveryNoteNo?, lines: [{ orderLineId, quantity, unitPrice }] }</c>
/// (finance Create — the Accountant): records the supplier's invoice and runs the 3-way match per line.
/// <list type="bullet">
///   <item>Invoiced quantity can never exceed what was received and not invoiced yet (409 exceeds_received).</item>
///   <item>Every price within the tolerance of the PO price → Matched, and posted at once as the project's supplier
///   cost (status Posted). Otherwise → Mismatch, blocked until <c>POST /api/procurement/supplier-invoices/{id}/resolve</c>.</item>
///   <item>The same supplier invoice number can't be recorded twice (409 invoice_exists).</item>
/// </list>
/// <c>…/resolve</c> <c>{ resolution: Accept | CreditNote | ReturnToSupplier, reason, creditNoteNo? }</c> (finance Approve):
/// Accept posts at the invoice price; CreditNote posts at the PO price; ReturnToSupplier posts nothing and frees the quantities.
/// </summary>
internal static class InvoiceEndpoints
{
    internal sealed record LineRequest(int? OrderLineId, decimal? Quantity, decimal? UnitPrice);

    internal sealed record Request(string? InvoiceNo, string? InvoiceDate, string? DeliveryNoteNo, IReadOnlyList<LineRequest>? Lines);

    internal sealed record ResolveRequest(string? Resolution, string? Reason, string? CreditNoteNo, string? RowVersion);

    public static void Map(IEndpointRouteBuilder api)
    {
        api.MapPost("/procurement/purchase-orders/{orderId:int}/invoices", RecordAsync)
            .RequirePermission(ProcurementPermissions.Finance, PermissionAction.Create)
            .WithName("RecordSupplierInvoice")
            .WithSummary("Record a supplier invoice and run the 3-way match (finance Create). 409 exceeds_received, invoice_exists");

        api.MapPost("/procurement/supplier-invoices/{invoiceId:int}/resolve", ResolveAsync)
            .RequirePermission(ProcurementPermissions.Finance, PermissionAction.Approve)
            .WithName("ResolveSupplierInvoice")
            .WithSummary("Resolve a 3-way-match mismatch: accept, credit note or return to supplier (finance Approve)");
    }

    internal static async Task<Results<Created<InvoiceView>, ValidationProblem, ProblemHttpResult>> RecordAsync(
        int orderId, Request request, HttpContext httpContext, ProcurementDbContext db, IProjectAccess access, IInvoicePosting posting,
        ProcurementViews views, CompanyClock clock, ProcurementLog log, CancellationToken cancellationToken)
    {
        if (ProcurementCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var invoiceNo = Input.RequiredText(request.InvoiceNo, ProcurementColumns.ReferenceMaxLength, errors, "invoiceNo", "The invoice number");
        var invoiceDate = Input.RequiredDate(request.InvoiceDate, errors, "invoiceDate", "The invoice date");
        if (invoiceDate > clock.Today)
        {
            errors.Add("invoiceDate", "The invoice date can't be in the future.");
        }

        var deliveryNote = Input.OptionalText(request.DeliveryNoteNo, ProcurementColumns.ReferenceMaxLength, errors, "deliveryNoteNo", "The delivery note number");
        var lines = request.Lines ?? [];
        if (lines.Count == 0)
        {
            errors.Add("lines", "Invoice at least one line.");
        }

        for (var index = 0; index < lines.Count; index++)
        {
            if (lines[index].OrderLineId is not > 0)
            {
                errors.Add($"lines[{index}].orderLineId", "The order line is required.");
            }

            NumberInput.RequiredQuantity(lines[index].Quantity, errors, $"lines[{index}].quantity", "The quantity");
            NumberInput.RequiredMoney(lines[index].UnitPrice, errors, $"lines[{index}].unitPrice", "The unit price");
        }

        if (lines.Select(line => line.OrderLineId).Distinct().Count() != lines.Count)
        {
            errors.Add("lines", "Each order line appears once per invoice.");
        }

        if (errors.HasErrors)
        {
            return ProcurementProblems.Validation(errors);
        }

        var order = await db.PurchaseOrders.AsNoTracking().Include(candidate => candidate.Lines).SingleOrDefaultAsync(candidate => candidate.Id == orderId, cancellationToken);
        var project = await DocumentScope.ProjectAsync(access, httpContext.User, order?.ProjectId, cancellationToken);
        if (order is null || project is null)
        {
            return ProcurementProblems.NotFound("purchase order");
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

        if (order.Status == PurchaseOrderStatus.Cancelled)
        {
            return ProcurementProblems.WrongStatus("The order is cancelled.");
        }

        var lineIds = order.Lines.Select(line => line.Id).ToList();
        var received = await ChainQuantities.ReceivedAsync(db, lineIds, cancellationToken);
        var invoiced = await ChainQuantities.InvoicedAsync(db, lineIds, cancellationToken);
        var policy = await db.Policies.AsNoTracking().SingleAsync(cancellationToken);

        var invoiceLines = new List<SupplierInvoiceLine>();
        foreach (var line in lines)
        {
            var orderLine = order.Lines.SingleOrDefault(candidate => candidate.Id == line.OrderLineId);
            if (orderLine is null)
            {
                return ProcurementProblems.Validation("lines", $"Line {line.OrderLineId} isn't on this order.");
            }

            // Never pay for what didn't arrive: a hard rule, not a mismatch to review.
            if (line.Quantity > ProcurementRules.Invoiceable(received.GetValueOrDefault(orderLine.Id), invoiced.GetValueOrDefault(orderLine.Id)))
            {
                return ProcurementProblems.MoreThanReceived(orderLine.Description);
            }

            invoiceLines.Add(new SupplierInvoiceLine
            {
                OrderLineId = orderLine.Id,
                Quantity = line.Quantity!.Value,
                UnitPrice = line.UnitPrice!.Value,
                OrderUnitPrice = orderLine.UnitPrice,
                PriceMatches = ProcurementRules.PriceMatches(orderLine.UnitPrice, line.UnitPrice.Value, policy.PriceTolerancePercent),
            });
        }

        var matched = invoiceLines.All(line => line.PriceMatches);
        var created = new SupplierInvoice
        {
            OrderId = order.Id,
            ProjectId = order.ProjectId,
            SupplierId = order.SupplierId,
            InvoiceNo = invoiceNo!,
            InvoiceDate = invoiceDate!.Value,
            DeliveryNoteNo = deliveryNote,
            Total = invoiceLines.Sum(line => ProcurementRules.Amount(line.Quantity, line.UnitPrice)),
            Status = matched ? SupplierInvoiceStatus.Matched : SupplierInvoiceStatus.Mismatch,
            CreatedAt = clock.UtcNow,
            CreatedBy = caller.UserId,
        };
        created.Lines.AddRange(invoiceLines);
        db.SupplierInvoices.Add(created);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, SupplierInvoiceConfiguration.SupplierNumberIndex))
        {
            return ProcurementProblems.DuplicateInvoice();
        }

        if (matched && await PostAsync(db, posting, created, order, priceFromOrder: false, caller.UserId, clock.UtcNow, cancellationToken) is { } refused)
        {
            await transaction.RollbackAsync(cancellationToken);
            return refused;
        }

        await transaction.CommitAsync(cancellationToken);
        log.InvoiceRecorded(caller.UserId, order.Id, created.Id, created.Status.ToString());
        return TypedResults.Created($"/api/procurement/supplier-invoices/{created.Id}", (await views.InvoiceAsync(created.Id, caller.SeesMoney, cancellationToken))!);
    }

    internal static async Task<Results<Ok<InvoiceView>, ValidationProblem, ProblemHttpResult>> ResolveAsync(
        int invoiceId, ResolveRequest request, HttpContext httpContext, ProcurementDbContext db, IProjectAccess access, IInvoicePosting posting,
        ProcurementViews views, CompanyClock clock, ProcurementLog log, CancellationToken cancellationToken)
    {
        if (ProcurementCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var resolution = Input.RequiredName<InvoiceResolution>(request.Resolution, errors, "resolution");
        var reason = Input.RequiredText(request.Reason, ProcurementColumns.NoteMaxLength, errors, "reason", "The reason");
        var creditNote = resolution == InvoiceResolution.CreditNote
            ? Input.RequiredText(request.CreditNoteNo, ProcurementColumns.ReferenceMaxLength, errors, "creditNoteNo", "The credit note number")
            : null;
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (errors.HasErrors)
        {
            return ProcurementProblems.Validation(errors);
        }

        var invoice = await db.SupplierInvoices.Include(candidate => candidate.Lines).SingleOrDefaultAsync(candidate => candidate.Id == invoiceId, cancellationToken);
        var project = await DocumentScope.ProjectAsync(access, httpContext.User, invoice?.ProjectId, cancellationToken);
        if (invoice is null || project is null)
        {
            return ProcurementProblems.NotFound("supplier invoice");
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

        if (invoice.Status != SupplierInvoiceStatus.Mismatch)
        {
            return ProcurementProblems.WrongStatus($"Only a mismatched invoice is resolved. This one is {invoice.Status}.");
        }

        var now = clock.UtcNow;
        invoice.Resolve(resolution!.Value, reason!, creditNote, caller.UserId, now);
        db.Entry(invoice).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        if (resolution != InvoiceResolution.ReturnToSupplier)
        {
            var order = await db.PurchaseOrders.AsNoTracking().Include(candidate => candidate.Lines).SingleAsync(candidate => candidate.Id == invoice.OrderId, cancellationToken);
            if (await PostAsync(db, posting, invoice, order, priceFromOrder: resolution == InvoiceResolution.CreditNote, caller.UserId, now, cancellationToken) is { } refused)
            {
                await transaction.RollbackAsync(cancellationToken);
                return refused;
            }
        }

        await transaction.CommitAsync(cancellationToken);
        log.InvoiceResolved(caller.UserId, invoice.Id, resolution.Value.ToString());
        return TypedResults.Ok((await views.InvoiceAsync(invoice.Id, caller.SeesMoney, cancellationToken))!);
    }

    /// <summary>
    /// Posts the invoice into the project's supplier cost (Projects, same transaction) and marks it Posted. With a
    /// credit note the lines are posted at the PO price: the supplier credits the rest.
    /// </summary>
    private static async Task<ProblemHttpResult?> PostAsync(
        ProcurementDbContext db, IInvoicePosting posting, SupplierInvoice invoice, PurchaseOrder order, bool priceFromOrder, int userId, DateTime now,
        CancellationToken cancellationToken)
    {
        var orderLines = order.Lines.ToDictionary(line => line.Id);
        try
        {
            await posting.PostAsync(
                new PostedInvoice(
                    invoice.ProjectId,
                    invoice.SupplierId,
                    invoice.InvoiceNo,
                    invoice.InvoiceDate,
                    invoice.DeliveryNoteNo,
                    $"{invoice.Number} / {order.Number}",
                    userId,
                    invoice.Lines.OrderBy(line => line.Id).Select(line => new PostedInvoiceLine(
                        line.Id,
                        orderLines[line.OrderLineId].CatalogItemId,
                        orderLines[line.OrderLineId].ProjectItemId,
                        orderLines[line.OrderLineId].Description,
                        orderLines[line.OrderLineId].Unit,
                        line.Quantity,
                        priceFromOrder ? line.OrderUnitPrice : line.UnitPrice)).ToList()),
                SharedTransaction.Of(db),
                cancellationToken);
        }
        catch (InvoiceNotPostableException exception)
        {
            return ProcurementProblems.CannotPost(exception.Message);
        }

        invoice.MarkPosted(userId, now);
        await db.SaveChangesAsync(cancellationToken);
        return null;
    }
}
