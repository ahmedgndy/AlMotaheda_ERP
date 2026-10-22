using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Finance.Common;
using AlMotaheda.Modules.Finance.Contracts;
using AlMotaheda.Modules.Finance.Data;
using AlMotaheda.Modules.Finance.Data.Configurations;
using AlMotaheda.Modules.Finance.Features.Custody;
using AlMotaheda.Modules.Finance.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Finance.Features.Treasury;

/// <summary>One treasury account with its figures, as the treasury screen shows it.</summary>
internal sealed record TreasuryAccountView(
    int Id,
    string Account,
    string NameAr,
    string NameEn,
    decimal OpeningBalance,
    DateOnly OpeningDate,
    bool OpeningSet,
    decimal MoneyIn,
    decimal MoneyOut,
    decimal InternalTransfersOut,
    decimal InternalTransfersIn,
    decimal Balance,
    decimal Reserved,
    decimal Available,
    string? Notes,
    string RowVersion)
{
    public static TreasuryAccountView From(TreasuryAccount account, TreasuryFigures figures) => new(
        account.Id, account.Account.ToString(), account.NameAr, account.NameEn, figures.OpeningBalance, figures.OpeningDate, figures.OpeningSet,
        figures.MoneyIn, figures.MoneyOut, figures.InternalOut, figures.InternalIn, figures.Balance, figures.Reserved, figures.Available, account.Notes,
        RowVersions.Format(account.RowVersion));
}

/// <summary>
/// The company treasury: <c>GET /api/treasury</c> (finance See money) shows Bank and Cash with their opening and
/// running balances; <c>PUT /api/treasury/{account}/opening-balance</c> (finance Approve, an Executive) sets the
/// opening balance once. The running balance is never stored: it is the opening balance plus the ledger.
/// </summary>
internal static class TreasuryEndpoints
{
    internal sealed record Response(IReadOnlyList<TreasuryAccountView> Accounts, decimal Balance, decimal Available, string BalancePolicy);

    internal sealed record OpeningRequest(decimal? OpeningBalance, string? OpeningDate, string? Notes, string? RowVersion);

    internal sealed record DepositRequest(decimal? Amount, string? DepositDate, string? Source, string? Reference, string? Notes, string? IdempotencyKey);

    internal sealed record DepositView(int Id, string Number, string Account, decimal Amount, DateOnly DepositDate, string Source, string? Reference, string? Notes, DateTime RecordedAt, int RecordedBy)
    {
        public static DepositView From(TreasuryDeposit deposit) => new(
            deposit.Id, deposit.Number, deposit.Account.ToString(), deposit.Amount, deposit.DepositDate, deposit.Source, deposit.Reference, deposit.Notes, deposit.RecordedAt, deposit.RecordedBy);
    }

    internal sealed record DepositsResponse(IReadOnlyList<DepositView> Items, int Page, int PageSize, int TotalCount);

    public static void Map(IEndpointRouteBuilder api)
    {
        api.MapGet("/treasury", ListAsync)
            .RequirePermission(FinancePermissions.Finance, PermissionAction.SeeMoney)
            .WithName("GetTreasury")
            .WithSummary("The company treasury: Bank and Cash with opening balance, money in and out, internal transfers, balance, reserved and available");

        api.MapPut("/treasury/{account}/opening-balance", SetOpeningAsync)
            .RequirePermission(FinancePermissions.Finance, PermissionAction.Approve)
            .RequireRole(AccessRoles.Executive)
            .WithName("SetTreasuryOpeningBalance")
            .WithSummary("Set the opening balance of Bank or Cash once (finance Approve, Executive). 409 opening_balance_set");

        api.MapPost("/treasury/{account}/deposits", DepositAsync)
            .RequirePermission(FinancePermissions.Finance, PermissionAction.Approve)
            .RequireRole(AccessRoles.Executive)
            .WithName("RecordTreasuryDeposit")
            .WithSummary("Put money into Bank or Cash from outside (owner capital, a loan, a client's payment): money in, never income (finance Approve, Executive). Idempotent by idempotencyKey");

        api.MapGet("/treasury/deposits", ListDepositsAsync)
            .RequirePermission(FinancePermissions.Finance, PermissionAction.SeeMoney)
            .WithName("ListTreasuryDeposits")
            .WithSummary("The deposits into the treasury, newest first (account; page, pageSize)");
    }

