using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.Modules.Finance.Common;
using AlMotaheda.Modules.Finance.Contracts;
using AlMotaheda.Modules.Finance.Data;
using AlMotaheda.Modules.Finance.Security;
using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Finance.Features.Ledger;

/// <summary>
/// <c>GET /api/ledger</c>: the ledger, newest first, with the totals of the whole filtered set (not only the page):
/// money out, money in and net. Needs finance See money; non-company-wide callers see their projects only.
/// </summary>
internal static class ListLedgerEntries
{
    internal sealed record Filter(
        DateOnly? From, DateOnly? To, int? ProjectId, LedgerAccount? Account, string? CounterpartyType, int? CounterpartyId,
        IReadOnlySet<int>? VisibleProjects);

    /// <param name="IsInternalTransfer">A custody advance or return: money moved inside the company, never an expense.</param>
    internal sealed record EntryView(
        long Id, DateOnly EntryDate, string Account, decimal Amount, string CounterpartyType, int CounterpartyId, int? ProjectId,
        string Purpose, int? PaymentRequestId, string SourceType, int? SourcePaymentId, long? ReversesEntryId, DateTime CreatedAt,
        int? CustodyTransferId = null, int? CustodyAccountId = null, bool IsInternalTransfer = false);

    internal sealed record Totals(decimal MoneyOut, decimal MoneyIn, decimal Net, int Count);

    internal sealed record Response(IReadOnlyList<EntryView> Items, int Page, int PageSize, int TotalCount, Totals Totals);

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/ledger", HandleAsync)
            .RequirePermission(FinancePermissions.Finance, PermissionAction.SeeMoney)
            .WithName(nameof(ListLedgerEntries))
            .WithSummary("List ledger entries (from, to, projectId, account, counterpartyType, counterpartyId; page, pageSize) with totals of the filtered set");

    internal static async Task<Results<Ok<Response>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        HttpContext httpContext,
        FinanceDbContext db,
        IVisibleProjects projects,
        CancellationToken cancellationToken,
        string? from = null,
        string? to = null,
        int? projectId = null,
        string? account = null,
        string? counterpartyType = null,
        int? counterpartyId = null,
        int page = 1,
        int pageSize = Paging.DefaultPageSize)
    {
        if (FinanceCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var fromDate = Input.OptionalDate(from, errors, "from", "from");
        var toDate = Input.OptionalDate(to, errors, "to", "to");
        if (fromDate is { } start && toDate is { } end && end < start)
        {
            errors.Add("to", "to must be on or after from.");
        }

        var wantedAccount = Input.OptionalName<LedgerAccount>(account, errors, "account");
        var wantedCounterparty = Input.Clean(counterpartyType);
        if (wantedCounterparty is not null && !PayeeTypes.All.Contains(wantedCounterparty))
        {
            errors.Add("counterpartyType", $"counterpartyType must be one of: {string.Join(", ", PayeeTypes.All)}.");
        }

        Paging.Validate(page, pageSize, errors);
        if (errors.HasErrors)
        {
            return FinanceProblems.Validation(errors);
        }

        var visible = await caller.VisibleProjectsAsync(projects, cancellationToken);
        var query = Query(db, new Filter(fromDate, toDate, projectId, wantedAccount, wantedCounterparty, counterpartyId, visible));

        var totals = await query
            .GroupBy(_ => 1)
            .Select(group => new Totals(
                group.Where(entry => entry.Amount < 0).Sum(entry => entry.Amount),
                group.Where(entry => entry.Amount > 0).Sum(entry => entry.Amount),
                group.Sum(entry => entry.Amount),
                group.Count()))
            .SingleOrDefaultAsync(cancellationToken) ?? new Totals(0, 0, 0, 0);

        var items = await query
            .OrderByDescending(entry => entry.EntryDate)
            .ThenByDescending(entry => entry.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(entry => new EntryView(
                entry.Id, entry.EntryDate, entry.Account.ToString(), entry.Amount, entry.CounterpartyType, entry.CounterpartyId,
                entry.ProjectId, entry.Purpose, entry.PaymentRequestId, entry.SourceType, entry.SourcePaymentId, entry.ReversesEntryId,
                entry.CreatedAt, entry.CustodyTransferId, entry.CustodyAccountId,
                entry.Purpose == PaymentPurposes.CustodyAdvance || entry.Purpose == PaymentPurposes.CustodyReturn))
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(new Response(items, page, pageSize, totals.Count, totals));
    }

    /// <summary>The filtered ledger. A separate method so a test can check its SQL translation.</summary>
    internal static IQueryable<LedgerEntry> Query(FinanceDbContext db, Filter filter)
    {
        var query = db.LedgerEntries.AsNoTracking();

        if (filter.VisibleProjects is { } visible)
        {
            query = query.Where(entry => entry.ProjectId != null && visible.Contains(entry.ProjectId.Value));
        }

        if (filter.From is { } from)
        {
            query = query.Where(entry => entry.EntryDate >= from);
        }

        if (filter.To is { } to)
        {
            query = query.Where(entry => entry.EntryDate <= to);
        }

        if (filter.ProjectId is { } projectId)
        {
            query = query.Where(entry => entry.ProjectId == projectId);
        }

        if (filter.Account is { } account)
        {
            query = query.Where(entry => entry.Account == account);
        }

        if (filter.CounterpartyType is { } type)
        {
            query = query.Where(entry => entry.CounterpartyType == type);
        }

        if (filter.CounterpartyId is { } id)
        {
            query = query.Where(entry => entry.CounterpartyId == id);
        }

        return query;
    }
}
