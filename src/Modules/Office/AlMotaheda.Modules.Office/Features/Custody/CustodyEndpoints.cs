using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Office.Common;
using AlMotaheda.Modules.Office.Data;
using AlMotaheda.Modules.Office.Data.Configurations;
using AlMotaheda.Modules.Office.Features.Alerts;
using AlMotaheda.Modules.Office.Features.Expenses;
using AlMotaheda.Modules.Office.Features.Ledger;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlMotaheda.Modules.Office.Features.Custody;

internal sealed record OfficeCustodyView(
    int Id,
    string NameAr,
    string NameEn,
    OfficePersonRef? Holder,
    string Status,
    OfficeCustodyBalance Balance,
    string? Notes,
    DateTime CreatedAt,
    DateTime? StatusChangedAt,
    OfficePersonRef? StatusChangedBy,
    string? StatusReason,
    string RowVersion);

internal sealed record OfficeLedgerEntryView(
    long Id, string Kind, decimal Amount, decimal RunningBalance, DateOnly EntryDate, string SourceType, int SourceId, long? ReversesEntryId,
    string? Reason, string? Reference, DateTime PostedAt, OfficePersonRef? PostedBy);

/// <summary>
/// The office's cash custodies. Reviewers (office Approve) open, change, suspend, reopen and close them, and record
/// cash handed back. The balance is never typed: it is the sum of the ledger, shown with what is on its way.
/// </summary>
internal static class OfficeCustodyEndpoints
{
    internal sealed record SaveRequest(string? NameAr, string? NameEn, int? HolderUserId, decimal? LowBalanceThreshold, string? Notes, string? RowVersion);

    internal sealed record StatusRequest(string? Reason, string? RowVersion);

    internal sealed record ReturnRequest(decimal? Amount, string? ReturnedOn, string? Reason, string? Reference, Guid? IdempotencyKey);

    internal sealed record CustodyList(IReadOnlyList<OfficeCustodyView> Items);

    internal sealed record LedgerPage(IReadOnlyList<OfficeLedgerEntryView> Items, int Total, int Page, int PageSize, decimal OpeningBalance, decimal ClosingBalance);

