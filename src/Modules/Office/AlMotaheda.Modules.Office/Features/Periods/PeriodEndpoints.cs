using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Finance.Contracts;
using AlMotaheda.Modules.Office.Common;
using AlMotaheda.Modules.Office.Data;
using AlMotaheda.Modules.Office.Data.Configurations;
using AlMotaheda.Modules.Office.Features.Alerts;
using AlMotaheda.Modules.Office.Features.Custody;
using AlMotaheda.Modules.Office.Features.Expenses;
using AlMotaheda.Modules.Office.Features.Ledger;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlMotaheda.Modules.Office.Features.Periods;

/// <summary>
/// A month's allocation as a screen shows it. Finance's own references (its request number and status) are shown to
/// finance only, never to the custodian.
/// </summary>
internal sealed record OfficePeriodAllocationView(
    int FundingId, decimal Amount, string Status, decimal? DisbursedAmount, decimal? ReceivedAmount, bool CanConfirm, string RowVersion,
    string? PaymentRequestNumber, string? PaymentRequestStatus);

/// <summary>What the caller may do with the month now; the server checks again on every action.</summary>
internal sealed record OfficePeriodActions(bool AuthorizeAllocation, bool Settle, IReadOnlyList<string> SettleOptions);

internal sealed record OfficePeriodView(
    int Id,
    int CustodyId,
    string CustodyNameAr,
    string CustodyNameEn,
    int Year,
    int Month,
    DateOnly StartsOn,
    DateOnly EndsOn,
    string Status,
    bool IsCurrent,
    decimal PlannedAllocation,
    OfficePeriodFigures Figures,
    OfficePeriodAllocationView? Allocation,
    int Waiting,
    DateTime? EndedAt,
    string? Settlement,
    decimal? SettledAmount,
    DateTime? ClosedAt,
    OfficePersonRef? ClosedBy,
    string? ClosingNote,
    OfficePeriodActions Actions,
    string RowVersion);

/// <summary>One movement of a month. Who posted it and Finance's references are left out for the custodian.</summary>
internal sealed record OfficePeriodMovement(
    long Id, string Kind, decimal Amount, DateOnly EntryDate, string SourceType, int SourceId, string? Reason, string? Reference, DateTime PostedAt,
    OfficePersonRef? PostedBy);

internal sealed record OfficePeriodDetail(OfficePeriodView Period, IReadOnlyList<OfficeExpenseRow> Expenses, IReadOnlyList<OfficePeriodMovement> Movements);

/// <summary>
/// The monthly custody cycle:
/// <list type="bullet">
///   <item><c>GET /api/office/my-custody</c>: the custodian's whole screen — the current month's allocation, spending
///   and remaining balance of each custody they hold, and the cash waiting for their receipt. Nothing else.</item>
///   <item><c>GET /api/office/custodies/{id}/periods</c>, <c>GET /api/office/periods/{id}</c>: the months (history kept
///   for good) and one month's expenses and movements; the custodian sees only their own custodies.</item>
///   <item><c>POST /api/office/periods/{id}/allocation</c> (office Approve, payments Create): authorize the month's
///   allocation; Finance is asked to pay it.</item>
///   <item><c>POST /api/office/periods/{id}/settle</c> (office Approve): settle a finished month's leftover money,
///   returned or carried forward as the company policy allows; the month then closes.</item>
/// </list>
/// </summary>
internal static class OfficePeriodEndpoints
{
    internal sealed record AllocationRequest(decimal? Amount, string? Method, string? Note, Guid? IdempotencyKey);

    internal sealed record SettleRequest(string? Action, string? Note, string? Reference, Guid? IdempotencyKey, string? RowVersion);

    internal sealed record ToConfirm(int FundingId, string Kind, int? PeriodId, decimal Amount, DateOnly? PaidOn, string RowVersion);

    internal sealed record MyCustody(int CustodyId, string NameAr, string NameEn, string Status, OfficePeriodView? Month, IReadOnlyList<ToConfirm> ToConfirm);

