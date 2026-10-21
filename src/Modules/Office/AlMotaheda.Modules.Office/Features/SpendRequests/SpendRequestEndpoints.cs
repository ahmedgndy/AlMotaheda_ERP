using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Office.Common;
using AlMotaheda.Modules.Office.Data;
using AlMotaheda.Modules.Office.Data.Configurations;
using AlMotaheda.Modules.Office.Features.Alerts;
using AlMotaheda.Modules.Office.Features.Attachments;
using AlMotaheda.Modules.Office.Features.Expenses;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Office.Features.SpendRequests;

internal sealed record OfficeSpendActions(bool Approve, bool Reject, bool Cancel, bool WaiveAfterEvidence, bool RecordExpense);

internal sealed record OfficeSpendView(
    int Id,
    int CustodyId,
    string CustodyNameAr,
    string CustodyNameEn,
    string Kind,
    string Title,
    string Description,
    string? Location,
    string? Asset,
    string? Supplier,
    decimal? EstimatedAmount,
    bool IsEmergency,
    string? EmergencyReason,
    string Status,
    OfficePersonRef? RequestedBy,
    DateTime RequestedAt,
    OfficePersonRef? ReviewedBy,
    DateTime? ReviewedAt,
    string? ReviewNote,
    string? AfterEvidenceWaivedReason,
    int? ExpenseId,
    decimal? FinalAmount,
    DateTime? CompletedAt,
    IReadOnlyList<OfficeAttachmentView> Attachments,
    OfficeSpendActions Actions,
    string RowVersion);

/// <summary>
/// Repairs and purchases asked before the money is spent (proposed expenditures, never deductions). A repair carries
/// before-repair photos and usually a quotation; the custodian then records the final cost as one office expense with
/// its invoice and after-repair photos (<c>spendRequestId</c> on the expense), which is reviewed and posted like any
/// expense. An emergency repair already done may be recorded at once, with its reason, and gets the elevated review.
/// </summary>
internal static class OfficeSpendRequestEndpoints
{
    internal sealed record CreateRequest(
        int? CustodyId,
        string? Kind,
        string? Title,
        string? Description,
        string? Location,
        string? Asset,
        string? Supplier,
        decimal? EstimatedAmount,
        bool? IsEmergency,
        string? EmergencyReason,
        IReadOnlyList<int>? AttachmentIds,
        Guid? IdempotencyKey);

    internal sealed record SpendRow(
        int Id, int CustodyId, string Kind, string Title, decimal? EstimatedAmount, bool IsEmergency, string Status, int? ExpenseId, DateTime RequestedAt, int RequestedBy);

    internal sealed record SpendList(IReadOnlyList<SpendRow> Items, int Total, int Page, int PageSize);

