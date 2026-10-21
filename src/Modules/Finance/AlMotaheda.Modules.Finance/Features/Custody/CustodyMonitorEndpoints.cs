using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.BuildingBlocks.Validation;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Finance.Common;
using AlMotaheda.Modules.Finance.Contracts;
using AlMotaheda.Modules.Finance.Data;
using AlMotaheda.Modules.Finance.Requests;
using AlMotaheda.Modules.Finance.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Finance.Features.Custody;

/// <summary>One custody holder on the central page: who, what they hold now, what happened in the period, what needs attention.</summary>
internal sealed record CustodyMonitorRow(
    int AccountId,
    string Number,
    string Kind,
    string LedgerOwner,
    int? HolderUserId,
    PersonView? Holder,
    string? HolderRole,
    int? ReferenceId,
    int? ProjectId,
    string NameAr,
    string NameEn,
    string Status,
    decimal Posted,
    decimal Pending,
    int PendingCount,
    decimal Reserved,
    decimal InTransit,
    decimal Available,
    decimal Owed,
    decimal ReceivedInPeriod,
    decimal ReturnedInPeriod,
    decimal TransferredOutInPeriod,
    decimal SpentInPeriod,
    decimal Unsupported,
    int UnsupportedCount,
    int OpenTransfers,
    DateOnly? LastEntryDate,
    DateOnly? LastPeriodEnd,
    string DetailPath);

internal sealed record CustodyMonitorTotals(decimal Posted, decimal Pending, decimal Owed, decimal ReceivedInPeriod, decimal SpentInPeriod, decimal Unsupported, int Accounts);

internal sealed record CustodyMonitorResponse(
    DateOnly From, DateOnly To, IReadOnlyList<CustodyMonitorRow> Items, int Page, int PageSize, int TotalCount, CustodyMonitorTotals Totals, string BalancePolicy);

/// <summary>One line of a holder's unified statement, newest first, whichever module keeps the ledger.</summary>
internal sealed record CustodyMonitorLine(
    DateOnly EntryDate, string Kind, string Status, decimal Amount, string SourceType, int SourceId, string? Reference, string? Notes, bool HasEvidence, PersonView? Actor, int? ProjectId);

internal sealed record CustodyMonitorTransfer(int Id, string Number, string Status, string Purpose, decimal Amount, bool Incoming, DateTime RequestedAt, PersonView? RequestedBy);

internal sealed record CustodyMonitorDetail(
    CustodyMonitorRow Account,
    CustodyAccountView View,
    IReadOnlyList<CustodyMonitorLine> Statement,
    int StatementCount,
    int Page,
    int PageSize,
    IReadOnlyList<CustodyMonitorTransfer> OpenTransfers,
    IReadOnlyList<AdjustmentView> Adjustments,
    IReadOnlyList<CashCountView> CashCounts,
    IReadOnlyList<PeriodCloseView> PeriodCloses,
    string BalancePolicy);

/// <summary>
/// The central custody page of the Executive (section 16): <c>GET /api/custody-monitor</c> lists every person who
/// holds company money with their balance, the period's figures and what needs attention;
/// <c>GET /api/custody-monitor/{accountId}</c> opens one holder's full history. Every figure is a sum of ledger
/// entries read from the module that keeps them, never a stored total. Needs custody See money or finance See money.
/// </summary>
internal static class CustodyMonitorEndpoints
{
    public static void Map(IEndpointRouteBuilder api)
    {
        api.MapGet("/custody-monitor", ListAsync)
            .RequireAnyPermission((FinancePermissions.Custody, PermissionAction.SeeMoney), (FinancePermissions.Finance, PermissionAction.SeeMoney))
            .WithName("ListCustodyMonitor")
            .WithSummary("Every custody holder with balance, period figures, pending and unsupported spending (from, to, kind, status, holderUserId, projectId, attention; page, pageSize)");

        api.MapGet("/custody-monitor/{accountId:int}", GetAsync)
            .RequireAnyPermission((FinancePermissions.Custody, PermissionAction.SeeMoney), (FinancePermissions.Finance, PermissionAction.SeeMoney))
            .WithName("GetCustodyMonitorAccount")
            .WithSummary("One holder's full custody history: statement (from, to; page, pageSize), open transfers, adjustments, cash counts, period closes");
    }