    internal sealed record MyCustodyPolicy(IReadOnlyList<string> Categories, int ImageMaxMegabytes, int MaxExpenseAgeDays);

    internal sealed record MyCustodyResponse(IReadOnlyList<MyCustody> Items, MyCustodyPolicy Policy);

    internal sealed record PeriodList(IReadOnlyList<OfficePeriodView> Items);

    public static class SettleActions
    {
        public const string Return = "Return";
        public const string CarryForward = "CarryForward";
    }

    public static void Map(IEndpointRouteBuilder api)
    {
        api.MapGet("/office/my-custody", MyCustodyAsync)
            .RequirePermission(OfficePermissions.Module, PermissionAction.View)
            .WithName("GetMyOfficeCustody")
            .WithSummary("The custodian's own custody this month: allocation, posted spending, remaining balance, cash to confirm");

        api.MapGet("/office/custodies/{custodyId:int}/periods", ListAsync)
            .RequirePermission(OfficePermissions.Module, PermissionAction.View)
            .WithName("ListOfficeCustodyPeriods")
            .WithSummary("The months of a custody, newest first, with their figures");

        api.MapGet("/office/periods/{periodId:int}", GetAsync)
            .RequirePermission(OfficePermissions.Module, PermissionAction.View)
            .WithName("GetOfficeCustodyPeriod")
            .WithSummary("One month of a custody: its figures, expenses and movements");

        api.MapPost("/office/periods/{periodId:int}/allocation", AuthorizeAllocationAsync)
            .RequirePermission(OfficePermissions.Module, PermissionAction.Approve)
            .RequirePermission(OfficePermissions.Payments, PermissionAction.Create)
            .WithName("AuthorizeOfficeMonthlyAllocation")
            .WithSummary("Authorize the current month's allocation and ask Finance to pay it (office Approve, payments Create)");

        api.MapPost("/office/periods/{periodId:int}/settle", SettleAsync)
            .RequirePermission(OfficePermissions.Module, PermissionAction.Approve)
            .WithName("SettleOfficeCustodyPeriod")
            .WithSummary("Settle a finished month: return or carry forward what is left, per the company policy, and close it (office Approve)");
    }