    internal static async Task<Results<Ok<Response>, ProblemHttpResult>> ListAsync(
        HttpContext httpContext, FinanceDbContext db, CustodyBalancePolicy policy, CancellationToken cancellationToken)
    {
        if (FinanceCaller.From(httpContext.User) is null)
        {
            return ApiProblem.ForbiddenResult();
        }

        var accounts = await db.TreasuryAccounts.AsNoTracking().OrderBy(account => account.Id).ToListAsync(cancellationToken);
        var views = new List<TreasuryAccountView>();
        foreach (var account in accounts)
        {
            views.Add(TreasuryAccountView.From(account, await TreasuryQueries.FiguresAsync(db, account, cancellationToken)));
        }

        return TypedResults.Ok(new Response(views, views.Sum(view => view.Balance), views.Sum(view => view.Available), policy.Value));
    }

    internal static async Task<Results<Ok<TreasuryAccountView>, ValidationProblem, ProblemHttpResult>> SetOpeningAsync(
        string account,
        OpeningRequest request,
        HttpContext httpContext,
        FinanceDbContext db,
        IExecutiveCheck executives,
        CompanyClock clock,
        FinanceLog log,
        CancellationToken cancellationToken)
    {
        if (FinanceCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        LedgerAccount? wanted = Input.OptionalName<LedgerAccount>(account, errors, "account");
        if (wanted is LedgerAccount.Custody)
        {
            errors.Add("account", "account must be Bank or Cash.");
        }

        if (request.OpeningBalance is null)
        {
            errors.Add("openingBalance", "The opening balance is required.");
        }
        else if (request.OpeningBalance < 0 || request.OpeningBalance > NumberInput.MaxMoney || decimal.Round(request.OpeningBalance.Value, 2) != request.OpeningBalance.Value)
        {
            errors.Add("openingBalance", $"The opening balance must be between 0 and {NumberInput.MaxMoney:0.00} with at most two decimals.");
        }

        var openingDate = Input.RequiredDate(request.OpeningDate, errors, "openingDate", "The opening date");
        if (openingDate > clock.Today)
        {
            errors.Add("openingDate", "The opening date can't be in the future.");
        }

        var notes = Input.OptionalText(request.Notes, TreasuryAccountConfiguration.NotesMaxLength, errors, "notes", "Notes");
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (errors.HasErrors)
        {
            return FinanceProblems.Validation(errors);
        }

        // The token says Executive; the database must still agree before the company's opening figures are written.
        if (caller.SessionId is not { } session || !await executives.IsActiveExecutiveAsync(caller.UserId, session, cancellationToken))
        {
            return FinanceProblems.ExecutiveOnly();
        }

        var treasury = await db.TreasuryAccounts.SingleOrDefaultAsync(candidate => candidate.Account == wanted!.Value, cancellationToken);
        if (treasury is null)
        {
            return FinanceProblems.TreasuryAccountNotFound();
        }

        if (treasury.OpeningSetAt is not null)
        {
            return FinanceProblems.OpeningAlreadySet();
        }

        // Money already deposited before that day would silently drop out of the balance.
        if (await db.TreasuryDeposits.AnyAsync(deposit => deposit.Account == treasury.Account && deposit.DepositDate < openingDate!.Value, cancellationToken))
        {
            errors.Add("openingDate", "A deposit into this account is dated before that day: the opening date can't be after it.");
            return FinanceProblems.Validation(errors);
        }

        treasury.OpeningBalance = request.OpeningBalance!.Value;
        treasury.OpeningDate = openingDate!.Value;
        treasury.OpeningSetAt = clock.UtcNow;
        treasury.OpeningSetBy = caller.UserId;
        treasury.Notes = notes;
        db.Entry(treasury).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        log.OpeningBalanceSet(caller.UserId, treasury.Account.ToString());
        return TypedResults.Ok(TreasuryAccountView.From(treasury, await TreasuryQueries.FiguresAsync(db, treasury, cancellationToken)));
    }

    internal static async Task<Results<Created<DepositView>, Ok<DepositView>, ValidationProblem, ProblemHttpResult>> DepositAsync(
        string account,
        DepositRequest request,
        HttpContext httpContext,
        FinanceDbContext db,
        IExecutiveCheck executives,
        CompanyClock clock,
        FinanceLog log,
        CancellationToken cancellationToken)
    {
        if (FinanceCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        LedgerAccount? wanted = Input.OptionalName<LedgerAccount>(account, errors, "account");
        if (wanted is LedgerAccount.Custody)
        {
            errors.Add("account", "account must be Bank or Cash.");
        }

        var amount = NumberInput.RequiredMoney(request.Amount, errors, "amount", "The amount");
        var date = Input.Clean(request.DepositDate) is null ? clock.Today : Input.RequiredDate(request.DepositDate, errors, "depositDate", "The date");
        if (date > clock.Today)
        {
            errors.Add("depositDate", "The date can't be in the future.");
        }

        var source = Input.RequiredText(request.Source, TreasuryDepositConfiguration.SourceMaxLength, errors, "source", "Where the money came from");
        var reference = Input.OptionalText(request.Reference, TreasuryDepositConfiguration.ReferenceMaxLength, errors, "reference", "The reference");
        var notes = Input.OptionalText(request.Notes, TreasuryDepositConfiguration.NotesMaxLength, errors, "notes", "Notes");
        if (!Guid.TryParse(Input.Clean(request.IdempotencyKey), out var key) || key == Guid.Empty)
        {
            errors.Add("idempotencyKey", "idempotencyKey must be a GUID made by the client for this deposit.");
        }

        if (errors.HasErrors)
        {
            return FinanceProblems.Validation(errors);
        }

        if (caller.SessionId is not { } session || !await executives.IsActiveExecutiveAsync(caller.UserId, session, cancellationToken))
        {
            return FinanceProblems.ExecutiveOnly();
        }

        if (await db.TreasuryDeposits.AsNoTracking().SingleOrDefaultAsync(deposit => deposit.IdempotencyKey == key, cancellationToken) is { } repeat)
        {
            return repeat.RecordedBy == caller.UserId && repeat.Amount == amount && repeat.Account == wanted
                ? TypedResults.Ok(DepositView.From(repeat))
                : FinanceProblems.KeyUsedElsewhere();
        }

        var treasury = await db.TreasuryAccounts.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Account == wanted!.Value, cancellationToken);
        if (treasury is null)
        {
            return FinanceProblems.TreasuryAccountNotFound();
        }

        // The balance counts entries from the opening date on: an earlier deposit would not show.
        if (treasury.OpeningSetAt is not null && date < treasury.OpeningDate)
        {
            errors.Add("depositDate", $"The date can't be before the account's opening date ({treasury.OpeningDate:yyyy-MM-dd}).");
            return FinanceProblems.Validation(errors);
        }

        var now = clock.UtcNow;
        var deposit = new TreasuryDeposit
        {
            Account = wanted!.Value,
            Amount = amount!.Value,
            DepositDate = date!.Value,
            Source = source!,
            Reference = reference,
            Notes = notes,
            IdempotencyKey = key,
            RecordedAt = now,
            RecordedBy = caller.UserId,
        };

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.TreasuryDeposits.Add(deposit);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            db.LedgerEntries.Add(new LedgerEntry
            {
                EntryDate = deposit.DepositDate,
                Account = deposit.Account,
                Amount = deposit.Amount,
                CounterpartyType = PaymentSourceTypes.TreasuryDeposit,
                CounterpartyId = deposit.Id,
                Purpose = PaymentPurposes.TreasuryDeposit,
                SourceType = PaymentSourceTypes.TreasuryDeposit,
                TreasuryDepositId = deposit.Id,
                CreatedAt = now,
                CreatedBy = caller.UserId,
            });
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, TreasuryDepositConfiguration.IdempotencyKeyIndex))
        {
            await transaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            var first = await db.TreasuryDeposits.AsNoTracking().SingleAsync(candidate => candidate.IdempotencyKey == key, cancellationToken);
            return first.RecordedBy == caller.UserId ? TypedResults.Ok(DepositView.From(first)) : FinanceProblems.KeyUsedElsewhere();
        }