    public static void Map(IEndpointRouteBuilder api)
    {
        api.MapGet("/office/spend-requests", ListAsync)
            .RequirePermission(OfficePermissions.Module, PermissionAction.View)
            .WithName("ListOfficeSpendRequests")
            .WithSummary("Repair and purchase requests the caller may see (custodyId, status, kind, page, pageSize)");

        api.MapGet("/office/spend-requests/{spendId:int}", GetAsync)
            .RequirePermission(OfficePermissions.Module, PermissionAction.View)
            .WithName("GetOfficeSpendRequest")
            .WithSummary("One repair or purchase request with its evidence");

        api.MapPost("/office/spend-requests", CreateAsync)
            .RequirePermission(OfficePermissions.Module, PermissionAction.Create)
            .WithName("CreateOfficeSpendRequest")
            .WithSummary("Ask to repair or buy something for the office (the custodian)");

        api.MapPost("/office/spend-requests/{spendId:int}/approve", (int spendId, OfficeExpenseEndpoints.DecisionRequest request, HttpContext httpContext,
                OfficeDbContext db, IUserDirectory users, CompanyClock clock, OfficeLog log, CancellationToken cancellationToken) =>
                DecideAsync(spendId, request, OfficeSpendStatus.Approved, httpContext, db, users, clock, log, cancellationToken))
            .RequirePermission(OfficePermissions.Module, PermissionAction.Approve)
            .WithName("ApproveOfficeSpendRequest")
            .WithSummary("Approve a repair or purchase request (office Approve)");

        api.MapPost("/office/spend-requests/{spendId:int}/reject", (int spendId, OfficeExpenseEndpoints.DecisionRequest request, HttpContext httpContext,
                OfficeDbContext db, IUserDirectory users, CompanyClock clock, OfficeLog log, CancellationToken cancellationToken) =>
                DecideAsync(spendId, request, OfficeSpendStatus.Rejected, httpContext, db, users, clock, log, cancellationToken))
            .RequirePermission(OfficePermissions.Module, PermissionAction.Approve)
            .WithName("RejectOfficeSpendRequest")
            .WithSummary("Reject a repair or purchase request (office Approve)");

        api.MapPost("/office/spend-requests/{spendId:int}/cancel", (int spendId, OfficeExpenseEndpoints.DecisionRequest request, HttpContext httpContext,
                OfficeDbContext db, IUserDirectory users, CompanyClock clock, OfficeLog log, CancellationToken cancellationToken) =>
                DecideAsync(spendId, request, OfficeSpendStatus.Cancelled, httpContext, db, users, clock, log, cancellationToken))
            .RequirePermission(OfficePermissions.Module, PermissionAction.View)
            .WithName("CancelOfficeSpendRequest")
            .WithSummary("Cancel a repair or purchase request that has no expense (its requester or a reviewer)");

        api.MapPost("/office/spend-requests/{spendId:int}/waive-after-evidence", WaiveAsync)
            .RequirePermission(OfficePermissions.Module, PermissionAction.Approve)
            .WithName("WaiveOfficeAfterRepairEvidence")
            .WithSummary("Let a repair's cost be recorded without after-repair photos, with a reason (office Approve)");
    }