    public static void Map(IEndpointRouteBuilder api)
    {
        api.MapGet("/office/custodies", ListAsync)
            .RequirePermission(OfficePermissions.Module, PermissionAction.View)
            .WithName("ListOfficeCustodies")
            .WithSummary("The custodies the caller may see, with their balances");

        api.MapGet("/office/custodies/{custodyId:int}", GetAsync)
            .RequirePermission(OfficePermissions.Module, PermissionAction.View)
            .WithName("GetOfficeCustody")
            .WithSummary("One custody with its balance");

        api.MapPost("/office/custodies", CreateAsync)
            .RequirePermission(OfficePermissions.Module, PermissionAction.Approve)
            .WithName("CreateOfficeCustody")
            .WithSummary("Open a custody for a custodian (office Approve). It starts at zero; fund it with a funding request");

        api.MapPut("/office/custodies/{custodyId:int}", UpdateAsync)
            .RequirePermission(OfficePermissions.Module, PermissionAction.Approve)
            .WithName("UpdateOfficeCustody")
            .WithSummary("Rename a custody, change its low-balance threshold or hand it to another custodian (office Approve)");

        api.MapPost("/office/custodies/{custodyId:int}/suspend", (int custodyId, StatusRequest request, HttpContext httpContext, OfficeDbContext db,
                IUserDirectory users, IOptions<OfficeOptions> settings, CompanyClock clock, OfficeLog log, CancellationToken cancellationToken) =>
                MoveAsync(custodyId, request, OfficeCustodyStatus.Suspended, httpContext, db, users, settings, clock, log, cancellationToken))
            .RequirePermission(OfficePermissions.Module, PermissionAction.Approve)
            .WithName("SuspendOfficeCustody")
            .WithSummary("Stop new records on a custody, with a reason (office Approve)");

        api.MapPost("/office/custodies/{custodyId:int}/reopen", (int custodyId, StatusRequest request, HttpContext httpContext, OfficeDbContext db,
                IUserDirectory users, IOptions<OfficeOptions> settings, CompanyClock clock, OfficeLog log, CancellationToken cancellationToken) =>
                MoveAsync(custodyId, request, OfficeCustodyStatus.Active, httpContext, db, users, settings, clock, log, cancellationToken))
            .RequirePermission(OfficePermissions.Module, PermissionAction.Approve)
            .WithName("ReopenOfficeCustody")
            .WithSummary("Reopen a suspended custody (office Approve)");

        api.MapPost("/office/custodies/{custodyId:int}/close", (int custodyId, StatusRequest request, HttpContext httpContext, OfficeDbContext db,
                IUserDirectory users, IOptions<OfficeOptions> settings, CompanyClock clock, OfficeLog log, CancellationToken cancellationToken) =>
                MoveAsync(custodyId, request, OfficeCustodyStatus.Closed, httpContext, db, users, settings, clock, log, cancellationToken))
            .RequirePermission(OfficePermissions.Module, PermissionAction.Approve)
            .WithName("CloseOfficeCustody")
            .WithSummary("Close a custody with a zero balance and nothing waiting (office Approve)");

        api.MapGet("/office/custodies/{custodyId:int}/ledger", LedgerAsync)
            .RequirePermission(OfficePermissions.Module, PermissionAction.View)
            .WithName("GetOfficeCustodyLedger")
            .WithSummary("The posted movements of a custody, newest first, with running balances (from, to, page, pageSize)");

        api.MapPost("/office/custodies/{custodyId:int}/returns", ReturnAsync)
            .RequirePermission(OfficePermissions.Module, PermissionAction.Approve)
            .WithName("RecordOfficeCashReturn")
            .WithSummary("Record cash handed back to the company from a custody (office Approve)");
    }

    internal static async Task<Results<Ok<CustodyList>, ProblemHttpResult>> ListAsync(
        HttpContext httpContext, OfficeDbContext db, IUserDirectory users, IOptions<OfficeOptions> settings, CancellationToken cancellationToken,
        bool includeClosed = false)
    {
        if (OfficeCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var custodies = await Visible(db, caller)
            .Where(custody => includeClosed || custody.Status != OfficeCustodyStatus.Closed)
            .OrderBy(custody => custody.NameEn)
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(new CustodyList(await ViewsAsync(db, users, custodies, settings.Value, cancellationToken)));
    }

    internal static async Task<Results<Ok<OfficeCustodyView>, ProblemHttpResult>> GetAsync(
        int custodyId, HttpContext httpContext, OfficeDbContext db, IUserDirectory users, IOptions<OfficeOptions> settings, CancellationToken cancellationToken)
    {
        if (OfficeCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var custody = await Visible(db, caller).SingleOrDefaultAsync(candidate => candidate.Id == custodyId, cancellationToken);
        return custody is null ? OfficeProblems.CustodyNotFound() : TypedResults.Ok((await ViewsAsync(db, users, [custody], settings.Value, cancellationToken))[0]);
    }

    internal static async Task<Results<Created<OfficeCustodyView>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        SaveRequest request, HttpContext httpContext, OfficeDbContext db, IUserDirectory users, IOptions<OfficeOptions> settings, CompanyClock clock,
        OfficeLog log, CancellationToken cancellationToken)
    {
        if (OfficeCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var (nameAr, nameEn, threshold, notes) = Fields(request, errors);
        if (request.HolderUserId is null)
        {
            errors.Add("holderUserId", "Choose the custodian.");
        }

        if (errors.HasErrors)
        {
            return OfficeProblems.Validation(errors);
        }

        if (await HolderRefusalAsync(users, request.HolderUserId!.Value, caller, cancellationToken) is { } refused)
        {
            return refused;
        }

        var custody = new OfficeCustody
        {
            NameAr = nameAr!,
            NameEn = nameEn!,
            HolderUserId = request.HolderUserId.Value,
            LowBalanceThreshold = threshold,
            Notes = notes,
            CreatedAt = clock.UtcNow,
            CreatedBy = caller.UserId,
        };
        db.Custodies.Add(custody);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, OfficeCustodyConfiguration.NameArIndex)
                                                  || SqlViolations.IsUnique(exception, OfficeCustodyConfiguration.NameEnIndex))
        {
            errors.Add("nameAr", "A custody with this name already exists.");
            return OfficeProblems.Validation(errors);
        }

        log.CustodyCreated(caller.UserId, custody.Id);
        return TypedResults.Created($"/api/office/custodies/{custody.Id}", (await ViewsAsync(db, users, [custody], settings.Value, cancellationToken))[0]);
    }