    internal static async Task<Results<Ok<CustodyMonitorResponse>, ValidationProblem, ProblemHttpResult>> ListAsync(
        HttpContext httpContext,
        FinanceDbContext db,
        CustodyLedgers ledgers,
        CustodyBalancePolicy policy,
        IUserDirectory users,
        CompanyClock clock,
        CancellationToken cancellationToken,
        string? from = null,
        string? to = null,
        string? kind = null,
        string? status = null,
        int? holderUserId = null,
        int? projectId = null,
        bool attention = false,
        int page = 1,
        int pageSize = 50)
    {
        if (FinanceCaller.From(httpContext.User) is null)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var (first, last) = Period(from, to, clock, errors);
        if (kind is not null && !CustodyAccountKinds.All.Contains(kind))
        {
            errors.Add("kind", $"kind must be one of: {string.Join(", ", CustodyAccountKinds.All)}.");
        }

        CustodyAccountStatus? wantedStatus = null;
        if (status is not null)
        {
            wantedStatus = Input.OptionalName<CustodyAccountStatus>(status, errors, "status");
        }

        Paging.Validate(page, pageSize, errors);
        if (errors.HasErrors)
        {
            return FinanceProblems.Validation(errors);
        }

        var query = db.CustodyAccounts.AsNoTracking();
        query = wantedStatus is { } wanted ? query.Where(account => account.Status == wanted) : query.Where(account => account.Status == CustodyAccountStatus.Active);
        if (kind is not null)
        {
            query = query.Where(account => account.Kind == kind);
        }

        if (holderUserId is { } holder)
        {
            query = query.Where(account => account.HolderUserId == holder);
        }

        var accounts = await query.OrderBy(account => account.Kind).ThenBy(account => account.Id).ToListAsync(cancellationToken);
        var rows = new List<CustodyMonitorRow>();
        foreach (var account in accounts)
        {
            var row = await RowAsync(account, first, last, ledgers, db, cancellationToken);
            if (projectId is { } project && row.ProjectId != project)
            {
                continue;
            }

            if (attention && row.PendingCount == 0 && row.UnsupportedCount == 0 && row.Owed == 0 && row.OpenTransfers == 0)
            {
                continue;
            }

            rows.Add(row);
        }

        var people = await users.FindAsync(rows.Select(row => row.HolderUserId).OfType<int>().Distinct().ToList(), cancellationToken);
        var named = rows.Select(row => row with
        {
            Holder = row.HolderUserId is { } id && people.TryGetValue(id, out var person) ? new PersonView(id, person.NameAr, person.NameEn) : null,
            HolderRole = row.HolderUserId is { } roleId && people.TryGetValue(roleId, out var holderPerson) ? holderPerson.RoleCode : null,
        }).ToList();
        var totals = new CustodyMonitorTotals(
            named.Sum(row => row.Posted), named.Sum(row => row.Pending), named.Sum(row => row.Owed), named.Sum(row => row.ReceivedInPeriod),
            named.Sum(row => row.SpentInPeriod), named.Sum(row => row.Unsupported), named.Count);
        var pageRows = named.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return TypedResults.Ok(new CustodyMonitorResponse(first, last, pageRows, page, pageSize, named.Count, totals, policy.Value));
    }

