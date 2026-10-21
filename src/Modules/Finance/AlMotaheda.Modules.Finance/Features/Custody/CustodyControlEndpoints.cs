using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Finance.Common;
using AlMotaheda.Modules.Finance.Contracts;
using AlMotaheda.Modules.Finance.Data;
using AlMotaheda.Modules.Finance.Data.Configurations;
using AlMotaheda.Modules.Finance.Requests;
using AlMotaheda.Modules.Finance.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlMotaheda.Modules.Finance.Features.Custody;

internal sealed record CashCountView(
    int Id, int AccountId, DateOnly CountDate, decimal CountedAmount, decimal BookBalance, decimal Difference, string? Notes, string? EvidenceReference,
    DateTime CountedAt, PersonView? CountedBy);

internal sealed record AdjustmentView(
    int Id, int AccountId, int? CashCountId, DateOnly EntryDate, decimal Amount, string Reason, string EvidenceReference, DateTime CreatedAt, PersonView? CreatedBy,
    decimal BalanceAfter);

internal sealed record PeriodCloseView(int Id, int AccountId, DateOnly PeriodEnd, decimal ClosingBalance, decimal PendingAtClose, string? Notes, DateTime ClosedAt, PersonView? ClosedBy);

/// <summary>
/// The controls of a custody account: cash counts (<c>POST /api/custody-accounts/{id}/cash-counts</c>: the holder
/// or a reviewer; a difference never changes the balance), adjustments (<c>POST …/adjustments</c>: custody Approve,
/// never the holder, with a reason and evidence; the one way a counted difference reaches the balance) and period
/// closes (<c>POST …/period-closes</c>: custody Approve; every entry is kept, nothing may be dated inside a closed
/// period afterwards; the balance carries forward by <c>Finance:CustodyPeriodCarryForward</c>).
/// </summary>
internal static class CustodyControlEndpoints
{
    internal sealed record CountRequest(string? CountDate, decimal? CountedAmount, string? Notes, string? EvidenceReference, string? IdempotencyKey);

    internal sealed record AdjustmentRequest(decimal? Amount, string? EntryDate, string? Reason, string? EvidenceReference, int? CashCountId, string? IdempotencyKey);

    internal sealed record CloseRequest(string? PeriodEnd, string? Notes);

    public static void Map(IEndpointRouteBuilder api)
    {
        var accounts = api.MapGroup("/custody-accounts/{accountId:int}");

        accounts.MapGet("/cash-counts", ListCountsAsync)
            .RequirePermission(FinancePermissions.Custody, PermissionAction.View)
            .WithName("ListCustodyCashCounts")
            .WithSummary("The cash counts of a custody account, newest first");

        accounts.MapPost("/cash-counts", CountAsync)
            .RequireAnyPermission((FinancePermissions.Custody, PermissionAction.Create), (FinancePermissions.Custody, PermissionAction.Approve))
            .WithName("RecordCustodyCashCount")
            .WithSummary("Record a physical cash count against the recorded balance (the holder or a custody reviewer). The difference changes nothing");

        accounts.MapGet("/adjustments", ListAdjustmentsAsync)
            .RequirePermission(FinancePermissions.Custody, PermissionAction.View)
            .WithName("ListCustodyAdjustments")
            .WithSummary("The reviewed adjustments of a custody account, newest first");

        accounts.MapPost("/adjustments", AdjustAsync)
            .RequirePermission(FinancePermissions.Custody, PermissionAction.Approve)
            .WithName("AdjustCustodyBalance")
            .WithSummary("Post a reviewed adjustment with a reason and evidence (custody Approve; never the holder). 409 period_closed");

        accounts.MapGet("/period-closes", ListClosesAsync)
            .RequirePermission(FinancePermissions.Custody, PermissionAction.View)
            .WithName("ListCustodyPeriodCloses")
            .WithSummary("The closed periods of a custody account");

        accounts.MapPost("/period-closes", CloseAsync)
            .RequirePermission(FinancePermissions.Custody, PermissionAction.Approve)
            .WithName("CloseCustodyPeriod")
            .WithSummary("Close a period up to a date (custody Approve): a snapshot of the balance; every entry is kept. 409 period_closed, period_balance_not_zero");
    }

