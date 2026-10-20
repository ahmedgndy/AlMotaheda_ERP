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
using AlMotaheda.Modules.Office.Features.Ledger;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Office.Features.CashCounts;

internal sealed record OfficeCashCountView(
    int Id,
    int CustodyId,
    DateOnly CountedOn,
    OfficePersonRef? CountedBy,
    DateTime CountedAt,
    decimal RecordedBalance,
    decimal CountedAmount,
    decimal Difference,
    string? Explanation,
    string Status,
    OfficePersonRef? ReviewedBy,
    DateTime? ReviewedAt,
    string? ReviewNote,
    long? AdjustmentEntryId,
    IReadOnlyList<OfficeAttachmentView> Attachments,
    bool CanReview,
    string RowVersion);

/// <summary>
/// Cash counts (reconciliation). The server takes the recorded balance at the moment of the count, under the custody
/// lock. A match needs nothing more. A difference never changes the balance by itself: it alerts the reviewers, who
/// either accept the explanation (<c>…/accept</c>) or post the difference as an adjustment with a reason
/// (<c>…/adjust</c>). Neither can be done by the person who counted or the custodian.
/// </summary>
internal static class OfficeCashCountEndpoints
{
    internal sealed record CreateRequest(int? CustodyId, decimal? CountedAmount, string? CountedOn, string? Explanation, IReadOnlyList<int>? AttachmentIds, Guid? IdempotencyKey);

    internal sealed record CountList(IReadOnlyList<OfficeCashCountView> Items, int Total, int Page, int PageSize);

    public static void Map(IEndpointRouteBuilder api)
    {
        api.MapGet("/office/cash-counts", ListAsync)
            .RequirePermission(OfficePermissions.Module, PermissionAction.View)
            .WithName("ListOfficeCashCounts")
            .WithSummary("Cash counts the caller may see (custodyId, status, page, pageSize)");

        api.MapGet("/office/cash-counts/{countId:int}", GetAsync)
            .RequirePermission(OfficePermissions.Module, PermissionAction.View)
            .WithName("GetOfficeCashCount")
            .WithSummary("One cash count with its photos");

        api.MapPost("/office/cash-counts", CreateAsync)
            .RequirePermission(OfficePermissions.Module, PermissionAction.Create)
            .WithName("CreateOfficeCashCount")
            .WithSummary("Record the cash counted in a custody (the custodian, or a reviewer on a surprise count)");

        api.MapPost("/office/cash-counts/{countId:int}/accept", (int countId, OfficeExpenseEndpoints.DecisionRequest request, HttpContext httpContext, OfficeDbContext db,
                IUserDirectory users, OfficeAlerts alerts, CompanyClock clock, OfficeLog log, CancellationToken cancellationToken) =>
                ReviewAsync(countId, request, adjust: false, httpContext, db, users, alerts, clock, log, cancellationToken))
            .RequirePermission(OfficePermissions.Module, PermissionAction.Approve)
            .WithName("AcceptOfficeCashCount")
            .WithSummary("Accept a counted difference without changing the balance (office Approve)");

        api.MapPost("/office/cash-counts/{countId:int}/adjust", (int countId, OfficeExpenseEndpoints.DecisionRequest request, HttpContext httpContext, OfficeDbContext db,
                IUserDirectory users, OfficeAlerts alerts, CompanyClock clock, OfficeLog log, CancellationToken cancellationToken) =>
                ReviewAsync(countId, request, adjust: true, httpContext, db, users, alerts, clock, log, cancellationToken))
            .RequirePermission(OfficePermissions.Module, PermissionAction.Approve)
            .WithName("AdjustOfficeCashCount")
            .WithSummary("Post a counted difference to the ledger as an adjustment, with its reason (office Approve)");
    }