    internal static async Task<Results<Ok<CustodyMonitorDetail>, ValidationProblem, ProblemHttpResult>> GetAsync(
        int accountId,
        HttpContext httpContext,
        FinanceDbContext db,
        CustodyLedgers ledgers,
        CustodyAccountViews views,
        CustodyBalancePolicy policy,
        IUserDirectory users,
        CompanyClock clock,
        CancellationToken cancellationToken,
        string? from = null,
        string? to = null,
        int page = 1,
        int pageSize = 50)
    {
        if (FinanceCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var (first, last) = Period(from, to, clock, errors);
        Paging.Validate(page, pageSize, errors);
        if (errors.HasErrors)
        {
            return FinanceProblems.Validation(errors);
        }

        var account = await db.CustodyAccounts.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == accountId, cancellationToken);
        if (account is null)
        {
            return FinanceProblems.CustodyAccountNotFound();
        }

        var row = await RowAsync(account, first, last, ledgers, db, cancellationToken);
        var statement = await ledgers.StatementPageAsync(account, from is null ? null : first, to is null ? null : last, page, pageSize, cancellationToken);
        var transfers = await db.CustodyTransfers.AsNoTracking()
            .Where(transfer => (transfer.SourceAccountId == accountId || transfer.DestinationAccountId == accountId)
                               && (transfer.Status == CustodyTransferStatus.Requested || transfer.Status == CustodyTransferStatus.Approved || transfer.Status == CustodyTransferStatus.Disbursed))
            .OrderByDescending(transfer => transfer.Id).ToListAsync(cancellationToken);
        var adjustments = await db.CustodyAdjustments.AsNoTracking().Where(adjustment => adjustment.AccountId == accountId).OrderByDescending(adjustment => adjustment.Id).Take(50).ToListAsync(cancellationToken);
        var counts = await db.CustodyCashCounts.AsNoTracking().Where(count => count.AccountId == accountId).OrderByDescending(count => count.Id).Take(50).ToListAsync(cancellationToken);
        var closes = await db.CustodyPeriodCloses.AsNoTracking().Where(close => close.AccountId == accountId).OrderByDescending(close => close.PeriodEnd).ToListAsync(cancellationToken);

        var userIds = statement.Lines.Select(line => line.ActorUserId).OfType<int>()
            .Concat(transfers.Select(transfer => transfer.RequestedBy))
            .Concat(adjustments.Select(adjustment => adjustment.CreatedBy))
            .Concat(counts.Select(count => count.CountedBy))
            .Concat(closes.Select(close => close.ClosedBy))
            .Concat(account.HolderUserId is { } holder ? [holder] : [])
            .Distinct().ToList();
        var people = await users.FindAsync(userIds, cancellationToken);
        PersonView? Person(int? id) => id is { } userId ? new PersonView(userId, people.GetValueOrDefault(userId)?.NameAr, people.GetValueOrDefault(userId)?.NameEn) : null;

        var named = row with
        {
            Holder = Person(account.HolderUserId),
            HolderRole = account.HolderUserId is { } roleId ? people.GetValueOrDefault(roleId)?.RoleCode : null,
        };
        return TypedResults.Ok(new CustodyMonitorDetail(
            named,
            await views.ViewAsync(account, caller, cancellationToken),
            statement.Lines.Select(line => new CustodyMonitorLine(line.EntryDate, line.Kind, line.Status, line.Amount, line.SourceType, line.SourceId, line.Reference, line.Notes, line.HasEvidence, Person(line.ActorUserId), line.ProjectId)).ToList(),
            statement.TotalCount,
            page,
            pageSize,
            transfers.Select(transfer => new CustodyMonitorTransfer(transfer.Id, transfer.Number, transfer.Status.ToString(), transfer.Purpose, transfer.Amount, transfer.DestinationAccountId == accountId, transfer.RequestedAt, Person(transfer.RequestedBy))).ToList(),
            adjustments.Select(adjustment => new AdjustmentView(adjustment.Id, adjustment.AccountId, adjustment.CashCountId, adjustment.EntryDate, adjustment.Amount, adjustment.Reason, adjustment.EvidenceReference, adjustment.CreatedAt, Person(adjustment.CreatedBy), 0m)).ToList(),
            counts.Select(count => new CashCountView(count.Id, count.AccountId, count.CountDate, count.CountedAmount, count.BookBalance, count.Difference, count.Notes, count.EvidenceReference, count.CountedAt, Person(count.CountedBy))).ToList(),
            closes.Select(close => new PeriodCloseView(close.Id, close.AccountId, close.PeriodEnd, close.ClosingBalance, close.PendingAtClose, close.Notes, close.ClosedAt, Person(close.ClosedBy))).ToList(),
            policy.Value));
    }

    private static (DateOnly From, DateOnly To) Period(string? from, string? to, CompanyClock clock, ValidationErrors errors)
    {
        var last = Input.OptionalDate(to, errors, "to", "The last day") ?? clock.Today;
        var first = Input.OptionalDate(from, errors, "from", "The first day") ?? new DateOnly(last.Year, last.Month, 1);
        if (first > last)
        {
            errors.Add("from", "from can't be after to.");
        }

        return (first, last);
    }

    private static async Task<CustodyMonitorRow> RowAsync(CustodyAccount account, DateOnly from, DateOnly to, CustodyLedgers ledgers, FinanceDbContext db, CancellationToken cancellationToken)
    {
        var figures = await ledgers.FiguresAsync(account, cancellationToken);
        var activity = await ledgers.ActivityAsync(account, from, to, cancellationToken);
        var openTransfers = await db.CustodyTransfers.AsNoTracking().CountAsync(
            transfer => (transfer.SourceAccountId == account.Id || transfer.DestinationAccountId == account.Id)
                        && (transfer.Status == CustodyTransferStatus.Requested || transfer.Status == CustodyTransferStatus.Approved || transfer.Status == CustodyTransferStatus.Disbursed),
            cancellationToken);
        var projectId = account.Kind == CustodyAccountKinds.EngineerProject ? account.ReferenceId : null;
        return new CustodyMonitorRow(
            account.Id, account.Number, account.Kind, CustodyAccountViews.LedgerOwnerOf(account.Kind), account.HolderUserId, null, null, account.ReferenceId, projectId,
            account.NameAr, account.NameEn, account.Status.ToString(),
            figures.Posted, figures.Pending, activity.PendingCount, figures.Reserved, figures.InTransit, figures.Available, Math.Max(0, -figures.Posted),
            activity.Received, activity.Returned, activity.TransferredOut, activity.Spent, activity.Unsupported, activity.UnsupportedCount, openTransfers,
            activity.LastEntryDate, await ledgers.LastPeriodEndAsync(account.Id, cancellationToken), $"/api/custody-monitor/{account.Id}");
    }
}