    internal static async Task<Results<Ok<MyCustodyResponse>, ProblemHttpResult>> MyCustodyAsync(
        HttpContext httpContext, OfficeDbContext db, IUserDirectory users, IPaymentRequests payments, OfficeAlerts alerts, IOptions<OfficeOptions> settings,
        CompanyClock clock, OfficeLog log, CancellationToken cancellationToken)
    {
        if (OfficeCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        // Only the custodies the caller holds, whatever else they may see.
        var heldIds = await db.Custodies.AsNoTracking()
            .Where(custody => custody.HolderUserId == caller.UserId && custody.Status != OfficeCustodyStatus.Closed)
            .Select(custody => custody.Id).ToListAsync(cancellationToken);
        await CustodyPeriods.RollIfNeededAsync(db, heldIds, clock.Today, clock.UtcNow, alerts, log, cancellationToken);

        var held = await db.Custodies.AsNoTracking().Where(custody => heldIds.Contains(custody.Id)).OrderBy(custody => custody.NameEn).ToListAsync(cancellationToken);
        var open = await CustodyPeriods.OpenOfAsync(db, heldIds, cancellationToken);
        var views = await ViewsAsync(db, users, payments, caller, open.Values.ToList(), settings.Value, clock.Today, cancellationToken);
        var toConfirm = await db.FundingRequests.AsNoTracking()
            .Where(funding => heldIds.Contains(funding.CustodyId) && funding.Status == OfficeFundingStatus.Disbursed)
            .OrderBy(funding => funding.DisbursedOn).ThenBy(funding => funding.Id)
            .ToListAsync(cancellationToken);

        var options = settings.Value;
        return TypedResults.Ok(new MyCustodyResponse(
            held.Select(custody => new MyCustody(
                    custody.Id, custody.NameAr, custody.NameEn, custody.Status.ToString(),
                    open.TryGetValue(custody.Id, out var month) ? views.Single(view => view.Id == month.Id) : null,
                    toConfirm.Where(funding => funding.CustodyId == custody.Id)
                        .Select(funding => new ToConfirm(funding.Id, funding.Kind.ToString(), funding.PeriodId, funding.DisbursedAmount ?? 0m, funding.DisbursedOn,
                            RowVersions.Format(funding.RowVersion)))
                        .ToList()))
                .ToList(),
            new MyCustodyPolicy(OfficeExpenseCategories.All, options.ImageMaxMegabytes, options.MaxExpenseAgeDays)));
    }

    internal static async Task<Results<Ok<PeriodList>, ProblemHttpResult>> ListAsync(
        int custodyId, HttpContext httpContext, OfficeDbContext db, IUserDirectory users, IPaymentRequests payments, OfficeAlerts alerts,
        IOptions<OfficeOptions> settings, CompanyClock clock, OfficeLog log, CancellationToken cancellationToken)
    {
        if (OfficeCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        if (!await OfficeCustodyEndpoints.Visible(db, caller).AnyAsync(custody => custody.Id == custodyId, cancellationToken))
        {
            return OfficeProblems.CustodyNotFound();
        }

        await CustodyPeriods.RollIfNeededAsync(db, [custodyId], clock.Today, clock.UtcNow, alerts, log, cancellationToken);
        var periods = await db.Periods.AsNoTracking().Where(period => period.CustodyId == custodyId)
            .OrderByDescending(period => period.StartsOn).ToListAsync(cancellationToken);
        return TypedResults.Ok(new PeriodList(await ViewsAsync(db, users, payments, caller, periods, settings.Value, clock.Today, cancellationToken)));
    }

    internal static async Task<Results<Ok<OfficePeriodDetail>, ProblemHttpResult>> GetAsync(
        int periodId, HttpContext httpContext, OfficeDbContext db, IUserDirectory users, IPaymentRequests payments, OfficeAlerts alerts,
        IOptions<OfficeOptions> settings, CompanyClock clock, OfficeLog log, CancellationToken cancellationToken)
    {
        if (OfficeCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        if (await DetailAsync(db, users, payments, alerts, caller, periodId, settings.Value, clock, log, cancellationToken) is not { } detail)
        {
            return OfficeProblems.PeriodNotFound();
        }

        return TypedResults.Ok(detail);
    }

    internal static async Task<Results<Ok<OfficePeriodDetail>, ValidationProblem, ProblemHttpResult>> AuthorizeAllocationAsync(
        int periodId, AllocationRequest request, HttpContext httpContext, OfficeDbContext db, IUserDirectory users, IPaymentRequests payments,
        OfficeAlerts alerts, IOptions<OfficeOptions> settings, CompanyClock clock, OfficeLog log, CancellationToken cancellationToken)
    {
        if (OfficeCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var amount = NumberInput.OptionalMoney(request.Amount, errors, "amount", "The amount");
        var method = Input.Clean(request.Method) ?? PaymentMethods.Cash;
        if (!PaymentMethods.All.Contains(method))
        {
            errors.Add("method", $"method must be one of: {string.Join(", ", PaymentMethods.All)}.");
        }

        var note = Input.OptionalText(request.Note, OfficeLengths.Note, errors, "note", "The note");
        var key = OfficeInput.Key(request.IdempotencyKey, errors);
        if (errors.HasErrors)
        {
            return OfficeProblems.Validation(errors);
        }

        var custodyId = await db.Periods.Where(period => period.Id == periodId).Select(period => (int?)period.CustodyId).SingleOrDefaultAsync(cancellationToken);
        if (custodyId is null)
        {
            return OfficeProblems.PeriodNotFound();
        }

        // A retried form: the first one already asked Finance.
        if (await db.FundingRequests.AsNoTracking().SingleOrDefaultAsync(funding => funding.IdempotencyKey == key, cancellationToken) is { } repeat)
        {
            return repeat.PeriodId == periodId && repeat.RequestedBy == caller.UserId
                ? TypedResults.Ok((await DetailAsync(db, users, payments, alerts, caller, periodId, settings.Value, clock, log, cancellationToken))!)
                : OfficeProblems.KeyUsedElsewhere();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await CustodyLedger.LockAsync(db, custodyId.Value, cancellationToken))
        {
            return OfficeProblems.TryAgainLater();
        }

        var custody = await db.Custodies.SingleAsync(candidate => candidate.Id == custodyId, cancellationToken);
        if (custody.HolderUserId == caller.UserId)
        {
            return OfficeProblems.ReviewerIsSubmitter();
        }

        // Finance never lets the creator of a payment request decide it, and only the Executive decides payments:
        // the Accountant authorizes the allocation here, the Executive approves its payment in Finance.
        if (caller.IsExecutive)
        {
            log.ActionRefused(caller.UserId, nameof(AuthorizeAllocationAsync), "the Executive decides the payment in Finance");
            return OfficeProblems.FinanceSeparationRequired();
        }

        if (!custody.IsActive)
        {
            return OfficeProblems.Inactive(custody.Status.ToString());
        }

        await CustodyPeriods.CurrentAsync(db, custody, clock.Today, clock.UtcNow, alerts, log, cancellationToken);
        var period = await db.Periods.SingleAsync(candidate => candidate.Id == periodId, cancellationToken);
        if (period.Status != OfficePeriodStatus.Open)
        {
            return OfficeProblems.WrongStatus($"Only the current month's allocation can be authorized. {period.Year}-{period.Month:00} is {period.Status}.");
        }

        if (await PeriodAllocation.LiveAsync(db, period.Id, cancellationToken) is not null)
        {
            return OfficeProblems.AllocationAlreadyAsked();
        }

        var value = amount ?? (period.PlannedAllocation > 0 ? period.PlannedAllocation : custody.MonthlyAllocation);
        if (value <= 0)
        {
            errors.Add("amount", "Enter the month's allocation (the custody has no standing monthly amount).");
            return OfficeProblems.Validation(errors);
        }

        OfficeFundingRequest funding;
        try
        {
            funding = await PeriodAllocation.RequestAsync(db, payments, custody, period, value, method, note, key!.Value, caller.UserId, clock.UtcNow, clock.Today,
                cancellationToken);
        }
        catch (DbUpdateException exception) when (PeriodAllocation.IsAllocationConflict(exception))
        {
            return OfficeProblems.AllocationAlreadyAsked();
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, OfficeFundingRequestConfiguration.KeyIndex))
        {
            return OfficeProblems.KeyUsedElsewhere();
        }

        await transaction.CommitAsync(cancellationToken);
        log.AllocationAuthorized(caller.UserId, period.Id, funding.Id);
        log.FundingPaymentRequested(caller.UserId, funding.Id, funding.PaymentRequestId ?? 0);
        return TypedResults.Ok((await DetailAsync(db, users, payments, alerts, caller, periodId, settings.Value, clock, log, cancellationToken))!);
    }

    internal static async Task<Results<Ok<OfficePeriodDetail>, ValidationProblem, ProblemHttpResult>> SettleAsync(
        int periodId, SettleRequest request, HttpContext httpContext, OfficeDbContext db, IUserDirectory users, IPaymentRequests payments,
        OfficeAlerts alerts, IOptions<OfficeOptions> settings, CompanyClock clock, OfficeLog log, CancellationToken cancellationToken)
    {
        if (OfficeCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var action = Input.Clean(request.Action);
        if (action is not (SettleActions.Return or SettleActions.CarryForward))
        {
            errors.Add("action", $"action must be {SettleActions.Return} or {SettleActions.CarryForward}.");
        }

        var note = OfficeInput.Reason(request.Note, errors);
        var reference = Input.OptionalText(request.Reference, OfficeLengths.Reference, errors, "reference", "The reference");
        var key = OfficeInput.Key(request.IdempotencyKey, errors);
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (errors.HasErrors)
        {
            return OfficeProblems.Validation(errors);
        }

        var custodyId = await db.Periods.Where(period => period.Id == periodId).Select(period => (int?)period.CustodyId).SingleOrDefaultAsync(cancellationToken);
        if (custodyId is null)
        {
            return OfficeProblems.PeriodNotFound();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await CustodyLedger.LockAsync(db, custodyId.Value, cancellationToken))
        {
            return OfficeProblems.TryAgainLater();
        }

        var custody = await db.Custodies.SingleAsync(candidate => candidate.Id == custodyId, cancellationToken);
        if (custody.HolderUserId == caller.UserId)
        {
            return OfficeProblems.ReviewerIsSubmitter();
        }

        var now = clock.UtcNow;
        var current = await CustodyPeriods.CurrentAsync(db, custody, clock.Today, now, alerts, log, cancellationToken);
        var period = await db.Periods.SingleAsync(candidate => candidate.Id == periodId, cancellationToken);

        // A double tap: the month is already settled.
        if (period.Status == OfficePeriodStatus.Closed)
        {
            await transaction.CommitAsync(cancellationToken);
            return TypedResults.Ok((await DetailAsync(db, users, payments, alerts, caller, periodId, settings.Value, clock, log, cancellationToken))!);
        }

        if (!period.RowVersion.SequenceEqual(rowVersion!))
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        if (period.Status == OfficePeriodStatus.Open)
        {
            return OfficeProblems.NotSettleable("The month is not over yet. It can be settled from its last day onwards, once the next month started.");
        }

        var waiting = await CustodyPeriods.WaitingAsync(db, period.Id, cancellationToken);
        if (waiting > 0)
        {
            return OfficeProblems.NotSettleable(
                $"{waiting} records of this month still wait (expenses in review, returned or approved and waiting, or its allocation not received). Decide them first.");
        }

        var policy = settings.Value.Periods.UnusedBalancePolicy;
        var remaining = await CustodyPeriods.RemainingAsync(db, period.Id, cancellationToken);
        OfficePeriodSettlement settlement;
        if (remaining == 0)
        {
            settlement = OfficePeriodSettlement.NothingLeft;
        }
        else if (remaining < 0)
        {
            return OfficeProblems.NotSettleable($"The month ends below zero ({remaining:0.00}). Count the cash and post the difference first.");
        }
        else if (action == SettleActions.Return)
        {
            if (policy == OfficeUnusedBalancePolicy.CarryForward)
            {
                return OfficeProblems.PolicyRefuses(policy.ToString());
            }

            settlement = OfficePeriodSettlement.Returned;
            if (await db.CashReturns.AnyAsync(cashReturn => cashReturn.IdempotencyKey == key, cancellationToken))
            {
                return OfficeProblems.KeyUsedElsewhere();
            }

            var cashReturn = new OfficeCashReturn
            {
                CustodyId = custody.Id,
                PeriodId = period.Id,
                Amount = remaining,
                ReturnedOn = clock.Today,
                Reason = note!,
                Reference = reference,
                IdempotencyKey = key!.Value,
                CreatedAt = now,
                CreatedBy = caller.UserId,
            };
            db.CashReturns.Add(cashReturn);
            await db.SaveChangesAsync(cancellationToken);
            var entry = CustodyLedger.Add(db, custody.Id, period.Id, OfficeLedgerKind.Return, -remaining, clock.Today, OfficeLedgerSources.CashReturn, cashReturn.Id,
                caller.UserId, now, note, reference);
            await db.SaveChangesAsync(cancellationToken);
            log.CashReturned(caller.UserId, custody.Id, cashReturn.Id);
            log.EntryPosted(caller.UserId, custody.Id, entry.Id, entry.Kind.ToString());
        }
        else
        {
            if (policy == OfficeUnusedBalancePolicy.Return)
            {
                return OfficeProblems.PolicyRefuses(policy.ToString());
            }

            if (current is null || current.Id == period.Id)
            {
                return OfficeProblems.NotSettleable("There is no next month to carry the money into.");
            }

            settlement = OfficePeriodSettlement.CarriedForward;
            var carriedOut = CustodyLedger.Add(db, custody.Id, period.Id, OfficeLedgerKind.CarryForwardOut, -remaining, clock.Today, OfficeLedgerSources.CarryOut,
                period.Id, caller.UserId, now, note, $"{current.Year}-{current.Month:00}");
            var carriedIn = CustodyLedger.Add(db, custody.Id, current.Id, OfficeLedgerKind.CarryForwardIn, remaining, clock.Today, OfficeLedgerSources.CarryIn,
                period.Id, caller.UserId, now, note, $"{period.Year}-{period.Month:00}");
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, OfficeLedgerEntryConfiguration.SourceIndex))
            {
                return OfficeProblems.WrongStatus("This month's money was already carried forward.");
            }

            log.EntryPosted(caller.UserId, custody.Id, carriedOut.Id, carriedOut.Kind.ToString());
            log.EntryPosted(caller.UserId, custody.Id, carriedIn.Id, carriedIn.Kind.ToString());
        }

        CustodyPeriods.Close(period, settlement, remaining, caller.UserId, note, now);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        await CustodyLedger.CheckLowBalanceAsync(db, custody, alerts, log, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        log.PeriodSettled(caller.UserId, period.Id, settlement.ToString());
        return TypedResults.Ok((await DetailAsync(db, users, payments, alerts, caller, periodId, settings.Value, clock, log, cancellationToken))!);
    }

    /// <summary>One month with its expenses and movements, if the caller may see its custody.</summary>
    internal static async Task<OfficePeriodDetail?> DetailAsync(
        OfficeDbContext db, IUserDirectory users, IPaymentRequests payments, OfficeAlerts alerts, OfficeCaller caller, int periodId, OfficeOptions options,
        CompanyClock clock, OfficeLog log, CancellationToken cancellationToken)
    {
        var custodyId = await db.Periods.AsNoTracking().Where(period => period.Id == periodId).Select(period => (int?)period.CustodyId).SingleOrDefaultAsync(cancellationToken);
        if (custodyId is null || !await OfficeCustodyEndpoints.Visible(db, caller).AnyAsync(custody => custody.Id == custodyId, cancellationToken))
        {
            return null;
        }

        await CustodyPeriods.RollIfNeededAsync(db, [custodyId.Value], clock.Today, clock.UtcNow, alerts, log, cancellationToken);
        var period = await db.Periods.AsNoTracking().SingleAsync(candidate => candidate.Id == periodId, cancellationToken);
        var view = (await ViewsAsync(db, users, payments, caller, [period], options, clock.Today, cancellationToken))[0];

        var expenses = await ExpenseQueries.RowsAsync(db, users,
            db.Expenses.AsNoTracking().Where(expense => expense.PeriodId == periodId)
                .OrderByDescending(expense => expense.ExpenseDate).ThenByDescending(expense => expense.Id).Take(500),
            cancellationToken);

        var entries = await db.LedgerEntries.AsNoTracking().Where(entry => entry.PeriodId == periodId)
            .OrderByDescending(entry => entry.EntryDate).ThenByDescending(entry => entry.Id).ToListAsync(cancellationToken);
        var people = caller.SeesAll ? await users.FindAsync(entries.Select(entry => entry.PostedBy).Distinct().ToList(), cancellationToken) : null;
        var movements = entries.Select(entry => new OfficePeriodMovement(
                entry.Id, entry.Kind.ToString(), entry.Amount, entry.EntryDate, entry.SourceType, entry.SourceId, entry.Reason,
                caller.SeesAll ? entry.Reference : null, entry.PostedAt, people is null ? null : OfficePersonRef.From(entry.PostedBy, people)))
            .ToList();

        return new OfficePeriodDetail(view, expenses, movements);
    }

    internal static async Task<IReadOnlyList<OfficePeriodView>> ViewsAsync(
        OfficeDbContext db, IUserDirectory users, IPaymentRequests payments, OfficeCaller caller, IReadOnlyList<OfficeCustodyPeriod> periods,
        OfficeOptions options, DateOnly today, CancellationToken cancellationToken)
    {
        if (periods.Count == 0)
        {
            return [];
        }

        var ids = periods.Select(period => period.Id).ToList();
        var custodyIds = periods.Select(period => period.CustodyId).Distinct().ToList();
        var custodies = await db.Custodies.AsNoTracking().Where(custody => custodyIds.Contains(custody.Id)).ToDictionaryAsync(custody => custody.Id, cancellationToken);
        var figures = await CustodyPeriods.FiguresAsync(db, ids, cancellationToken);
        var allocations = (await db.FundingRequests.AsNoTracking()
                .Where(funding => funding.PeriodId != null && ids.Contains(funding.PeriodId.Value)
                                  && funding.Status != OfficeFundingStatus.Rejected && funding.Status != OfficeFundingStatus.Cancelled)
                .ToListAsync(cancellationToken))
            .GroupBy(funding => funding.PeriodId!.Value)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(funding => funding.Id).First());
        var waiting = new Dictionary<int, int>();
        foreach (var period in periods.Where(period => period.Status != OfficePeriodStatus.Closed))
        {
            waiting[period.Id] = await CustodyPeriods.WaitingAsync(db, period.Id, cancellationToken);
        }

        var people = await users.FindAsync(periods.Where(period => period.ClosedBy != null).Select(period => period.ClosedBy!.Value).Distinct().ToList(), cancellationToken);
        var policy = options.Periods.UnusedBalancePolicy;
        IReadOnlyList<string> settleOptions = policy switch
        {
            OfficeUnusedBalancePolicy.Return => [SettleActions.Return],
            OfficeUnusedBalancePolicy.CarryForward => [SettleActions.CarryForward],
            _ => [SettleActions.Return, SettleActions.CarryForward],
        };

        var views = new List<OfficePeriodView>();
        foreach (var period in periods)
        {
            var custody = custodies[period.CustodyId];
            var reviewer = caller.IsReviewer && custody.HolderUserId != caller.UserId;
            var allocation = allocations.GetValueOrDefault(period.Id);
            OfficePeriodAllocationView? allocationView = null;
            if (allocation is not null)
            {
                var payment = caller.SeesAll && allocation.PaymentRequestKey is { } key ? await payments.FindByKeyAsync(key, cancellationToken) : null;
                allocationView = new OfficePeriodAllocationView(
                    allocation.Id, allocation.ApprovedAmount ?? allocation.RequestedAmount, allocation.Status.ToString(), allocation.DisbursedAmount,
                    allocation.ReceivedAmount, custody.HolderUserId == caller.UserId && allocation.Status == OfficeFundingStatus.Disbursed,
                    RowVersions.Format(allocation.RowVersion),
                    caller.SeesAll ? allocation.PaymentRequestNumber : null, payment?.Status);
            }

            var waitingCount = waiting.GetValueOrDefault(period.Id);
            views.Add(new OfficePeriodView(
                period.Id, period.CustodyId, custody.NameAr, custody.NameEn, period.Year, period.Month, period.StartsOn, period.EndsOn, period.Status.ToString(),
                period.Status == OfficePeriodStatus.Open && period.Contains(today), period.PlannedAllocation, figures[period.Id], allocationView, waitingCount,
                period.EndedAt, period.Settlement?.ToString(), period.SettledAmount, period.ClosedAt, OfficePersonRef.From(period.ClosedBy, people),
                period.ClosingNote,
                new OfficePeriodActions(
                    reviewer && caller.Can(PermissionAction.Approve) && caller.Principal.HasPermission(OfficePermissions.Payments, PermissionAction.Create)
                    && !caller.IsExecutive && custody.IsActive && period.Status == OfficePeriodStatus.Open && allocation is null,
                    reviewer && period.Status == OfficePeriodStatus.Closing && waitingCount == 0,
                    reviewer && period.Status == OfficePeriodStatus.Closing ? settleOptions : []),
                RowVersions.Format(period.RowVersion)));
        }

        return views;
    }
}