    internal static async Task<Results<Ok<IReadOnlyList<CashCountView>>, ProblemHttpResult>> ListCountsAsync(
        int accountId, HttpContext httpContext, FinanceDbContext db, CustodyAccountViews views, IUserDirectory users, CancellationToken cancellationToken)
    {
        if (FinanceCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        if (await views.FindVisibleAsync(caller, accountId, cancellationToken) is null)
        {
            return FinanceProblems.CustodyAccountNotFound();
        }

        var counts = await db.CustodyCashCounts.AsNoTracking().Where(count => count.AccountId == accountId)
            .OrderByDescending(count => count.CountDate).ThenByDescending(count => count.Id).Take(200).ToListAsync(cancellationToken);
        var people = await users.FindAsync(counts.Select(count => count.CountedBy).Distinct().ToList(), cancellationToken);
        return TypedResults.Ok<IReadOnlyList<CashCountView>>(counts.Select(count => CountView(count, people)).ToList());
    }

    internal static async Task<Results<Created<CashCountView>, Ok<CashCountView>, ValidationProblem, ProblemHttpResult>> CountAsync(
        int accountId,
        CountRequest request,
        HttpContext httpContext,
        FinanceDbContext db,
        CustodyLedgers ledgers,
        CustodyAccountViews views,
        IUserDirectory users,
        CompanyClock clock,
        FinanceLog log,
        CancellationToken cancellationToken)
    {
        if (FinanceCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var countDate = Input.OptionalDate(request.CountDate, errors, "countDate", "The count date") ?? clock.Today;
        if (countDate > clock.Today)
        {
            errors.Add("countDate", "The count date can't be in the future.");
        }

        if (request.CountedAmount is null)
        {
            errors.Add("countedAmount", "The counted amount is required.");
        }
        else if (request.CountedAmount < 0 || request.CountedAmount > NumberInput.MaxMoney || decimal.Round(request.CountedAmount.Value, 2) != request.CountedAmount.Value)
        {
            errors.Add("countedAmount", "The counted amount must be zero or more, with at most two decimals.");
        }

        var notes = Input.OptionalText(request.Notes, CustodyCashCountConfiguration.NotesMaxLength, errors, "notes", "Notes");
        var evidence = Input.OptionalText(request.EvidenceReference, CustodyCashCountConfiguration.EvidenceMaxLength, errors, "evidenceReference", "The evidence reference");
        if (!Guid.TryParse(Input.Clean(request.IdempotencyKey), out var key) || key == Guid.Empty)
        {
            errors.Add("idempotencyKey", "idempotencyKey must be a GUID made by the client for this count.");
        }

        if (errors.HasErrors)
        {
            return FinanceProblems.Validation(errors);
        }

        var account = await views.FindVisibleAsync(caller, accountId, cancellationToken);
        if (account is null)
        {
            return FinanceProblems.CustodyAccountNotFound();
        }

        if (!CustodyAccountViews.MayOperate(caller, account))
        {
            return FinanceProblems.OnlyTheHolder("count its cash");
        }

        if (account.Status == CustodyAccountStatus.Closed)
        {
            return FinanceProblems.AccountClosed();
        }

        var people = await users.FindAsync([caller.UserId], cancellationToken);
        if (await db.CustodyCashCounts.AsNoTracking().SingleOrDefaultAsync(count => count.IdempotencyKey == key, cancellationToken) is { } repeat)
        {
            return repeat.AccountId == accountId && repeat.CountedBy == caller.UserId ? TypedResults.Ok(CountView(repeat, people)) : FinanceProblems.KeyUsedElsewhere();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await CustodyLedgers.TryLockAsync(db, accountId, cancellationToken))
        {
            return FinanceProblems.CustodyIsBusy();
        }

        // The book balance is read under the lock, so the count compares with exactly what was posted at that moment.
        var figures = await ledgers.FiguresAsync(account, cancellationToken);
        var count = new CustodyCashCount
        {
            AccountId = accountId,
            CountDate = countDate,
            CountedAmount = request.CountedAmount!.Value,
            BookBalance = figures.Posted,
            Difference = request.CountedAmount.Value - figures.Posted,
            Notes = notes,
            EvidenceReference = evidence,
            IdempotencyKey = key,
            CountedAt = clock.UtcNow,
            CountedBy = caller.UserId,
        };
        db.CustodyCashCounts.Add(count);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, CustodyCashCountConfiguration.IdempotencyKeyIndex))
        {
            await transaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            var first = await db.CustodyCashCounts.AsNoTracking().SingleAsync(candidate => candidate.IdempotencyKey == key, cancellationToken);
            return TypedResults.Ok(CountView(first, people));
        }

        await transaction.CommitAsync(cancellationToken);
        log.CashCounted(caller.UserId, accountId, count.Id);
        return TypedResults.Created($"/api/custody-accounts/{accountId}/cash-counts", CountView(count, people));
    }