    internal static async Task<Results<Ok<OfficeCustodyView>, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        int custodyId, SaveRequest request, HttpContext httpContext, OfficeDbContext db, IUserDirectory users, IOptions<OfficeOptions> settings,
        OfficeLog log, CancellationToken cancellationToken)
    {
        if (OfficeCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var (nameAr, nameEn, threshold, notes) = Fields(request, errors);
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (errors.HasErrors)
        {
            return OfficeProblems.Validation(errors);
        }

        var custody = await db.Custodies.SingleOrDefaultAsync(candidate => candidate.Id == custodyId, cancellationToken);
        if (custody is null)
        {
            return OfficeProblems.CustodyNotFound();
        }

        if (custody.Status == OfficeCustodyStatus.Closed)
        {
            return OfficeProblems.Inactive(custody.Status.ToString());
        }

        if (request.HolderUserId is { } holder && holder != custody.HolderUserId)
        {
            if (await HolderRefusalAsync(users, holder, caller, cancellationToken) is { } refused)
            {
                return refused;
            }

            // A new custodian must not inherit someone else's open records under their name.
            var open = await OpenRecordsAsync(db, custody.Id, cancellationToken);
            if (open > 0)
            {
                return OfficeProblems.WrongStatus($"Finish the {open} waiting records of this custody before handing it to another custodian (count the cash first).");
            }

            custody.HolderUserId = holder;
        }

        custody.NameAr = nameAr!;
        custody.NameEn = nameEn!;
        custody.LowBalanceThreshold = threshold;
        custody.Notes = notes;
        db.Entry(custody).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, OfficeCustodyConfiguration.NameArIndex)
                                                  || SqlViolations.IsUnique(exception, OfficeCustodyConfiguration.NameEnIndex))
        {
            errors.Add("nameAr", "A custody with this name already exists.");
            return OfficeProblems.Validation(errors);
        }

        log.CustodyUpdated(caller.UserId, custody.Id);
        return TypedResults.Ok((await ViewsAsync(db, users, [custody], settings.Value, cancellationToken))[0]);
    }

    internal static async Task<Results<Ok<OfficeCustodyView>, ValidationProblem, ProblemHttpResult>> MoveAsync(
        int custodyId,
        StatusRequest request,
        OfficeCustodyStatus target,
        HttpContext httpContext,
        OfficeDbContext db,
        IUserDirectory users,
        IOptions<OfficeOptions> settings,
        CompanyClock clock,
        OfficeLog log,
        CancellationToken cancellationToken)
    {
        if (OfficeCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var reason = OfficeInput.Reason(request.Reason, errors, "reason");
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (errors.HasErrors)
        {
            return OfficeProblems.Validation(errors);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await CustodyLedger.LockAsync(db, custodyId, cancellationToken))
        {
            return OfficeProblems.TryAgainLater();
        }

        var custody = await db.Custodies.SingleOrDefaultAsync(candidate => candidate.Id == custodyId, cancellationToken);
        if (custody is null)
        {
            return OfficeProblems.CustodyNotFound();
        }

        if (custody.Status == target)
        {
            return TypedResults.Ok((await ViewsAsync(db, users, [custody], settings.Value, cancellationToken))[0]);
        }

        if (!custody.RowVersion.SequenceEqual(rowVersion!))
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        var allowed = (custody.Status, target) switch
        {
            (OfficeCustodyStatus.Active, OfficeCustodyStatus.Suspended) => true,
            (OfficeCustodyStatus.Suspended, OfficeCustodyStatus.Active) => true,
            (OfficeCustodyStatus.Active or OfficeCustodyStatus.Suspended, OfficeCustodyStatus.Closed) => true,
            _ => false,
        };
        if (!allowed)
        {
            return OfficeProblems.WrongStatus($"A {custody.Status} custody can't be moved to {target}.");
        }

        if (target == OfficeCustodyStatus.Closed)
        {
            var balance = await CustodyLedger.RecordedAsync(db, custody.Id, cancellationToken);
            var open = await OpenRecordsAsync(db, custody.Id, cancellationToken);
            if (balance != 0 || open > 0)
            {
                return OfficeProblems.NotEmpty(balance, open);
            }
        }

        custody.Status = target;
        custody.StatusChangedAt = clock.UtcNow;
        custody.StatusChangedBy = caller.UserId;
        custody.StatusReason = reason;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        log.CustodyMoved(caller.UserId, custody.Id, target.ToString());
        return TypedResults.Ok((await ViewsAsync(db, users, [custody], settings.Value, cancellationToken))[0]);
    }

    internal static async Task<Results<Ok<LedgerPage>, ValidationProblem, ProblemHttpResult>> LedgerAsync(
        int custodyId, HttpContext httpContext, OfficeDbContext db, IUserDirectory users, CancellationToken cancellationToken,
        string? from = null, string? to = null, int page = 1, int pageSize = 50)
    {
        if (OfficeCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var fromDate = Input.OptionalDate(from, errors, "from", "from");
        var toDate = Input.OptionalDate(to, errors, "to", "to");
        Paging.Validate(page, pageSize, errors);
        if (errors.HasErrors)
        {
            return OfficeProblems.Validation(errors);
        }

        if (!await Visible(db, caller).AnyAsync(custody => custody.Id == custodyId, cancellationToken))
        {
            return OfficeProblems.CustodyNotFound();
        }

        var all = db.LedgerEntries.AsNoTracking().Where(entry => entry.CustodyId == custodyId);
        var opening = fromDate is { } first ? await all.Where(entry => entry.EntryDate < first).SumAsync(entry => (decimal?)entry.Amount, cancellationToken) ?? 0m : 0m;
        var inRange = all.Where(entry => (fromDate == null || entry.EntryDate >= fromDate) && (toDate == null || entry.EntryDate <= toDate));

        // Running balances need every earlier entry, so the range is read in order once (a custody has few entries a month).
        var entries = await inRange.OrderBy(entry => entry.EntryDate).ThenBy(entry => entry.Id).ToListAsync(cancellationToken);
        var running = opening;
        var withBalance = entries.Select(entry => (Entry: entry, Balance: running += entry.Amount)).ToList();
        var people = await users.FindAsync(entries.Select(entry => entry.PostedBy).Distinct().ToList(), cancellationToken);
        var items = withBalance.AsEnumerable().Reverse().Skip((page - 1) * pageSize).Take(pageSize)
            .Select(row => new OfficeLedgerEntryView(row.Entry.Id, row.Entry.Kind.ToString(), row.Entry.Amount, row.Balance, row.Entry.EntryDate, row.Entry.SourceType,
                row.Entry.SourceId, row.Entry.ReversesEntryId, row.Entry.Reason, row.Entry.Reference, row.Entry.PostedAt, OfficePersonRef.From(row.Entry.PostedBy, people)))
            .ToList();
        return TypedResults.Ok(new LedgerPage(items, entries.Count, page, pageSize, opening, running));
    }

    internal static async Task<Results<Created<OfficeLedgerEntryView>, Ok<OfficeLedgerEntryView>, ValidationProblem, ProblemHttpResult>> ReturnAsync(
        int custodyId, ReturnRequest request, HttpContext httpContext, OfficeDbContext db, IUserDirectory users, OfficeAlerts alerts, CompanyClock clock,
        OfficeLog log, CancellationToken cancellationToken)
    {
        if (OfficeCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var amount = NumberInput.RequiredMoney(request.Amount, errors, "amount", "The amount");
        var returnedOn = Input.OptionalDate(request.ReturnedOn, errors, "returnedOn", "The date") ?? clock.Today;
        var reason = OfficeInput.Reason(request.Reason, errors, "reason");
        var reference = Input.OptionalText(request.Reference, OfficeLengths.Reference, errors, "reference", "The reference");
        var key = OfficeInput.Key(request.IdempotencyKey, errors);
        if (errors.HasErrors)
        {
            return OfficeProblems.Validation(errors);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await CustodyLedger.LockAsync(db, custodyId, cancellationToken))
        {
            return OfficeProblems.TryAgainLater();
        }

        var custody = await db.Custodies.SingleOrDefaultAsync(candidate => candidate.Id == custodyId, cancellationToken);
        if (custody is null)
        {
            return OfficeProblems.CustodyNotFound();
        }

        if (custody.HolderUserId == caller.UserId)
        {
            return OfficeProblems.ReviewerIsSubmitter();
        }

        if (await db.CashReturns.AsNoTracking().SingleOrDefaultAsync(cashReturn => cashReturn.IdempotencyKey == key, cancellationToken) is { } repeat)
        {
            if (repeat.CustodyId != custodyId || repeat.Amount != amount || repeat.CreatedBy != caller.UserId)
            {
                return OfficeProblems.KeyUsedElsewhere();
            }

            var posted = await db.LedgerEntries.AsNoTracking().SingleAsync(entry => entry.SourceType == OfficeLedgerSources.CashReturn && entry.SourceId == repeat.Id, cancellationToken);
            return TypedResults.Ok(await EntryViewAsync(users, posted, cancellationToken));
        }

        if (custody.Status == OfficeCustodyStatus.Closed)
        {
            return OfficeProblems.Inactive(custody.Status.ToString());
        }

        var balance = await CustodyLedger.RecordedAsync(db, custody.Id, cancellationToken);
        if (amount > balance)
        {
            return OfficeProblems.NotEnoughCash(balance, amount!.Value);
        }

        var now = clock.UtcNow;
        var cashReturn = new OfficeCashReturn
        {
            CustodyId = custody.Id,
            Amount = amount!.Value,
            ReturnedOn = returnedOn,
            Reason = reason!,
            Reference = reference,
            IdempotencyKey = key!.Value,
            CreatedAt = now,
            CreatedBy = caller.UserId,
        };
        db.CashReturns.Add(cashReturn);
        await db.SaveChangesAsync(cancellationToken);

        var entry = CustodyLedger.Add(db, custody.Id, OfficeLedgerKind.Return, -cashReturn.Amount, returnedOn, OfficeLedgerSources.CashReturn, cashReturn.Id,
            caller.UserId, now, reason, reference);
        await db.SaveChangesAsync(cancellationToken);
        await CustodyLedger.CheckLowBalanceAsync(db, custody, balance - cashReturn.Amount, alerts, log, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        log.CashReturned(caller.UserId, custody.Id, cashReturn.Id);
        log.EntryPosted(caller.UserId, custody.Id, entry.Id, entry.Kind.ToString());

        return TypedResults.Created($"/api/office/custodies/{custody.Id}/ledger", await EntryViewAsync(users, entry, cancellationToken));
    }

    internal static IQueryable<OfficeCustody> Visible(OfficeDbContext db, OfficeCaller caller) =>
        caller.SeesAll ? db.Custodies.AsNoTracking() : db.Custodies.AsNoTracking().Where(custody => custody.HolderUserId == caller.UserId);

    internal static async Task<IReadOnlyList<OfficeCustodyView>> ViewsAsync(
        OfficeDbContext db, IUserDirectory users, IReadOnlyList<OfficeCustody> custodies, OfficeOptions options, CancellationToken cancellationToken)
    {
        var balances = await CustodyLedger.BalancesAsync(db, custodies, options.ReservePendingExpenses, cancellationToken);
        var people = await users.FindAsync(custodies.SelectMany(custody => new[] { custody.HolderUserId, custody.StatusChangedBy ?? 0 }).Where(id => id != 0).Distinct().ToList(), cancellationToken);
        return custodies.Select(custody => new OfficeCustodyView(
                custody.Id, custody.NameAr, custody.NameEn, OfficePersonRef.From(custody.HolderUserId, people), custody.Status.ToString(), balances[custody.Id],
                custody.Notes, custody.CreatedAt, custody.StatusChangedAt, OfficePersonRef.From(custody.StatusChangedBy, people), custody.StatusReason,
                RowVersions.Format(custody.RowVersion)))
            .ToList();
    }

    /// <summary>Records still waiting on a custody: expenses not decided or not posted, funding not received, repairs not done, counts not reviewed.</summary>
    internal static async Task<int> OpenRecordsAsync(OfficeDbContext db, int custodyId, CancellationToken cancellationToken) =>
        await db.Expenses.CountAsync(expense => expense.CustodyId == custodyId && expense.PostingStatus == OfficePostingStatus.NotPosted
                                                && expense.Status != OfficeExpenseStatus.Rejected, cancellationToken)
        + await db.FundingRequests.CountAsync(funding => funding.CustodyId == custodyId
                                                         && (funding.Status == OfficeFundingStatus.Submitted || funding.Status == OfficeFundingStatus.Approved
                                                             || funding.Status == OfficeFundingStatus.Disbursed), cancellationToken)
        + await db.SpendRequests.CountAsync(spend => spend.CustodyId == custodyId
                                                     && (spend.Status == OfficeSpendStatus.Submitted || spend.Status == OfficeSpendStatus.Approved), cancellationToken)
        + await db.CashCounts.CountAsync(count => count.CustodyId == custodyId && count.Status == OfficeCashCountStatus.Submitted, cancellationToken);

    private static (string? NameAr, string? NameEn, decimal Threshold, string? Notes) Fields(SaveRequest request, ValidationErrors errors) =>
        (Input.RequiredText(request.NameAr, OfficeLengths.Name, errors, "nameAr", "The Arabic name"),
            Input.RequiredText(request.NameEn, OfficeLengths.Name, errors, "nameEn", "The English name"),
            OfficeInput.NonNegativeMoney(request.LowBalanceThreshold, errors, "lowBalanceThreshold", "The low-balance threshold"),
            Input.OptionalText(request.Notes, OfficeLengths.Note, errors, "notes", "The notes"));

    /// <summary>The custodian must be an active Office Admin (who records office expenses), and not the reviewer opening it.</summary>
    private static async Task<ProblemHttpResult?> HolderRefusalAsync(IUserDirectory users, int holderUserId, OfficeCaller caller, CancellationToken cancellationToken)
    {
        if (holderUserId == caller.UserId)
        {
            return OfficeProblems.ReviewerIsSubmitter();
        }

        var found = await users.FindAsync([holderUserId], cancellationToken);
        return found.TryGetValue(holderUserId, out var holder) && holder.IsActive && holder.RoleCode == AccessRoles.OfficeAdmin
            ? null
            : OfficeProblems.HolderNotValid();
    }

    private static async Task<OfficeLedgerEntryView> EntryViewAsync(IUserDirectory users, OfficeLedgerEntry entry, CancellationToken cancellationToken)
    {
        var people = await users.FindAsync([entry.PostedBy], cancellationToken);
        return new OfficeLedgerEntryView(entry.Id, entry.Kind.ToString(), entry.Amount, 0m, entry.EntryDate, entry.SourceType, entry.SourceId, entry.ReversesEntryId,
            entry.Reason, entry.Reference, entry.PostedAt, OfficePersonRef.From(entry.PostedBy, people));
    }
}