        await transaction.CommitAsync(cancellationToken);
        log.DepositRecorded(caller.UserId, deposit.Id, deposit.Account.ToString());
        var saved = await db.TreasuryDeposits.AsNoTracking().SingleAsync(candidate => candidate.Id == deposit.Id, cancellationToken);
        return TypedResults.Created($"/api/treasury/deposits/{deposit.Id}", DepositView.From(saved));
    }

    internal static async Task<Results<Ok<DepositsResponse>, ValidationProblem, ProblemHttpResult>> ListDepositsAsync(
        HttpContext httpContext, FinanceDbContext db, CancellationToken cancellationToken, string? account = null, int page = 1, int pageSize = Paging.DefaultPageSize)
    {
        if (FinanceCaller.From(httpContext.User) is null)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        LedgerAccount? wanted = Input.OptionalName<LedgerAccount>(account, errors, "account");
        Paging.Validate(page, pageSize, errors);
        if (errors.HasErrors)
        {
            return FinanceProblems.Validation(errors);
        }

        var query = db.TreasuryDeposits.AsNoTracking();
        if (wanted is { } only)
        {
            query = query.Where(deposit => deposit.Account == only);
        }

        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderByDescending(deposit => deposit.DepositDate).ThenByDescending(deposit => deposit.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        return TypedResults.Ok(new DepositsResponse(rows.Select(DepositView.From).ToList(), page, pageSize, total));
    }
}