    internal static async Task<Results<Ok<IReadOnlyList<AdjustmentView>>, ProblemHttpResult>> ListAdjustmentsAsync(
        int accountId, HttpContext httpContext, FinanceDbContext db, CustodyAccountViews views, IUserDirectory users, CancellationToken cancellationToken)
    {
        if (FinanceCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        if (await views.FindVisibleAsync(caller, accountId, cancellationToken) is null)
        {
            return FinanceProblems.CustodyAccountNotFound();
        }

        var adjustments = await db.CustodyAdjustments.AsNoTracking().Where(adjustment => adjustment.AccountId == accountId)
            .OrderByDescending(adjustment => adjustment.EntryDate).ThenByDescending(adjustment => adjustment.Id).Take(200).ToListAsync(cancellationToken);
        var people = await users.FindAsync(adjustments.Select(adjustment => adjustment.CreatedBy).Distinct().ToList(), cancellationToken);
        return TypedResults.Ok<IReadOnlyList<AdjustmentView>>(adjustments.Select(adjustment => AdjustmentOf(adjustment, people, null)).ToList());
    }

    /// <summary>
    /// Posts an adjustment: a movement with the reason and the evidence, by a reviewer who is not the holder, under
    /// the account's lock, never inside a closed period. For an engineer's project custody or a vehicle the owning
    /// module writes it in the same transaction.
    /// </summary>
    internal static async Task<Results<Created<AdjustmentView>, Ok<AdjustmentView>, ValidationProblem, ProblemHttpResult>> AdjustAsync(
        int accountId,
        AdjustmentRequest request,
        HttpContext httpContext,
        FinanceDbContext db,
        CustodyLedgers ledgers,
        CustodyAccountViews views,
        IUserDirectory users,
        IPermissionCheck permissions,
        CompanyClock clock,
        FinanceLog log,
        CancellationToken cancellationToken)
    {
        if (FinanceCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        if (request.Amount is null or 0)
        {
            errors.Add("amount", "The amount is required and can't be zero.");
        }
        else if (Math.Abs(request.Amount.Value) > NumberInput.MaxMoney || decimal.Round(request.Amount.Value, 2) != request.Amount.Value)
        {
            errors.Add("amount", "The amount must have at most two decimals.");
        }

        var entryDate = Input.OptionalDate(request.EntryDate, errors, "entryDate", "The date") ?? clock.Today;
        if (entryDate > clock.Today)
        {
            errors.Add("entryDate", "The date can't be in the future.");
        }

        var reason = Input.RequiredText(request.Reason, CustodyAdjustmentConfiguration.ReasonMaxLength, errors, "reason", "The reason");
        var evidence = Input.RequiredText(request.EvidenceReference, CustodyAdjustmentConfiguration.EvidenceMaxLength, errors, "evidenceReference", "The evidence");
        if (request.CashCountId is < 1)
        {
            errors.Add("cashCountId", "cashCountId must be a count id.");
        }

        if (!Guid.TryParse(Input.Clean(request.IdempotencyKey), out var key) || key == Guid.Empty)
        {
            errors.Add("idempotencyKey", "idempotencyKey must be a GUID made by the client for this adjustment.");
        }

        if (errors.HasErrors)
        {
            return FinanceProblems.Validation(errors);
        }

        if (!await caller.StillHoldsAsync(permissions, FinancePermissions.Custody, PermissionAction.Approve, cancellationToken))
        {
            return FinanceProblems.Revoked();
        }

        var people = await users.FindAsync([caller.UserId], cancellationToken);
        if (await db.CustodyAdjustments.AsNoTracking().SingleOrDefaultAsync(adjustment => adjustment.IdempotencyKey == key, cancellationToken) is { } repeat)
        {
            return repeat.AccountId == accountId && repeat.CreatedBy == caller.UserId ? TypedResults.Ok(AdjustmentOf(repeat, people, null)) : FinanceProblems.KeyUsedElsewhere();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await CustodyLedgers.TryLockAsync(db, accountId, cancellationToken))
        {
            return FinanceProblems.CustodyIsBusy();
        }

        var account = await db.CustodyAccounts.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == accountId, cancellationToken);
        if (account is null)
        {
            return FinanceProblems.CustodyAccountNotFound();
        }

        // Separation of duties: the holder never adjusts their own custody.
        if (account.HolderUserId == caller.UserId)
        {
            return FinanceProblems.OwnRequest();
        }

        if (account.Status == CustodyAccountStatus.Closed)
        {
            return FinanceProblems.AccountClosed();
        }

        if (request.CashCountId is { } countId && !await db.CustodyCashCounts.AnyAsync(count => count.Id == countId && count.AccountId == accountId, cancellationToken))
        {
            return FinanceProblems.CashCountNotFound();
        }

        if (await ledgers.ClosedPeriodOfAsync(accountId, entryDate, cancellationToken) is { } closed)
        {
            return FinanceProblems.ClosedPeriod(closed);
        }

        var adjustment = new CustodyAdjustment
        {
            AccountId = accountId,
            CashCountId = request.CashCountId,
            EntryDate = entryDate,
            Amount = request.Amount!.Value,
            Reason = reason!,
            EvidenceReference = evidence!,
            IdempotencyKey = key,
            CreatedAt = clock.UtcNow,
            CreatedBy = caller.UserId,
        };
        db.CustodyAdjustments.Add(adjustment);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            var write = new CustodyLedgerWrite(account.ReferenceId ?? 0, account.HolderUserId, Math.Abs(adjustment.Amount), entryDate, CustodyLedgerSources.Adjustment, adjustment.Id, evidence, reason, caller.UserId);
            if (adjustment.Amount > 0)
            {
                await ledgers.ReceiveAsync(account, CustodyMovementKind.Adjustment, write, null, SharedTransaction.Of(db), cancellationToken);
            }
            else
            {
                await ledgers.WithdrawAsync(account, CustodyMovementKind.Adjustment, write, null, null, SharedTransaction.Of(db), cancellationToken);
            }
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, CustodyAdjustmentConfiguration.IdempotencyKeyIndex))
        {
            await transaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            var first = await db.CustodyAdjustments.AsNoTracking().SingleAsync(candidate => candidate.IdempotencyKey == key, cancellationToken);
            return TypedResults.Ok(AdjustmentOf(first, people, null));
        }
        catch (CustodyLedgerRefusedException refused)
        {
            await transaction.RollbackAsync(cancellationToken);
            return refused.Code == CustodyLedgerRefusedException.Busy ? FinanceProblems.CustodyIsBusy() : FinanceProblems.Conflict(refused.Code, refused.Message);
        }