    internal static async Task<Results<Ok<SpendList>, ValidationProblem, ProblemHttpResult>> ListAsync(
        HttpContext httpContext, OfficeDbContext db, CancellationToken cancellationToken,
        int? custodyId = null, string? status = null, string? kind = null, int page = 1, int pageSize = Paging.DefaultPageSize)
    {
        if (OfficeCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var statusFilter = Input.OptionalName<OfficeSpendStatus>(status, errors, "status");
        var kindFilter = Input.OptionalName<OfficeSpendKind>(kind, errors, "kind");
        Paging.Validate(page, pageSize, errors);
        if (errors.HasErrors)
        {
            return OfficeProblems.Validation(errors);
        }

        var query = Visible(db, caller);
        if (custodyId is { } custody)
        {
            query = query.Where(spend => spend.CustodyId == custody);
        }

        if (statusFilter is { } wantedStatus)
        {
            query = query.Where(spend => spend.Status == wantedStatus);
        }

        if (kindFilter is { } wantedKind)
        {
            query = query.Where(spend => spend.Kind == wantedKind);
        }

        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderByDescending(spend => spend.RequestedAt).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(spend => new SpendRow(spend.Id, spend.CustodyId, spend.Kind.ToString(), spend.Title, spend.EstimatedAmount, spend.IsEmergency,
                spend.Status.ToString(), spend.ExpenseId, spend.RequestedAt, spend.RequestedBy))
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(new SpendList(rows, total, page, pageSize));
    }

    internal static async Task<Results<Ok<OfficeSpendView>, ProblemHttpResult>> GetAsync(
        int spendId, HttpContext httpContext, OfficeDbContext db, IUserDirectory users, CancellationToken cancellationToken)
    {
        if (OfficeCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        return await ViewAsync(db, users, caller, spendId, cancellationToken) is { } view ? TypedResults.Ok(view) : OfficeProblems.SpendNotFound();
    }

    internal static async Task<Results<Created<OfficeSpendView>, Ok<OfficeSpendView>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        CreateRequest request,
        HttpContext httpContext,
        OfficeDbContext db,
        IUserDirectory users,
        OfficeAlerts alerts,
        CompanyClock clock,
        OfficeLog log,
        CancellationToken cancellationToken)
    {
        if (OfficeCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        if (request.CustodyId is null)
        {
            errors.Add("custodyId", "Choose the custody.");
        }

        var kind = Input.RequiredName<OfficeSpendKind>(request.Kind, errors, "kind");
        var title = Input.RequiredText(request.Title, OfficeLengths.Name, errors, "title", "The title");
        var description = Input.RequiredText(request.Description, OfficeLengths.Note, errors, "description", "The description");
        var location = Input.OptionalText(request.Location, OfficeLengths.Name, errors, "location", "The location");
        var asset = Input.OptionalText(request.Asset, OfficeLengths.Name, errors, "asset", "The asset");
        var supplier = Input.OptionalText(request.Supplier, OfficeLengths.Vendor, errors, "supplier", "The supplier");
        var estimate = NumberInput.OptionalMoney(request.EstimatedAmount, errors, "estimatedAmount", "The estimated cost");
        var emergency = request.IsEmergency == true;
        var emergencyReason = Input.OptionalText(request.EmergencyReason, OfficeLengths.Note, errors, "emergencyReason", "The emergency reason");
        if (emergency && emergencyReason is null)
        {
            errors.Add("emergencyReason", "Explain why the repair could not wait for approval.");
        }

        if (emergency && kind == OfficeSpendKind.Purchase)
        {
            errors.Add("isEmergency", "Only a repair can be recorded as an emergency.");
        }

        var key = OfficeInput.Key(request.IdempotencyKey, errors);
        if (errors.HasErrors)
        {
            return OfficeProblems.Validation(errors);
        }

        if (await FindRepeatAsync(db, users, caller, key!.Value, request.CustodyId!.Value, cancellationToken) is { } repeat)
        {
            return repeat;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var custody = await db.Custodies.SingleOrDefaultAsync(candidate => candidate.Id == request.CustodyId, cancellationToken);
        if (custody is null || !caller.MaySee(custody))
        {
            return OfficeProblems.CustodyNotFound();
        }

        if (!caller.Holds(custody))
        {
            return OfficeProblems.NotTheHolder();
        }

        if (!custody.IsActive)
        {
            return OfficeProblems.Inactive(custody.Status.ToString());
        }

        var (attachments, refused) = await AttachmentBinding.LoadAsync(db, (request.AttachmentIds ?? []).Distinct().ToList(), caller.UserId,
            OfficeAttachmentOwners.SpendRequest, null, cancellationToken);
        if (refused is not null)
        {
            return refused;
        }

        // A repair is shown before it is fixed (or, for an emergency, as it was found).
        if (kind == OfficeSpendKind.Repair && !attachments.Any(attachment => attachment.Purpose == OfficeAttachmentPurpose.BeforeRepair))
        {
            return OfficeProblems.NoEvidence("Take at least one photo of the damage before the repair.");
        }

        var spend = new OfficeSpendRequest
        {
            CustodyId = custody.Id,
            Kind = kind!.Value,
            Title = title!,
            Description = description!,
            Location = location,
            Asset = asset,
            Supplier = supplier,
            EstimatedAmount = estimate,
            IsEmergency = emergency,
            EmergencyReason = emergency ? emergencyReason : null,
            IdempotencyKey = key.Value,
            RequestedBy = caller.UserId,
            RequestedAt = clock.UtcNow,
        };
        db.SpendRequests.Add(spend);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, OfficeSpendRequestConfiguration.KeyIndex))
        {
            await transaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            return await FindRepeatAsync(db, users, caller, key.Value, custody.Id, cancellationToken) ?? OfficeProblems.KeyUsedElsewhere();
        }

        AttachmentBinding.Bind(attachments, OfficeAttachmentOwners.SpendRequest, spend.Id, clock.UtcNow);
        alerts.Add<OfficeSpendRequestedAlert>(db, await alerts.ReviewersAsync(caller.UserId, cancellationToken), custody.Id,
            OfficeAttachmentOwners.SpendRequest, spend.Id, clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        log.SpendMoved(caller.UserId, spend.Id, spend.Status.ToString());

        return TypedResults.Created($"/api/office/spend-requests/{spend.Id}", (await ViewAsync(db, users, caller, spend.Id, cancellationToken))!);
    }

    internal static async Task<Results<Ok<OfficeSpendView>, ValidationProblem, ProblemHttpResult>> DecideAsync(
        int spendId,
        OfficeExpenseEndpoints.DecisionRequest request,
        OfficeSpendStatus target,
        HttpContext httpContext,
        OfficeDbContext db,
        IUserDirectory users,
        CompanyClock clock,
        OfficeLog log,
        CancellationToken cancellationToken)
    {
        if (OfficeCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var note = target == OfficeSpendStatus.Approved
            ? Input.OptionalText(request.Note, OfficeLengths.Note, errors, "note", "The note")
            : target == OfficeSpendStatus.Rejected ? OfficeInput.Reason(request.Note, errors) : Input.OptionalText(request.Note, OfficeLengths.Note, errors, "note", "The note");
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (errors.HasErrors)
        {
            return OfficeProblems.Validation(errors);
        }

        var spend = await db.SpendRequests.SingleOrDefaultAsync(candidate => candidate.Id == spendId, cancellationToken);
        var custody = spend is null ? null : await db.Custodies.AsNoTracking().SingleAsync(candidate => candidate.Id == spend.CustodyId, cancellationToken);
        if (spend is null || custody is null || !caller.MaySee(custody))
        {
            return OfficeProblems.SpendNotFound();
        }

        var own = spend.RequestedBy == caller.UserId || custody.HolderUserId == caller.UserId;
        if (target == OfficeSpendStatus.Cancelled ? !(spend.RequestedBy == caller.UserId || caller.IsReviewer) : own)
        {
            return target == OfficeSpendStatus.Cancelled ? ApiProblem.ForbiddenResult() : OfficeProblems.ReviewerIsSubmitter();
        }

        if (spend.Status == target)
        {
            return TypedResults.Ok((await ViewAsync(db, users, caller, spendId, cancellationToken))!);
        }

        if (!spend.RowVersion.SequenceEqual(rowVersion!))
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        if (!spend.CanMoveTo(target) || (target == OfficeSpendStatus.Approved && spend.Status != OfficeSpendStatus.Submitted))
        {
            return OfficeProblems.WrongStatus($"A {spend.Status} request can't be moved to {target}.");
        }

        spend.MoveTo(target);
        spend.ReviewedBy = caller.UserId;
        spend.ReviewedAt = clock.UtcNow;
        spend.ReviewNote = note;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        log.SpendMoved(caller.UserId, spend.Id, target.ToString());
        return TypedResults.Ok((await ViewAsync(db, users, caller, spend.Id, cancellationToken))!);
    }

    internal static async Task<Results<Ok<OfficeSpendView>, ValidationProblem, ProblemHttpResult>> WaiveAsync(
        int spendId,
        OfficeExpenseEndpoints.DecisionRequest request,
        HttpContext httpContext,
        OfficeDbContext db,
        IUserDirectory users,
        CompanyClock clock,
        OfficeLog log,
        CancellationToken cancellationToken)
    {
        if (OfficeCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var reason = OfficeInput.Reason(request.Note, errors);
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (errors.HasErrors)
        {
            return OfficeProblems.Validation(errors);
        }

        var spend = await db.SpendRequests.SingleOrDefaultAsync(candidate => candidate.Id == spendId, cancellationToken);
        var custody = spend is null ? null : await db.Custodies.AsNoTracking().SingleAsync(candidate => candidate.Id == spend.CustodyId, cancellationToken);
        if (spend is null || custody is null)
        {
            return OfficeProblems.SpendNotFound();
        }

        if (spend.RequestedBy == caller.UserId || custody.HolderUserId == caller.UserId)
        {
            return OfficeProblems.ReviewerIsSubmitter();
        }

        if (!spend.RowVersion.SequenceEqual(rowVersion!))
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        if (spend.Kind != OfficeSpendKind.Repair || spend.Status is not (OfficeSpendStatus.Submitted or OfficeSpendStatus.Approved))
        {
            return OfficeProblems.WrongStatus("Only an open repair request can go without after-repair photos.");
        }

        spend.AfterEvidenceWaivedReason = reason;
        spend.AfterEvidenceWaivedBy = caller.UserId;
        spend.AfterEvidenceWaivedAt = clock.UtcNow;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        log.SpendMoved(caller.UserId, spend.Id, "AfterEvidenceWaived");
        return TypedResults.Ok((await ViewAsync(db, users, caller, spend.Id, cancellationToken))!);
    }

    internal static IQueryable<OfficeSpendRequest> Visible(OfficeDbContext db, OfficeCaller caller) =>
        caller.SeesAll
            ? db.SpendRequests.AsNoTracking()
            : db.SpendRequests.AsNoTracking().Where(spend => db.Custodies.Any(custody => custody.Id == spend.CustodyId && custody.HolderUserId == caller.UserId));

    internal static async Task<OfficeSpendView?> ViewAsync(OfficeDbContext db, IUserDirectory users, OfficeCaller caller, int spendId, CancellationToken cancellationToken)
    {
        var spend = await Visible(db, caller).SingleOrDefaultAsync(candidate => candidate.Id == spendId, cancellationToken);
        if (spend is null)
        {
            return null;
        }

        var custody = await db.Custodies.AsNoTracking().SingleAsync(candidate => candidate.Id == spend.CustodyId, cancellationToken);
        var finalAmount = spend.ExpenseId is { } expenseId
            ? await db.Expenses.Where(expense => expense.Id == expenseId).Select(expense => (decimal?)expense.Amount).SingleOrDefaultAsync(cancellationToken)
            : null;
        var people = await users.FindAsync(new int?[] { spend.RequestedBy, spend.ReviewedBy }.Where(id => id is not null).Select(id => id!.Value).Distinct().ToList(), cancellationToken);
        var attachments = await OfficeAttachmentView.OfAsync(db, OfficeAttachmentOwners.SpendRequest, spend.Id, cancellationToken);
        var reviewer = caller.IsReviewer && spend.RequestedBy != caller.UserId && custody.HolderUserId != caller.UserId;
        var open = spend.Status is OfficeSpendStatus.Submitted or OfficeSpendStatus.Approved;

        return new OfficeSpendView(
            spend.Id, spend.CustodyId, custody.NameAr, custody.NameEn, spend.Kind.ToString(), spend.Title, spend.Description, spend.Location, spend.Asset,
            spend.Supplier, spend.EstimatedAmount, spend.IsEmergency, spend.EmergencyReason, spend.Status.ToString(),
            OfficePersonRef.From(spend.RequestedBy, people), spend.RequestedAt, OfficePersonRef.From(spend.ReviewedBy, people), spend.ReviewedAt, spend.ReviewNote,
            spend.AfterEvidenceWaivedReason, spend.ExpenseId, finalAmount, spend.CompletedAt, attachments,
            new OfficeSpendActions(
                reviewer && spend.Status == OfficeSpendStatus.Submitted,
                reviewer && spend.Status == OfficeSpendStatus.Submitted,
                (spend.RequestedBy == caller.UserId || caller.IsReviewer) && open,
                reviewer && open && spend.Kind == OfficeSpendKind.Repair && spend.AfterEvidenceWaivedReason is null,
                custody.HolderUserId == caller.UserId && custody.IsActive
                && (spend.Status == OfficeSpendStatus.Approved || (spend.Status == OfficeSpendStatus.Submitted && spend.IsEmergency))),
            RowVersions.Format(spend.RowVersion));
    }

    private static async Task<Results<Created<OfficeSpendView>, Ok<OfficeSpendView>, ValidationProblem, ProblemHttpResult>?> FindRepeatAsync(
        OfficeDbContext db, IUserDirectory users, OfficeCaller caller, Guid key, int custodyId, CancellationToken cancellationToken)
    {
        var existing = await db.SpendRequests.AsNoTracking().SingleOrDefaultAsync(spend => spend.IdempotencyKey == key, cancellationToken);
        if (existing is null)
        {
            return null;
        }

        return existing.RequestedBy != caller.UserId || existing.CustodyId != custodyId
            ? OfficeProblems.KeyUsedElsewhere()
            : TypedResults.Ok((await ViewAsync(db, users, caller, existing.Id, cancellationToken))!);
    }
}