    internal static async Task<Results<Ok<CountList>, ValidationProblem, ProblemHttpResult>> ListAsync(
        HttpContext httpContext, OfficeDbContext db, IUserDirectory users, CancellationToken cancellationToken,
        int? custodyId = null, string? status = null, int page = 1, int pageSize = Paging.DefaultPageSize)
    {
        if (OfficeCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var statusFilter = Input.OptionalName<OfficeCashCountStatus>(status, errors, "status");
        Paging.Validate(page, pageSize, errors);
        if (errors.HasErrors)
        {
            return OfficeProblems.Validation(errors);
        }

        var query = Visible(db, caller);
        if (custodyId is { } custody)
        {
            query = query.Where(count => count.CustodyId == custody);
        }

        if (statusFilter is { } wanted)
        {
            query = query.Where(count => count.Status == wanted);
        }

        var total = await query.CountAsync(cancellationToken);
        var ids = await query.OrderByDescending(count => count.CountedAt).Skip((page - 1) * pageSize).Take(pageSize).Select(count => count.Id).ToListAsync(cancellationToken);
        var views = new List<OfficeCashCountView>();
        foreach (var id in ids)
        {
            views.Add((await ViewAsync(db, users, caller, id, cancellationToken))!);
        }

        return TypedResults.Ok(new CountList(views, total, page, pageSize));
    }

    internal static async Task<Results<Ok<OfficeCashCountView>, ProblemHttpResult>> GetAsync(
        int countId, HttpContext httpContext, OfficeDbContext db, IUserDirectory users, CancellationToken cancellationToken)
    {
        if (OfficeCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        return await ViewAsync(db, users, caller, countId, cancellationToken) is { } view ? TypedResults.Ok(view) : OfficeProblems.CountNotFound();
    }

    internal static async Task<Results<Created<OfficeCashCountView>, Ok<OfficeCashCountView>, ValidationProblem, ProblemHttpResult>> CreateAsync(
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

        if (request.CountedAmount is null)
        {
            errors.Add("countedAmount", "Enter the cash counted.");
        }

        var counted = OfficeInput.NonNegativeMoney(request.CountedAmount, errors, "countedAmount", "The counted cash");
        var countedOn = Input.OptionalDate(request.CountedOn, errors, "countedOn", "The count date") ?? clock.Today;
        if (countedOn > clock.Today)
        {
            errors.Add("countedOn", "The count date can't be in the future.");
        }

        var explanation = Input.OptionalText(request.Explanation, OfficeLengths.Note, errors, "explanation", "The explanation");
        var key = OfficeInput.Key(request.IdempotencyKey, errors);
        if (errors.HasErrors)
        {
            return OfficeProblems.Validation(errors);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var custody = await db.Custodies.SingleOrDefaultAsync(candidate => candidate.Id == request.CustodyId, cancellationToken);
        if (custody is null || !caller.MaySee(custody))
        {
            return OfficeProblems.CustodyNotFound();
        }

        if (!caller.Holds(custody) && !caller.IsReviewer)
        {
            return OfficeProblems.NotTheHolder();
        }

        if (custody.Status == OfficeCustodyStatus.Closed)
        {
            return OfficeProblems.Inactive(custody.Status.ToString());
        }

        if (!await CustodyLedger.LockAsync(db, custody.Id, cancellationToken))
        {
            return OfficeProblems.TryAgainLater();
        }

        if (await db.CashCounts.AsNoTracking().SingleOrDefaultAsync(count => count.IdempotencyKey == key, cancellationToken) is { } repeat)
        {
            return repeat.CountedBy == caller.UserId && repeat.CustodyId == custody.Id && repeat.CountedAmount == counted
                ? TypedResults.Ok((await ViewAsync(db, users, caller, repeat.Id, cancellationToken))!)
                : OfficeProblems.KeyUsedElsewhere();
        }

        var recorded = await CustodyLedger.RecordedAsync(db, custody.Id, cancellationToken);
        var difference = counted - recorded;
        if (difference != 0 && explanation is null)
        {
            errors.Add("explanation", $"The cash is {(difference > 0 ? "more" : "less")} than the recorded balance by {Math.Abs(difference):0.00}. Explain why.");
            return OfficeProblems.Validation(errors);
        }

        var (attachments, refused) = await AttachmentBinding.LoadAsync(db, (request.AttachmentIds ?? []).Distinct().ToList(), caller.UserId,
            OfficeAttachmentOwners.CashCount, null, cancellationToken);
        if (refused is not null)
        {
            return refused;
        }

        var count = new OfficeCashCount
        {
            CustodyId = custody.Id,
            CountedOn = countedOn,
            CountedBy = caller.UserId,
            CountedAt = clock.UtcNow,
            RecordedBalance = recorded,
            CountedAmount = counted,
            Difference = difference,
            Explanation = explanation,
            IdempotencyKey = key!.Value,
            Status = difference == 0 ? OfficeCashCountStatus.Matched : OfficeCashCountStatus.Submitted,
        };
        db.CashCounts.Add(count);
        await db.SaveChangesAsync(cancellationToken);

        AttachmentBinding.Bind(attachments, OfficeAttachmentOwners.CashCount, count.Id, clock.UtcNow);
        if (difference != 0)
        {
            alerts.Add<OfficeDiscrepancyAlert>(db, await alerts.ReviewersAsync(caller.UserId, cancellationToken), custody.Id, OfficeAttachmentOwners.CashCount, count.Id, clock.UtcNow);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        log.CashCounted(caller.UserId, custody.Id, count.Id, count.Status.ToString());

        return TypedResults.Created($"/api/office/cash-counts/{count.Id}", (await ViewAsync(db, users, caller, count.Id, cancellationToken))!);
    }

    internal static async Task<Results<Ok<OfficeCashCountView>, ValidationProblem, ProblemHttpResult>> ReviewAsync(
        int countId,
        OfficeExpenseEndpoints.DecisionRequest request,
        bool adjust,
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
        var note = OfficeInput.Reason(request.Note, errors);
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (errors.HasErrors)
        {
            return OfficeProblems.Validation(errors);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var custodyId = await db.CashCounts.Where(count => count.Id == countId).Select(count => (int?)count.CustodyId).SingleOrDefaultAsync(cancellationToken);
        if (custodyId is null)
        {
            return OfficeProblems.CountNotFound();
        }

        if (!await CustodyLedger.LockAsync(db, custodyId.Value, cancellationToken))
        {
            return OfficeProblems.TryAgainLater();
        }

        var count = await db.CashCounts.SingleAsync(candidate => candidate.Id == countId, cancellationToken);
        var custody = await db.Custodies.SingleAsync(candidate => candidate.Id == custodyId, cancellationToken);
        if (count.CountedBy == caller.UserId || custody.HolderUserId == caller.UserId)
        {
            return OfficeProblems.ReviewerIsSubmitter();
        }

        var target = adjust ? OfficeCashCountStatus.Adjusted : OfficeCashCountStatus.Accepted;
        if (count.Status == target)
        {
            await transaction.RollbackAsync(cancellationToken);
            return TypedResults.Ok((await ViewAsync(db, users, caller, countId, cancellationToken))!);
        }

        if (!count.RowVersion.SequenceEqual(rowVersion!))
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        if (count.Status != OfficeCashCountStatus.Submitted)
        {
            return OfficeProblems.WrongStatus($"Only a count with a difference waiting for review can be decided. This one is {count.Status}.");
        }

        var now = clock.UtcNow;
        count.MoveTo(target);
        count.ReviewedBy = caller.UserId;
        count.ReviewedAt = now;
        count.ReviewNote = note;

        if (adjust)
        {
            var entry = CustodyLedger.Add(db, custody.Id, OfficeLedgerKind.Adjustment, count.Difference, clock.Today, OfficeLedgerSources.CashCount, count.Id,
                caller.UserId, now, note);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, OfficeLedgerEntryConfiguration.SourceIndex))
            {
                return OfficeProblems.WrongStatus("This difference was already posted.");
            }

            count.AdjustmentEntryId = entry.Id;
            log.EntryPosted(caller.UserId, custody.Id, entry.Id, entry.Kind.ToString());
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        if (adjust)
        {
            await CustodyLedger.CheckLowBalanceAsync(db, custody, await CustodyLedger.RecordedAsync(db, custody.Id, cancellationToken), alerts, log, now, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        log.CashCountMoved(caller.UserId, count.Id, target.ToString());
        return TypedResults.Ok((await ViewAsync(db, users, caller, count.Id, cancellationToken))!);
    }

    internal static IQueryable<OfficeCashCount> Visible(OfficeDbContext db, OfficeCaller caller) =>
        caller.SeesAll
            ? db.CashCounts.AsNoTracking()
            : db.CashCounts.AsNoTracking().Where(count => db.Custodies.Any(custody => custody.Id == count.CustodyId && custody.HolderUserId == caller.UserId));

    internal static async Task<OfficeCashCountView?> ViewAsync(OfficeDbContext db, IUserDirectory users, OfficeCaller caller, int countId, CancellationToken cancellationToken)
    {
        var count = await Visible(db, caller).SingleOrDefaultAsync(candidate => candidate.Id == countId, cancellationToken);
        if (count is null)
        {
            return null;
        }

        var holder = await db.Custodies.Where(custody => custody.Id == count.CustodyId).Select(custody => custody.HolderUserId).SingleAsync(cancellationToken);
        var people = await users.FindAsync(new int?[] { count.CountedBy, count.ReviewedBy }.Where(id => id is not null).Select(id => id!.Value).Distinct().ToList(), cancellationToken);
        var attachments = await OfficeAttachmentView.OfAsync(db, OfficeAttachmentOwners.CashCount, count.Id, cancellationToken);
        return new OfficeCashCountView(
            count.Id, count.CustodyId, count.CountedOn, OfficePersonRef.From(count.CountedBy, people), count.CountedAt, count.RecordedBalance, count.CountedAmount,
            count.Difference, count.Explanation, count.Status.ToString(), OfficePersonRef.From(count.ReviewedBy, people), count.ReviewedAt, count.ReviewNote,
            count.AdjustmentEntryId, attachments,
            caller.IsReviewer && count.Status == OfficeCashCountStatus.Submitted && count.CountedBy != caller.UserId && holder != caller.UserId,
            RowVersions.Format(count.RowVersion));
    }
}