        await transaction.CommitAsync(cancellationToken);
        log.CustodyAdjusted(caller.UserId, accountId, adjustment.Id);
        var after = await ledgers.FiguresAsync(account, cancellationToken);
        return TypedResults.Created($"/api/custody-accounts/{accountId}/adjustments", AdjustmentOf(adjustment, people, after.Posted));
    }

    internal static async Task<Results<Ok<IReadOnlyList<PeriodCloseView>>, ProblemHttpResult>> ListClosesAsync(
        int accountId, HttpContext httpContext, FinanceDbContext db, CustodyAccountViews views, IUserDirectory users, CancellationToken cancellationToken)
    {
        if (FinanceCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        if (await views.FindVisibleAsync(caller, accountId, cancellationToken) is null)
        {
            return FinanceProblems.CustodyAccountNotFound();
        }

        var closes = await db.CustodyPeriodCloses.AsNoTracking().Where(close => close.AccountId == accountId).OrderByDescending(close => close.PeriodEnd).ToListAsync(cancellationToken);
        var people = await users.FindAsync(closes.Select(close => close.ClosedBy).Distinct().ToList(), cancellationToken);
        return TypedResults.Ok<IReadOnlyList<PeriodCloseView>>(closes.Select(close => CloseView(close, people)).ToList());
    }

    internal static async Task<Results<Created<PeriodCloseView>, ValidationProblem, ProblemHttpResult>> CloseAsync(
        int accountId,
        CloseRequest request,
        HttpContext httpContext,
        FinanceDbContext db,
        CustodyLedgers ledgers,
        IOptions<FinanceOptions> options,
        IUserDirectory users,
        CompanyClock clock,
        FinanceLog log,
        CancellationToken cancellationToken)
    {
        if (FinanceCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var periodEnd = Input.RequiredDate(request.PeriodEnd, errors, "periodEnd", "The period end");
        if (periodEnd > clock.Today)
        {
            errors.Add("periodEnd", "A period can't be closed before it ends.");
        }

        var notes = Input.OptionalText(request.Notes, CustodyPeriodCloseConfiguration.NotesMaxLength, errors, "notes", "Notes");
        if (errors.HasErrors)
        {
            return FinanceProblems.Validation(errors);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await CustodyLedgers.TryLockAsync(db, accountId, cancellationToken))
        {
            return FinanceProblems.CustodyIsBusy();
        }

        var account = await db.CustodyAccounts.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == accountId, cancellationToken);
        if (account is null)
        {
            return FinanceProblems.CustodyAccountNotFound();
        }

        if (await ledgers.LastPeriodEndAsync(accountId, cancellationToken) is { } last && periodEnd <= last)
        {
            return FinanceProblems.ClosedPeriod(last);
        }

        var figures = await ledgers.FiguresAsync(account, cancellationToken);
        if (options.Value.RequiresZeroBalanceAtClose && figures.Posted != 0)
        {
            return FinanceProblems.BalanceNotZero(figures.Posted);
        }

        var close = new CustodyPeriodClose
        {
            AccountId = accountId,
            PeriodEnd = periodEnd!.Value,
            ClosingBalance = figures.Posted,
            PendingAtClose = figures.Pending,
            Notes = notes,
            ClosedAt = clock.UtcNow,
            ClosedBy = caller.UserId,
        };
        db.CustodyPeriodCloses.Add(close);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, CustodyPeriodCloseConfiguration.PeriodIndex))
        {
            return FinanceProblems.ClosedPeriod(periodEnd.Value);
        }

        await transaction.CommitAsync(cancellationToken);
        log.PeriodClosed(caller.UserId, accountId, periodEnd.Value);
        var people = await users.FindAsync([caller.UserId], cancellationToken);
        return TypedResults.Created($"/api/custody-accounts/{accountId}/period-closes", CloseView(close, people));
    }

    private static CashCountView CountView(CustodyCashCount count, IReadOnlyDictionary<int, UserSummary> people) => new(
        count.Id, count.AccountId, count.CountDate, count.CountedAmount, count.BookBalance, count.Difference, count.Notes, count.EvidenceReference,
        count.CountedAt, CustodyAccountViews.Person(count.CountedBy, people));

    private static AdjustmentView AdjustmentOf(CustodyAdjustment adjustment, IReadOnlyDictionary<int, UserSummary> people, decimal? balanceAfter) => new(
        adjustment.Id, adjustment.AccountId, adjustment.CashCountId, adjustment.EntryDate, adjustment.Amount, adjustment.Reason, adjustment.EvidenceReference,
        adjustment.CreatedAt, CustodyAccountViews.Person(adjustment.CreatedBy, people), balanceAfter ?? 0);

    private static PeriodCloseView CloseView(CustodyPeriodClose close, IReadOnlyDictionary<int, UserSummary> people) => new(
        close.Id, close.AccountId, close.PeriodEnd, close.ClosingBalance, close.PendingAtClose, close.Notes, close.ClosedAt, CustodyAccountViews.Person(close.ClosedBy, people));
}
