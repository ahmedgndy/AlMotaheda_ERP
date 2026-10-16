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

namespace AlMotaheda.Modules.Procurement.Features.Requisitions;

internal sealed record RequisitionSummary(int Id, string Number, int ProjectId, string Status, DateOnly? NeededBy, int LineCount, DateTime CreatedAt, int CreatedBy);

/// <summary><c>GET /api/procurement/requisitions</c> and <c>GET /api/procurement/requisitions/{id}</c> (procurement View).</summary>
internal static class ReadRequisitions
{
    internal sealed record Response(IReadOnlyList<RequisitionSummary> Items, int Page, int PageSize, int TotalCount);

    public static void Map(IEndpointRouteBuilder api)
    {
        api.MapGet("/procurement/requisitions", ListAsync)
            .RequirePermission(ProcurementPermissions.Module, PermissionAction.View)
            .WithName("ListRequisitions")
            .WithSummary("List purchase requisitions, newest first (status, projectId; paging)");

        api.MapGet("/procurement/requisitions/{requisitionId:int}", GetAsync)
            .RequirePermission(ProcurementPermissions.Module, PermissionAction.View)
            .WithName("GetRequisition")
            .WithSummary("Get one purchase requisition with its lines and approval history");
    }

    internal static async Task<Results<Ok<Response>, ValidationProblem, ProblemHttpResult>> ListAsync(
        HttpContext httpContext, ProcurementDbContext db, IVisibleProjects projects, CancellationToken cancellationToken,
        string? status = null, int? projectId = null, int page = 1, int pageSize = Paging.DefaultPageSize)
    {
        if (ProcurementCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        Paging.Validate(page, pageSize, errors);
        var wanted = Input.OptionalName<RequisitionStatus>(status, errors, "status");
        if (errors.HasErrors)
        {
            return ProcurementProblems.Validation(errors);
        }

        var query = db.Requisitions.AsNoTracking();
        if (await caller.VisibleProjectsAsync(projects, cancellationToken) is { } visible)
        {
            query = query.Where(requisition => visible.Contains(requisition.ProjectId));
        }

        if (wanted is { } wantedStatus)
        {
            query = query.Where(requisition => requisition.Status == wantedStatus);
        }

        if (projectId is { } wantedProject)
        {
            query = query.Where(requisition => requisition.ProjectId == wantedProject);
        }

        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderByDescending(requisition => requisition.Id).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(requisition => new RequisitionSummary(requisition.Id, requisition.Number, requisition.ProjectId, requisition.Status.ToString(),
                requisition.NeededBy, requisition.Lines.Count, requisition.CreatedAt, requisition.CreatedBy))
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(new Response(rows, page, pageSize, total));
    }

    internal static async Task<Results<Ok<RequisitionView>, ProblemHttpResult>> GetAsync(
        int requisitionId, HttpContext httpContext, ProcurementDbContext db, IProjectAccess access, ProcurementViews views, CancellationToken cancellationToken)
    {
        if (ProcurementCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var projectId = await db.Requisitions.AsNoTracking().Where(requisition => requisition.Id == requisitionId)
            .Select(requisition => (int?)requisition.ProjectId).SingleOrDefaultAsync(cancellationToken);
        if (await DocumentScope.ProjectAsync(access, httpContext.User, projectId, cancellationToken) is null)
        {
            return ProcurementProblems.NotFound("requisition");
        }

        return TypedResults.Ok((await views.RequisitionAsync(requisitionId, caller.SeesMoney, cancellationToken))!);
    }
}

/// <summary>
/// The human approval steps, used when the auto-approval policy is off (procurement Approve):
/// <c>POST /api/procurement/requisitions/{id}/approve</c> <c>{ note? }</c> and <c>/reject</c> <c>{ reason }</c>.
/// Step 1 is the project's manager, step 2 an Accountant; nobody decides their own requisition.
/// <c>POST /{id}/cancel</c> <c>{ reason }</c>: the requester, Purchasing (procurement Edit) or an Executive, before
/// anything is ordered.
/// </summary>
internal static class DecideRequisition
{
    internal sealed record Request(string? Note, string? Reason, string? RowVersion);

    private enum Decision
    {
        Approve,
        Reject,
        Cancel,
    }

    public static void Map(IEndpointRouteBuilder api)
    {
        api.MapPost("/procurement/requisitions/{requisitionId:int}/approve", (int requisitionId, Request request, HttpContext httpContext, ProcurementDbContext db,
                IProjectAccess access, ProcurementViews views, CompanyClock clock, ProcurementLog log, CancellationToken cancellationToken) =>
                HandleAsync(requisitionId, request, Decision.Approve, httpContext, db, access, views, clock, log, cancellationToken))
            .RequirePermission(ProcurementPermissions.Module, PermissionAction.Approve)
            .WithName("ApproveRequisition")
            .WithSummary("Approve a requisition: the project manager first, then an Accountant. 409 own_record, not_project_manager, accountant_step");

        api.MapPost("/procurement/requisitions/{requisitionId:int}/reject", (int requisitionId, Request request, HttpContext httpContext, ProcurementDbContext db,
                IProjectAccess access, ProcurementViews views, CompanyClock clock, ProcurementLog log, CancellationToken cancellationToken) =>
                HandleAsync(requisitionId, request, Decision.Reject, httpContext, db, access, views, clock, log, cancellationToken))
            .RequirePermission(ProcurementPermissions.Module, PermissionAction.Approve)
            .WithName("RejectRequisition")
            .WithSummary("Reject a requisition with a reason, at either approval step");

        api.MapPost("/procurement/requisitions/{requisitionId:int}/cancel", (int requisitionId, Request request, HttpContext httpContext, ProcurementDbContext db,
                IProjectAccess access, ProcurementViews views, CompanyClock clock, ProcurementLog log, CancellationToken cancellationToken) =>
                HandleAsync(requisitionId, request, Decision.Cancel, httpContext, db, access, views, clock, log, cancellationToken))
            .RequirePermission(ProcurementPermissions.Module, PermissionAction.View)
            .WithName("CancelRequisition")
            .WithSummary("Cancel a requisition before anything is ordered (requester, Purchasing or Executive)");
    }

    private static async Task<Results<Ok<RequisitionView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int requisitionId, Request request, Decision decision, HttpContext httpContext, ProcurementDbContext db, IProjectAccess access,
        ProcurementViews views, CompanyClock clock, ProcurementLog log, CancellationToken cancellationToken)
    {
        if (ProcurementCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var note = decision == Decision.Approve
            ? Input.OptionalText(request.Note, ProcurementColumns.NoteMaxLength, errors, "note", "The note")
            : Input.RequiredText(request.Reason, ProcurementColumns.NoteMaxLength, errors, "reason", "The reason");
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (errors.HasErrors)
        {
            return ProcurementProblems.Validation(errors);
        }

        var requisition = await db.Requisitions.Include(candidate => candidate.Events).SingleOrDefaultAsync(candidate => candidate.Id == requisitionId, cancellationToken);
        var project = await DocumentScope.ProjectAsync(access, httpContext.User, requisition?.ProjectId, cancellationToken);
        if (requisition is null || project is null)
        {
            return ProcurementProblems.NotFound("requisition");
        }

        if (project.WritesBlocked)
        {
            return ProcurementProblems.CompletedProject();
        }

        var now = clock.UtcNow;
        if (decision == Decision.Cancel)
        {
            var mayCancel = requisition.CreatedBy == caller.UserId || caller.IsExecutive
                            || caller.Can(ProcurementPermissions.Module, PermissionAction.Edit);
            if (!mayCancel)
            {
                return ApiProblem.ForbiddenResult();
            }

            if (requisition.Status is not (RequisitionStatus.Submitted or RequisitionStatus.ManagerApproved or RequisitionStatus.Approved))
            {
                return ProcurementProblems.WrongStatus($"A {requisition.Status} requisition can't be cancelled.");
            }

            var ordered = await db.PurchaseOrders.AnyAsync(order => order.RequisitionId == requisitionId && order.Status != PurchaseOrderStatus.Cancelled, cancellationToken);
            if (ordered)
            {
                return ProcurementProblems.WrongStatus("Something is already ordered on this requisition: cancel the purchase orders first.");
            }

            requisition.Cancel(caller.UserId, now, note!);
        }
        else
        {
            if (requisition.Status is not (RequisitionStatus.Submitted or RequisitionStatus.ManagerApproved))
            {
                return ProcurementProblems.WrongStatus($"Only a requisition waiting for approval can be decided. This one is {requisition.Status}.");
            }

            if (requisition.CreatedBy == caller.UserId)
            {
                return ProcurementProblems.OwnRequisition();
            }

            // Step 1: the project's manager. Step 2: an Accountant (decision: Project Manager → Accountant).
            if (requisition.Status == RequisitionStatus.Submitted && !project.CallerIsManager)
            {
                return ProcurementProblems.ManagerFirst();
            }

            if (requisition.Status == RequisitionStatus.ManagerApproved && !caller.IsAccountant)
            {
                return ProcurementProblems.AccountantSecond();
            }

            if (decision == Decision.Approve)
            {
                requisition.Approve(caller.UserId, now, note);
            }
            else
            {
                requisition.Reject(caller.UserId, now, note!);
            }
        }

        db.Entry(requisition).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        log.RequisitionMoved(caller.UserId, requisition.Id, requisition.Status.ToString());
        return TypedResults.Ok((await views.RequisitionAsync(requisition.Id, caller.SeesMoney, cancellationToken))!);
    }
}

/// <summary>
/// <c>GET /api/procurement/requisitions/{id}/chain</c> (procurement View): the whole trail from the requisition to
/// its quotations, orders, receipts, invoices and the project supply records they became (to pay them).
/// </summary>
internal static class GetRequisitionChain
{
    internal sealed record ChainDocument(int Id, string Number, string Status);

    internal sealed record ChainOrder(int Id, string Number, string Status, IReadOnlyList<ChainDocument> Receipts, IReadOnlyList<ChainInvoice> Invoices);

    internal sealed record ChainInvoice(int Id, string Number, string Status, IReadOnlyList<int> SupplyRecordIds);

    internal sealed record Response(ChainDocument Requisition, ChainDocument? QuotationRequest, IReadOnlyList<ChainOrder> Orders);

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/procurement/requisitions/{requisitionId:int}/chain", HandleAsync)
            .RequirePermission(ProcurementPermissions.Module, PermissionAction.View)
            .WithName("GetRequisitionChain")
            .WithSummary("The document chain PR → quotations → POs → receipts → invoices → supply records");

    internal static async Task<Results<Ok<Response>, ProblemHttpResult>> HandleAsync(
        int requisitionId, HttpContext httpContext, ProcurementDbContext db, IProjectAccess access, IInvoicePosting posting, CancellationToken cancellationToken)
    {
        var requisition = await db.Requisitions.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == requisitionId, cancellationToken);
        if (requisition is null || await DocumentScope.ProjectAsync(access, httpContext.User, requisition.ProjectId, cancellationToken) is null)
        {
            return ProcurementProblems.NotFound("requisition");
        }

        var quotationRequest = await db.QuotationRequests.AsNoTracking().Where(request => request.RequisitionId == requisitionId)
            .Select(request => new ChainDocument(request.Id, request.Number, request.Status.ToString())).SingleOrDefaultAsync(cancellationToken);
        var orders = await db.PurchaseOrders.AsNoTracking().Where(order => order.RequisitionId == requisitionId).OrderBy(order => order.Id).ToListAsync(cancellationToken);
        var orderIds = orders.Select(order => order.Id).ToList();
        var receipts = await db.GoodsReceipts.AsNoTracking().Where(receipt => orderIds.Contains(receipt.OrderId)).OrderBy(receipt => receipt.Id).ToListAsync(cancellationToken);
        var invoices = await db.SupplierInvoices.AsNoTracking().Include(invoice => invoice.Lines).Where(invoice => orderIds.Contains(invoice.OrderId))
            .OrderBy(invoice => invoice.Id).ToListAsync(cancellationToken);
        var posted = await posting.StatesAsync(invoices.SelectMany(invoice => invoice.Lines).Select(line => line.Id).ToList(), cancellationToken);

        return TypedResults.Ok(new Response(
            new ChainDocument(requisition.Id, requisition.Number, requisition.Status.ToString()),
            quotationRequest,
            orders.Select(order => new ChainOrder(
                order.Id, order.Number, order.Status.ToString(),
                receipts.Where(receipt => receipt.OrderId == order.Id).Select(receipt => new ChainDocument(receipt.Id, receipt.Number, "Recorded")).ToList(),
                invoices.Where(invoice => invoice.OrderId == order.Id).Select(invoice => new ChainInvoice(
                    invoice.Id, invoice.Number, invoice.Status.ToString(),
                    invoice.Lines.Where(line => posted.ContainsKey(line.Id)).Select(line => posted[line.Id].RecordId).ToList())).ToList())).ToList()));
    }
}
