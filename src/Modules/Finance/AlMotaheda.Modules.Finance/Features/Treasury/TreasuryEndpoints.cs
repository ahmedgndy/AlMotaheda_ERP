using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Finance.Common;
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
}
