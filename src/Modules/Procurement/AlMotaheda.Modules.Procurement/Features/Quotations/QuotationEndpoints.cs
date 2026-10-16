using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Procurement.Common;
using AlMotaheda.Modules.Procurement.Data;
using AlMotaheda.Modules.Procurement.Data.Configurations;
using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Procurement.Features.Quotations;

/// <summary>
/// Quotations on the Executive's request (12-procurement-design.md §1):
/// <list type="bullet">
///   <item><c>POST /api/procurement/requisitions/{id}/quotation-request</c> <c>{ quotationsWanted?, note? }</c>:
///   Executive only, on an approved requisition with nothing ordered yet; once per requisition.</item>
///   <item><c>GET /api/procurement/quotation-requests/{id}</c> (procurement View): the set, side by side.</item>
///   <item><c>POST /api/procurement/quotation-requests/{id}/quotations</c>, <c>DELETE /api/procurement/quotations/{id}</c>,
///   <c>POST …/recommend</c> <c>{ quotationId, reason }</c>, <c>POST …/submit</c> (procurement Edit): Purchasing.</item>
///   <item><c>POST …/select</c> <c>{ quotationId, reason? }</c> (reason required unless the cheapest) and
///   <c>POST …/return</c> <c>{ note }</c>: Executive only.</item>
/// </list>
/// The Executive steps re-check the role in the database.
/// </summary>
internal static class QuotationEndpoints
{
    internal sealed record AskRequest(int? QuotationsWanted, string? Note);

    internal sealed record LineRequest(int? RequisitionLineId, decimal? UnitPrice);

    internal sealed record QuotationRequestBody(
        int? SupplierId, string? SupplierReference, int? DeliveryDays, string? PaymentTerms, string? ValidUntil, string? Notes, IReadOnlyList<LineRequest>? Lines);

    internal sealed record StepRequest(int? QuotationId, string? Reason, string? Note, string? RowVersion);

    public static void Map(IEndpointRouteBuilder api)
    {
        api.MapPost("/procurement/requisitions/{requisitionId:int}/quotation-request", AskAsync)
            .RequireRole(AccessRoles.Executive)
            .WithName("RequestQuotations")
            .WithSummary("Ask Purchasing for quotations on a requisition (Executive). 409 quotations_exist, invalid_status");

        api.MapGet("/procurement/quotation-requests/{requestId:int}", GetAsync)
            .RequirePermission(ProcurementPermissions.Module, PermissionAction.View)
            .WithName("GetQuotationRequest")
            .WithSummary("A quotation set, side by side, with the cheapest marked");

        api.MapPost("/procurement/quotation-requests/{requestId:int}/quotations", AddAsync)
            .RequirePermission(ProcurementPermissions.Module, PermissionAction.Edit)
            .WithName("AddQuotation")
            .WithSummary("Add a supplier's quotation to the set (Purchasing)");

        api.MapDelete("/procurement/quotations/{quotationId:int}", RemoveAsync)
            .RequirePermission(ProcurementPermissions.Module, PermissionAction.Edit)
            .WithName("RemoveQuotation")
            .WithSummary("Remove a quotation while the set is with Purchasing");

        api.MapPost("/procurement/quotation-requests/{requestId:int}/recommend", (int requestId, StepRequest request, HttpContext httpContext,
                ProcurementDbContext db, IProjectAccess access, IExecutiveCheck executives, ProcurementViews views, CompanyClock clock, ProcurementLog log,
                CancellationToken cancellationToken) => StepAsync(requestId, request, Step.Recommend, httpContext, db, access, executives, views, clock, log, cancellationToken))
            .RequirePermission(ProcurementPermissions.Module, PermissionAction.Edit)
            .WithName("RecommendQuotation")
            .WithSummary("Mark the recommended quotation, with a reason (Purchasing)");

        api.MapPost("/procurement/quotation-requests/{requestId:int}/submit", (int requestId, StepRequest request, HttpContext httpContext,
                ProcurementDbContext db, IProjectAccess access, IExecutiveCheck executives, ProcurementViews views, CompanyClock clock, ProcurementLog log,
                CancellationToken cancellationToken) => StepAsync(requestId, request, Step.Submit, httpContext, db, access, executives, views, clock, log, cancellationToken))
            .RequirePermission(ProcurementPermissions.Module, PermissionAction.Edit)
            .WithName("SubmitQuotations")
            .WithSummary("Send the quotation set to the Executive (Purchasing)");

        api.MapPost("/procurement/quotation-requests/{requestId:int}/select", (int requestId, StepRequest request, HttpContext httpContext,
                ProcurementDbContext db, IProjectAccess access, IExecutiveCheck executives, ProcurementViews views, CompanyClock clock, ProcurementLog log,
                CancellationToken cancellationToken) => StepAsync(requestId, request, Step.Select, httpContext, db, access, executives, views, clock, log, cancellationToken))
            .RequireRole(AccessRoles.Executive)
            .WithName("SelectQuotation")
            .WithSummary("Select a quotation (Executive); a reason is required unless it is the cheapest");

        api.MapPost("/procurement/quotation-requests/{requestId:int}/return", (int requestId, StepRequest request, HttpContext httpContext,
                ProcurementDbContext db, IProjectAccess access, IExecutiveCheck executives, ProcurementViews views, CompanyClock clock, ProcurementLog log,
                CancellationToken cancellationToken) => StepAsync(requestId, request, Step.Return, httpContext, db, access, executives, views, clock, log, cancellationToken))
            .RequireRole(AccessRoles.Executive)
            .WithName("ReturnQuotations")
            .WithSummary("Return the quotation set to Purchasing with a note (Executive)");
    }

