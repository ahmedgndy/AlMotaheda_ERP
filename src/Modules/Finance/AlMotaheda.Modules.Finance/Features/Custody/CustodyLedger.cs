using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Finance.Common;
using AlMotaheda.Modules.Finance.Contracts;
using AlMotaheda.Modules.Finance.Data;
using AlMotaheda.Modules.Finance.Requests;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Finance.Features.Custody;

/// <summary>
/// The figures of one custody account, every one calculated from append-only rows, never stored.
/// </summary>
/// <param name="Posted">The authoritative balance: every posted movement.</param>
/// <param name="Pending">Outflows recorded but not reviewed (claims, payroll): the exposure. Not in <see cref="Posted"/>.</param>
/// <param name="Reserved">Transfers out and payment requests out of this account that are open but not disbursed or paid.</param>
/// <param name="InTransit">Transfers disbursed to this account and not received yet.</param>
/// <param name="Available">Posted − Pending − Reserved: what a new outflow may take.</param>
internal sealed record CustodyFigures(decimal Posted, decimal Pending, decimal Reserved, decimal InTransit, decimal Available)
{
    public static CustodyFigures Of(decimal posted, decimal pending, decimal reserved, decimal inTransit) =>
        new(posted, pending, reserved, inTransit, posted - pending - reserved);
}

/// <summary>The figures of one treasury account (Bank or Cash).</summary>
/// <param name="Balance">Opening balance + every ledger entry of the account from the opening date on.</param>
/// <param name="Reserved">Transfers out of the account that are approved or requested but not disbursed.</param>
internal sealed record TreasuryFigures(decimal OpeningBalance, DateOnly OpeningDate, bool OpeningSet, decimal MoneyIn, decimal MoneyOut, decimal InternalOut, decimal InternalIn, decimal Balance, decimal Reserved, decimal Available);

/// <summary>
/// The pure arithmetic of a custody account Finance ledgers: one rule for the account screen, the transfer checks
/// and the reports, so they can't disagree.
/// </summary>
internal static class CustodyArithmetic
{
    public static (decimal Posted, decimal Pending) Sum(IEnumerable<(decimal Amount, CustodyMovementStatus Status)> movements)
    {
        decimal posted = 0, pending = 0;
        foreach (var (amount, status) in movements)
        {
            switch (status)
            {
                case CustodyMovementStatus.Posted:
                    posted += amount;
                    break;
                case CustodyMovementStatus.Pending:
                    pending += -amount;
                    break;
            }
        }

        return (posted, pending);
    }
}

/// <summary>The treasury queries, as <see cref="IQueryable{T}"/> so tests can prove they translate.</summary>
internal static class TreasuryQueries
{
    /// <summary>The ledger entries that move the treasury: Bank and Cash only, never a custody's own payments.</summary>
    public static IQueryable<LedgerEntry> Entries(FinanceDbContext db, LedgerAccount account, DateOnly from) =>
        db.LedgerEntries.AsNoTracking().Where(entry => entry.Account == account && entry.EntryDate >= from);

    /// <summary>Open transfers out of a treasury account: the amount reserved until they are disbursed.</summary>
    public static IQueryable<CustodyTransfer> OpenTransfersOut(FinanceDbContext db, LedgerAccount account) =>
        db.CustodyTransfers.AsNoTracking()
            .Where(transfer => transfer.SourceKind == TransferEndpointKind.Treasury && transfer.SourceTreasuryAccount == account
                               && (transfer.Status == CustodyTransferStatus.Requested || transfer.Status == CustodyTransferStatus.Approved));

    public static async Task<TreasuryFigures> FiguresAsync(FinanceDbContext db, TreasuryAccount account, CancellationToken cancellationToken)
    {
        var entries = await Entries(db, account.Account, account.OpeningDate)
            .Select(entry => new { entry.Amount, entry.Purpose })
            .ToListAsync(cancellationToken);
        var reserved = await OpenTransfersOut(db, account.Account).SumAsync(transfer => (decimal?)transfer.Amount, cancellationToken) ?? 0;

        decimal moneyIn = 0, moneyOut = 0, internalIn = 0, internalOut = 0;
        foreach (var entry in entries)
        {
            var internalTransfer = PaymentPurposes.IsInternalTransfer(entry.Purpose);
            if (entry.Amount > 0)
            {
                if (internalTransfer) internalIn += entry.Amount; else moneyIn += entry.Amount;
            }
            else
            {
                if (internalTransfer) internalOut += -entry.Amount; else moneyOut += -entry.Amount;
            }
        }

        var balance = account.OpeningBalance + entries.Sum(entry => entry.Amount);
        return new TreasuryFigures(account.OpeningBalance, account.OpeningDate, account.OpeningSetAt is not null, moneyIn, moneyOut, internalOut, internalIn, balance, reserved, balance - reserved);
    }
}

/// <summary>
/// One door to every custody ledger: the movements Finance keeps for the accountant's custody and the payroll
/// batches, and the ledgers of the Projects (engineer's project custody) and Fleet (vehicle custody) modules through
/// <see cref="ICustodyLedgerSource"/>. Every write is append-only and runs under the account's application lock.
/// </summary>
internal sealed class CustodyLedgers(FinanceDbContext db, IEnumerable<ICustodyLedgerSource> sources, CustodyBalancePolicy policy, CompanyClock clock)
{
    public static string LockName(int accountId) => $"AlMotaheda.Finance.Custody.{accountId}";

    public static Task<bool> TryLockAsync(DbContext db, int accountId, CancellationToken cancellationToken) =>
        SqlAppLock.TryAcquireAsync(db, LockName(accountId), cancellationToken);

    public CustodyBalancePolicy Policy => policy;

    /// <summary>The module that keeps the ledger of <paramref name="kind"/>, or <c>null</c> when Finance keeps it.</summary>
    public ICustodyLedgerSource? SourceOf(string kind) =>
        CustodyAccountKinds.IsLedgeredByFinance(kind) ? null : sources.SingleOrDefault(source => source.Kind == kind);

    /// <summary>Whether <paramref name="userId"/> answers for the account now: its holder, or the person its ledger module names (a vehicle's driver).</summary>
    public async Task<bool> IsAccountableAsync(CustodyAccount account, int userId, CancellationToken cancellationToken) =>
        account.HolderUserId == userId
        || (account.ReferenceId is { } reference && SourceOf(account.Kind) is { } source && await source.IsAccountableAsync(reference, account.HolderUserId, userId, cancellationToken));

    /// <summary>The target of an external kind, validated by its module (exists, the holder may hold it).</summary>
    public async Task<CustodyTarget?> FindTargetAsync(string kind, int referenceId, int? holderUserId, CancellationToken cancellationToken) =>
        SourceOf(kind) is { } source ? await source.FindAsync(referenceId, holderUserId, cancellationToken) : null;

    /// <summary>Movements the account itself keeps (Finance-ledgered kinds), as the statement shows them.</summary>
    public IQueryable<CustodyMovement> Movements(int accountId) =>
        db.CustodyMovements.AsNoTracking().Where(movement => movement.AccountId == accountId);

    /// <summary>Open transfers out of the account: reserved until disbursed.</summary>
    public IQueryable<CustodyTransfer> OpenTransfersOut(int accountId) =>
        db.CustodyTransfers.AsNoTracking()
            .Where(transfer => transfer.SourceAccountId == accountId
                               && (transfer.Status == CustodyTransferStatus.Requested || transfer.Status == CustodyTransferStatus.Approved));

    /// <summary>Transfers disbursed to the account and not received yet.</summary>
    public IQueryable<CustodyTransfer> InTransitTo(int accountId) =>
        db.CustodyTransfers.AsNoTracking()
            .Where(transfer => transfer.DestinationAccountId == accountId && transfer.Status == CustodyTransferStatus.Disbursed);

    /// <summary>Open payment requests paid out of the account: reserved until paid.</summary>
    public IQueryable<PaymentRequest> OpenRequestsOut(int accountId)
    {
        var open = PaymentRequestRules.Open;
        return db.PaymentRequests.AsNoTracking().Where(request => request.SourceCustodyAccountId == accountId && open.Contains(request.Status));
    }