    internal static async Task<Results<Created<QuotationRequestView>, ValidationProblem, ProblemHttpResult>> AskAsync(
        int requisitionId, AskRequest request, HttpContext httpContext, ProcurementDbContext db, IProjectAccess access, IExecutiveCheck executives,
        ProcurementViews views, CompanyClock clock, ProcurementLog log, CancellationToken cancellationToken)
    {
        if (ProcurementCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        if (request.QuotationsWanted is < 1 or > 10)
        {
            errors.Add("quotationsWanted", "quotationsWanted must be between 1 and 10, or left out.");
        }

        var note = Input.OptionalText(request.Note, ProcurementColumns.NoteMaxLength, errors, "note", "The note");
        if (errors.HasErrors)
        {
            return ProcurementProblems.Validation(errors);
        }

        if (!await caller.IsActiveExecutiveAsync(executives, cancellationToken))
        {
            return ProcurementProblems.Executive();
        }

        var requisition = await db.Requisitions.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == requisitionId, cancellationToken);
        if (requisition is null || await DocumentScope.ProjectAsync(access, httpContext.User, requisition.ProjectId, cancellationToken) is null)
        {
            return ProcurementProblems.NotFound("requisition");
        }

        if (requisition.Status != RequisitionStatus.Approved)
        {
            return ProcurementProblems.WrongStatus($"Quotations are asked for on an approved requisition. This one is {requisition.Status}.");
        }

        if (await db.PurchaseOrders.AnyAsync(order => order.RequisitionId == requisitionId && order.Status != PurchaseOrderStatus.Cancelled, cancellationToken)
            || await db.QuotationRequests.AnyAsync(candidate => candidate.RequisitionId == requisitionId, cancellationToken))
        {
            return ProcurementProblems.AlreadyOrdered();
        }

        var quotationRequest = new QuotationRequest
        {
            RequisitionId = requisitionId,
            ProjectId = requisition.ProjectId,
            QuotationsWanted = request.QuotationsWanted,
            RequestNote = note,
            RequestedAt = clock.UtcNow,
            RequestedBy = caller.UserId,
        };
        db.QuotationRequests.Add(quotationRequest);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, QuotationRequestConfiguration.RequisitionIndex))
        {
            return ProcurementProblems.AlreadyOrdered();
        }

        log.QuotationRequestMoved(caller.UserId, quotationRequest.Id, quotationRequest.Status.ToString());
        return TypedResults.Created($"/api/procurement/quotation-requests/{quotationRequest.Id}",
            (await views.QuotationRequestAsync(quotationRequest.Id, caller.SeesMoney, cancellationToken))!);
    }

    internal static async Task<Results<Ok<QuotationRequestView>, ProblemHttpResult>> GetAsync(
        int requestId, HttpContext httpContext, ProcurementDbContext db, IProjectAccess access, ProcurementViews views, CancellationToken cancellationToken)
    {
        if (ProcurementCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var projectId = await db.QuotationRequests.AsNoTracking().Where(request => request.Id == requestId)
            .Select(request => (int?)request.ProjectId).SingleOrDefaultAsync(cancellationToken);
        if (await DocumentScope.ProjectAsync(access, httpContext.User, projectId, cancellationToken) is null)
        {
            return ProcurementProblems.NotFound("quotation request");
        }

        return TypedResults.Ok((await views.QuotationRequestAsync(requestId, caller.SeesMoney, cancellationToken))!);
    }

    internal static async Task<Results<Created<QuotationRequestView>, ValidationProblem, ProblemHttpResult>> AddAsync(
        int requestId, QuotationRequestBody request, HttpContext httpContext, ProcurementDbContext db, IProjectAccess access,
        ISupplierDirectory suppliers, ProcurementViews views, CompanyClock clock, CancellationToken cancellationToken)
    {
        if (ProcurementCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        if (request.SupplierId is not > 0)
        {
            errors.Add("supplierId", "The supplier is required.");
        }

        var reference = Input.OptionalText(request.SupplierReference, ProcurementColumns.ReferenceMaxLength, errors, "supplierReference", "The supplier's reference");
        if (request.DeliveryDays is < 0 or > 3650)
        {
            errors.Add("deliveryDays", "deliveryDays must be between 0 and 3650.");
        }

        var terms = Input.OptionalText(request.PaymentTerms, ProcurementColumns.TermsMaxLength, errors, "paymentTerms", "The payment terms");
        var validUntil = Input.OptionalDate(request.ValidUntil, errors, "validUntil", "The validity date");
        var notes = Input.OptionalText(request.Notes, ProcurementColumns.NoteMaxLength, errors, "notes", "Notes");
        var lines = request.Lines ?? [];
        if (lines.Count == 0)
        {
            errors.Add("lines", "Quote at least one line.");
        }

        for (var index = 0; index < lines.Count; index++)
        {
            if (lines[index].RequisitionLineId is not > 0)
            {
                errors.Add($"lines[{index}].requisitionLineId", "The requisition line is required.");
            }

            NumberInput.RequiredMoney(lines[index].UnitPrice, errors, $"lines[{index}].unitPrice", "The unit price");
        }

        if (lines.Select(line => line.RequisitionLineId).Distinct().Count() != lines.Count)
        {
            errors.Add("lines", "Each requisition line is quoted once.");
        }

        if (errors.HasErrors)
        {
            return ProcurementProblems.Validation(errors);
        }

        var set = await db.QuotationRequests.SingleOrDefaultAsync(candidate => candidate.Id == requestId, cancellationToken);
        if (set is null || await DocumentScope.ProjectAsync(access, httpContext.User, set.ProjectId, cancellationToken) is null)
        {
            return ProcurementProblems.NotFound("quotation request");
        }

        if (!set.IsWithPurchasing)
        {
            return ProcurementProblems.WrongStatus($"Quotations are added while the set is with Purchasing. This one is {set.Status}.");
        }

        if (!(await suppliers.FindAsync([request.SupplierId!.Value], cancellationToken)).TryGetValue(request.SupplierId.Value, out var supplier) || !supplier.IsActive)
        {
            return ProcurementProblems.InactiveSupplier();
        }

        var prLines = await db.RequisitionLines.AsNoTracking().Where(line => line.RequisitionId == set.RequisitionId).ToDictionaryAsync(line => line.Id, cancellationToken);
        if (lines.Any(line => !prLines.ContainsKey(line.RequisitionLineId!.Value)))
        {
            return ProcurementProblems.Validation("lines", "Every line must be a line of this requisition.");
        }

        var quotation = new Quotation
        {
            RequestId = set.Id,
            SupplierId = supplier.Id,
            SupplierReference = reference,
            DeliveryDays = request.DeliveryDays,
            PaymentTerms = terms,
            ValidUntil = validUntil,
            Notes = notes,
            CreatedAt = clock.UtcNow,
            CreatedBy = caller.UserId,
        };
        quotation.Lines.AddRange(lines.Select(line => new QuotationLine { RequisitionLineId = line.RequisitionLineId!.Value, UnitPrice = line.UnitPrice!.Value }));
        quotation.Total = quotation.Lines.Sum(line => ProcurementRules.Amount(prLines[line.RequisitionLineId].Quantity, line.UnitPrice));
        db.Quotations.Add(quotation);
        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.Created($"/api/procurement/quotation-requests/{set.Id}", (await views.QuotationRequestAsync(set.Id, caller.SeesMoney, cancellationToken))!);
    }

    internal static async Task<Results<Ok<QuotationRequestView>, ProblemHttpResult>> RemoveAsync(
        int quotationId, HttpContext httpContext, ProcurementDbContext db, IProjectAccess access, ProcurementViews views, CancellationToken cancellationToken)
    {
        if (ProcurementCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var quotation = await db.Quotations.Include(candidate => candidate.Lines).SingleOrDefaultAsync(candidate => candidate.Id == quotationId, cancellationToken);
        var set = quotation is null ? null : await db.QuotationRequests.SingleAsync(candidate => candidate.Id == quotation.RequestId, cancellationToken);
        if (quotation is null || set is null || await DocumentScope.ProjectAsync(access, httpContext.User, set.ProjectId, cancellationToken) is null)
        {
            return ProcurementProblems.NotFound("quotation");
        }

        if (!set.IsWithPurchasing)
        {
            return ProcurementProblems.WrongStatus($"Quotations are removed while the set is with Purchasing. This one is {set.Status}.");
        }

        if (set.RecommendedQuotationId == quotationId)
        {
            set.Recommend(null, null);
        }

        db.Quotations.Remove(quotation);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok((await views.QuotationRequestAsync(set.Id, caller.SeesMoney, cancellationToken))!);
    }

    private enum Step
    {
        Recommend,
        Submit,
        Select,
        Return,
    }

    private static async Task<Results<Ok<QuotationRequestView>, ValidationProblem, ProblemHttpResult>> StepAsync(
        int requestId, StepRequest request, Step step, HttpContext httpContext, ProcurementDbContext db, IProjectAccess access, IExecutiveCheck executives,
        ProcurementViews views, CompanyClock clock, ProcurementLog log, CancellationToken cancellationToken)
    {
        if (ProcurementCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var reason = step == Step.Recommend
            ? Input.RequiredText(request.Reason, ProcurementColumns.NoteMaxLength, errors, "reason", "The reason")
            : Input.OptionalText(request.Reason, ProcurementColumns.NoteMaxLength, errors, "reason", "The reason");
        var note = step == Step.Return ? Input.RequiredText(request.Note, ProcurementColumns.NoteMaxLength, errors, "note", "The note") : null;
        if (step is Step.Recommend or Step.Select && request.QuotationId is not > 0)
        {
            errors.Add("quotationId", "The quotation is required.");
        }

        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (errors.HasErrors)
        {
            return ProcurementProblems.Validation(errors);
        }

        if (step is Step.Select or Step.Return && !await caller.IsActiveExecutiveAsync(executives, cancellationToken))
        {
            return ProcurementProblems.Executive();
        }

        var set = await db.QuotationRequests.Include(candidate => candidate.Quotations).SingleOrDefaultAsync(candidate => candidate.Id == requestId, cancellationToken);
        if (set is null || await DocumentScope.ProjectAsync(access, httpContext.User, set.ProjectId, cancellationToken) is null)
        {
            return ProcurementProblems.NotFound("quotation request");
        }

        var chosen = request.QuotationId is { } id ? set.Quotations.SingleOrDefault(quotation => quotation.Id == id) : null;
        if (step is Step.Recommend or Step.Select && chosen is null)
        {
            return ProcurementProblems.Validation("quotationId", "The quotation isn't in this set.");
        }

        var now = clock.UtcNow;
        switch (step)
        {
            case Step.Recommend when !set.IsWithPurchasing:
            case Step.Submit when !set.IsWithPurchasing:
                return ProcurementProblems.WrongStatus($"The set is {set.Status}: it isn't with Purchasing.");
            case Step.Select or Step.Return when set.Status != QuotationRequestStatus.Submitted:
                return ProcurementProblems.WrongStatus($"Only a submitted set is decided. This one is {set.Status}.");
            case Step.Recommend:
                set.Recommend(chosen!.Id, reason);
                break;
            case Step.Submit:
                if (set.Quotations.Count == 0)
                {
                    return ProcurementProblems.WrongStatus("Add at least one quotation before submitting the set.");
                }

                set.Submit(caller.UserId, now);
                break;
            case Step.Select:
                var cheapest = set.Quotations.Min(quotation => quotation.Total);
                if (chosen!.Total > cheapest && reason is null)
                {
                    return ProcurementProblems.ReasonForNotCheapest();
                }

                set.Select(chosen.Id, caller.UserId, now, reason);
                break;
            case Step.Return:
                set.Return(caller.UserId, now, note!);
                break;
        }

        db.Entry(set).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        log.QuotationRequestMoved(caller.UserId, set.Id, set.Status.ToString());
        return TypedResults.Ok((await views.QuotationRequestAsync(set.Id, caller.SeesMoney, cancellationToken))!);
    }
}