    public async Task<CustodyFigures> FiguresAsync(CustodyAccount account, CancellationToken cancellationToken)
    {
        decimal posted, pending;
        if (account.IsLedgeredByFinance)
        {
            var movements = await Movements(account.Id).Select(movement => new { movement.Amount, movement.Status }).ToListAsync(cancellationToken);
            (posted, pending) = CustodyArithmetic.Sum(movements.Select(movement => (movement.Amount, movement.Status)));
        }
        else
        {
            var source = SourceOf(account.Kind) ?? throw new InvalidOperationException($"No module keeps the ledger of {account.Kind} custody accounts.");
            var figures = await source.FiguresAsync(account.ReferenceId!.Value, account.HolderUserId, cancellationToken);
            (posted, pending) = (figures.Posted, figures.Pending);
        }

        var reserved = (await OpenTransfersOut(account.Id).SumAsync(transfer => (decimal?)transfer.Amount, cancellationToken) ?? 0)
                       + (await OpenRequestsOut(account.Id).SumAsync(request => (decimal?)request.Amount, cancellationToken) ?? 0);
        var inTransit = await InTransitTo(account.Id).SumAsync(transfer => (decimal?)transfer.Amount, cancellationToken) ?? 0;
        return CustodyFigures.Of(posted, pending, reserved, inTransit);
    }

    /// <summary>The last closed period of the account, or <c>null</c>.</summary>
    /// <summary>
    /// The account's activity in a period for the central custody page: from Finance's own movements for the kinds it
    /// ledgers, from the owning module otherwise (<see cref="ICustodyLedgerSource.ActivityAsync"/>).
    /// </summary>
    public async Task<CustodyActivity> ActivityAsync(CustodyAccount account, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        if (!CustodyAccountKinds.IsLedgeredByFinance(account.Kind))
        {
            return SourceOf(account.Kind) is { } source
                ? await source.ActivityAsync(account.ReferenceId ?? 0, account.HolderUserId, from, to, cancellationToken)
                : CustodyActivity.Empty;
        }

        var rows = await Movements(account.Id)
            .Select(movement => new { movement.EntryDate, movement.Amount, movement.Kind, movement.Status, movement.TransferId })
            .ToListAsync(cancellationToken);
        var returnTransfers = await db.CustodyTransfers.AsNoTracking()
            .Where(transfer => transfer.SourceAccountId == account.Id && transfer.Purpose == PaymentPurposes.CustodyReturn)
            .Select(transfer => transfer.Id).ToListAsync(cancellationToken);
        var inPeriod = rows.Where(row => row.EntryDate >= from && row.EntryDate <= to && row.Status == CustodyMovementStatus.Posted).ToList();
        var pending = rows.Where(row => row.Status == CustodyMovementStatus.Pending).ToList();
        return new CustodyActivity(
            inPeriod.Where(row => row.Amount > 0).Sum(row => row.Amount),
            inPeriod.Where(row => row.Kind == CustodyMovementKind.TransferOut && row.TransferId is { } id && returnTransfers.Contains(id)).Sum(row => -row.Amount),
            inPeriod.Where(row => row.Kind == CustodyMovementKind.TransferOut && !(row.TransferId is { } id && returnTransfers.Contains(id))).Sum(row => -row.Amount),
            inPeriod.Where(row => row.Amount < 0 && row.Kind != CustodyMovementKind.TransferOut).Sum(row => -row.Amount),
            pending.Sum(row => -row.Amount),
            pending.Count,
            0m,
            0,
            rows.Count == 0 ? null : rows.Max(row => row.EntryDate));
    }

    /// <summary>One page of the account's statement, newest first: Finance's movements, or the owning module's lines.</summary>
    public async Task<CustodyStatementPage> StatementPageAsync(CustodyAccount account, DateOnly? from, DateOnly? to, int page, int pageSize, CancellationToken cancellationToken)
    {
        if (!CustodyAccountKinds.IsLedgeredByFinance(account.Kind))
        {
            return SourceOf(account.Kind) is { } source
                ? await source.StatementAsync(account.ReferenceId ?? 0, account.HolderUserId, from, to, page, pageSize, cancellationToken)
                : new CustodyStatementPage([], 0);
        }

        var query = Movements(account.Id);
        if (from is { } first)
        {
            query = query.Where(movement => movement.EntryDate >= first);
        }

        if (to is { } last)
        {
            query = query.Where(movement => movement.EntryDate <= last);
        }

        var total = await query.CountAsync(cancellationToken);
        var lines = await query.OrderByDescending(movement => movement.EntryDate).ThenByDescending(movement => movement.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(movement => new CustodyStatementLine(
                movement.EntryDate, movement.Kind.ToString(), movement.Status.ToString(), movement.Amount, movement.SourceType, movement.SourceId,
                movement.Reference, movement.Reason, true, movement.CreatedBy, null))
            .ToListAsync(cancellationToken);
        return new CustodyStatementPage(lines, total);
    }

    public async Task<DateOnly?> LastPeriodEndAsync(int accountId, CancellationToken cancellationToken) =>
        await db.CustodyPeriodCloses.AsNoTracking().Where(close => close.AccountId == accountId).MaxAsync(close => (DateOnly?)close.PeriodEnd, cancellationToken);

    /// <summary>The closed period a movement dated <paramref name="entryDate"/> would fall into, or <c>null</c> when it is open.</summary>
    public async Task<DateOnly?> ClosedPeriodOfAsync(int accountId, DateOnly entryDate, CancellationToken cancellationToken)
    {
        var last = await LastPeriodEndAsync(accountId, cancellationToken);
        return last is { } end && entryDate <= end ? end : null;
    }

    /// <summary>
    /// Money into an account, posted, in <paramref name="transaction"/>: a movement for a Finance-ledgered account,
    /// otherwise written by the owning module. The account must be locked by the caller.
    /// </summary>
    public async Task ReceiveAsync(CustodyAccount account, CustodyMovementKind kind, CustodyLedgerWrite write, int? transferId, SharedTransaction transaction, CancellationToken cancellationToken)
    {
        if (account.IsLedgeredByFinance)
        {
            db.CustodyMovements.Add(Movement(account.Id, kind, write.Amount, write, transferId));
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        await (SourceOf(account.Kind) ?? throw new InvalidOperationException($"No module keeps the ledger of {account.Kind} custody accounts."))
            .ReceiveAsync(write, transaction, cancellationToken);
    }

    /// <summary>
    /// Money out of an account, posted. Under policy Block the amount must fit in what is available; under Warn the
    /// balance may go negative. Throws <see cref="CustodyLedgerRefusedException"/> when refused.
    /// </summary>
    public async Task WithdrawAsync(CustodyAccount account, CustodyMovementKind kind, CustodyLedgerWrite write, int? transferId, decimal? alreadyReserved, SharedTransaction transaction, CancellationToken cancellationToken)
    {
        if (account.IsLedgeredByFinance)
        {
            if (policy.Blocks)
            {
                var figures = await FiguresAsync(account, cancellationToken);
                // The transfer being disbursed is still counted as reserved: it is what the money is for.
                var available = figures.Available + (alreadyReserved ?? 0);
                if (write.Amount > available)
                {
                    throw new CustodyLedgerRefusedException(CustodyLedgerRefusedException.Insufficient, $"The custody has {Math.Max(available, 0):0.00} available, less than {write.Amount:0.00}.");
                }
            }

            db.CustodyMovements.Add(Movement(account.Id, kind, -write.Amount, write, transferId));
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        await (SourceOf(account.Kind) ?? throw new InvalidOperationException($"No module keeps the ledger of {account.Kind} custody accounts."))
            .WithdrawAsync(write, transaction, cancellationToken);
    }

    /// <summary>A reversal of a posted movement of a Finance-ledgered account: the opposite amount, pointing to it.</summary>
    public async Task ReverseMovementAsync(CustodyMovement original, string sourceType, string reason, DateOnly on, int actorUserId, CancellationToken cancellationToken)
    {
        db.CustodyMovements.Add(new CustodyMovement
        {
            AccountId = original.AccountId,
            EntryDate = on,
            Amount = -original.Amount,
            Kind = CustodyMovementKind.Reversal,
            Status = CustodyMovementStatus.Posted,
            SourceType = sourceType,
            SourceId = original.SourceId,
            TransferId = original.TransferId,
            PaymentRequestId = original.PaymentRequestId,
            ReversesMovementId = original.Id,
            Reason = reason,
            Reference = original.Reference,
            CreatedAt = clock.UtcNow,
            CreatedBy = actorUserId,
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    private CustodyMovement Movement(int accountId, CustodyMovementKind kind, decimal signedAmount, CustodyLedgerWrite write, int? transferId) => new()
    {
        AccountId = accountId,
        EntryDate = write.EntryDate,
        Amount = signedAmount,
        Kind = kind,
        Status = CustodyMovementStatus.Posted,
        SourceType = write.SourceType,
        SourceId = write.SourceId,
        TransferId = transferId,
        Reason = write.Notes,
        Reference = write.Reference,
        CreatedAt = clock.UtcNow,
        CreatedBy = write.ActorUserId,
    };
}
