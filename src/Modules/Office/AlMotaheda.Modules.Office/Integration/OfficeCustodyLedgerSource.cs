using AlMotaheda.BuildingBlocks.Data;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Finance.Contracts;
using AlMotaheda.Modules.Office.Common;
using AlMotaheda.Modules.Office.Data;
using AlMotaheda.Modules.Office.Features.Ledger;
using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace AlMotaheda.Modules.Office.Integration;

/// <summary>
/// The office cash custody as Finance's custody hierarchy sees it (<see cref="ICustodyLedgerSource"/>, kind
/// <c>Office</c>): the registry's reference is the <see cref="OfficeCustody"/> id and the holder its custodian.
/// Cash handed down by the Accountant through a custody transfer, or handed back, is written to the same
/// <c>office.OfficeLedgerEntry</c> ledger the office expenses draw from, in Finance's transaction and under the
/// custody's lock; the central custody page reads this ledger for the office custodian's figures. One ledger per
/// custody: the Office module's own funding requests (a Finance payment request) stay as they are.
/// </summary>
internal sealed class OfficeCustodyLedgerSource(
    DbContextOptions<OfficeDbContext> options,
    OfficeDbContext db,
    IConfiguration configuration,
    CompanyClock clock,
    OfficeLog log) : ICustodyLedgerSource
{
    public string Kind => CustodyAccountKinds.Office;

    public async Task<CustodyTarget?> FindAsync(int referenceId, int? holderUserId, CancellationToken cancellationToken)
    {
        var custody = await db.Custodies.AsNoTracking()
            .Where(candidate => candidate.Id == referenceId && candidate.Status == OfficeCustodyStatus.Active)
            .Select(candidate => new { candidate.Id, candidate.HolderUserId, candidate.NameAr, candidate.NameEn })
            .SingleOrDefaultAsync(cancellationToken);
        if (custody is null || (holderUserId is not null && holderUserId != custody.HolderUserId))
        {
            return null;
        }

        return new CustodyTarget(custody.Id, custody.HolderUserId, custody.NameAr, custody.NameEn, null);
    }

    public async Task<CustodyLedgerFigures> FiguresAsync(int referenceId, int? holderUserId, CancellationToken cancellationToken)
    {
        var custody = await db.Custodies.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == referenceId, cancellationToken);
        if (custody is null)
        {
            return new CustodyLedgerFigures(0, 0);
        }

        var balance = await CustodyLedger.BalanceAsync(db, custody, reservePending: true, cancellationToken);
        return new CustodyLedgerFigures(balance.Recorded, balance.PendingExpenses);
    }

    public Task ReceiveAsync(CustodyLedgerWrite write, SharedTransaction transaction, CancellationToken cancellationToken) =>
        WriteAsync(write, cashIn: true, transaction, cancellationToken);

    public Task WithdrawAsync(CustodyLedgerWrite write, SharedTransaction transaction, CancellationToken cancellationToken) =>
        WriteAsync(write, cashIn: false, transaction, cancellationToken);

    public async Task<CustodyActivity> ActivityAsync(int referenceId, int? holderUserId, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var entries = await db.LedgerEntries.AsNoTracking().Where(entry => entry.CustodyId == referenceId)
            .Select(entry => new { entry.EntryDate, entry.Kind, entry.Amount, entry.SourceType, entry.SourceId }).ToListAsync(cancellationToken);
        var inPeriod = entries.Where(entry => entry.EntryDate >= from && entry.EntryDate <= to).ToList();
        var custody = await db.Custodies.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == referenceId, cancellationToken);
        var pending = custody is null ? new OfficeCustodyBalance(0, 0, 0, 0, 0, 0, 0, 0, false) : await CustodyLedger.BalanceAsync(db, custody, reservePending: true, cancellationToken);
        var pendingCount = await db.Expenses.AsNoTracking().CountAsync(
            expense => expense.CustodyId == referenceId && expense.PostingStatus == OfficePostingStatus.NotPosted
                       && (expense.Status == OfficeExpenseStatus.Submitted || expense.Status == OfficeExpenseStatus.PartlyApproved || expense.Status == OfficeExpenseStatus.Approved),
            cancellationToken);

        // An office expense is posted only with its receipt or invoice, so a posted one without an attachment is the exception, not the rule.
        var expenseIds = inPeriod.Where(entry => entry.Kind == OfficeLedgerKind.Expense && entry.SourceType == OfficeLedgerSources.Expense).Select(entry => entry.SourceId).Distinct().ToList();
        var supported = expenseIds.Count == 0 ? new HashSet<int>() : (await db.Attachments.AsNoTracking()
            .Where(attachment => attachment.OwnerType == OfficeAttachmentOwners.Expense && attachment.OwnerId != null && expenseIds.Contains(attachment.OwnerId.Value))
            .Select(attachment => attachment.OwnerId!.Value).Distinct().ToListAsync(cancellationToken)).ToHashSet();
        var unsupported = inPeriod.Where(entry => entry.Kind == OfficeLedgerKind.Expense && entry.SourceType == OfficeLedgerSources.Expense && !supported.Contains(entry.SourceId)).ToList();

        return new CustodyActivity(
            inPeriod.Where(entry => entry.Amount > 0 && entry.Kind == OfficeLedgerKind.Funding).Sum(entry => entry.Amount),
            inPeriod.Where(entry => entry.Kind == OfficeLedgerKind.Return).Sum(entry => -entry.Amount),
            0m,
            inPeriod.Where(entry => entry.Kind == OfficeLedgerKind.Expense).Sum(entry => -entry.Amount),
            pending.PendingExpenses,
            pendingCount,
            unsupported.Sum(entry => -entry.Amount),
            unsupported.Count,
            entries.Count == 0 ? null : entries.Max(entry => entry.EntryDate));
    }

    public async Task<CustodyStatementPage> StatementAsync(int referenceId, int? holderUserId, DateOnly? from, DateOnly? to, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = db.LedgerEntries.AsNoTracking().Where(entry => entry.CustodyId == referenceId);
        if (from is { } first)
        {
            query = query.Where(entry => entry.EntryDate >= first);
        }

        if (to is { } last)
        {
            query = query.Where(entry => entry.EntryDate <= last);
        }

        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderByDescending(entry => entry.EntryDate).ThenByDescending(entry => entry.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        var expenseIds = rows.Where(entry => entry.SourceType == OfficeLedgerSources.Expense).Select(entry => entry.SourceId).Distinct().ToList();
        var supported = expenseIds.Count == 0 ? new HashSet<int>() : (await db.Attachments.AsNoTracking()
            .Where(attachment => attachment.OwnerType == OfficeAttachmentOwners.Expense && attachment.OwnerId != null && expenseIds.Contains(attachment.OwnerId.Value))
            .Select(attachment => attachment.OwnerId!.Value).Distinct().ToListAsync(cancellationToken)).ToHashSet();
        return new CustodyStatementPage(
            rows.Select(entry => new CustodyStatementLine(
                entry.EntryDate, entry.Kind.ToString(), "Posted", entry.Amount, entry.SourceType, entry.SourceId, entry.Reference, entry.Reason,
                entry.SourceType != OfficeLedgerSources.Expense || supported.Contains(entry.SourceId), entry.PostedBy, null)).ToList(),
            total);
    }

    private async Task WriteAsync(CustodyLedgerWrite write, bool cashIn, SharedTransaction transaction, CancellationToken cancellationToken)
    {
        await using var joined = await transaction.JoinAsync(options, OfficeDatabase.Configure, joinedOptions => new OfficeDbContext(joinedOptions), cancellationToken);
        var custody = await joined.Custodies.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == write.ReferenceId, cancellationToken)
                      ?? throw new ArgumentException($"Office custody {write.ReferenceId} doesn't exist.", nameof(write));
        if (!await CustodyLedger.LockAsync(joined, custody.Id, cancellationToken))
        {
            throw new CustodyLedgerRefusedException(CustodyLedgerRefusedException.Busy, "The office custody is being updated by someone else. Try again in a moment.");
        }

        // The company's one overdraft rule (Projects:CustodyBalanceCheck): Block refuses a withdrawal above what the custody holds.
        if (!cashIn && string.Equals(configuration["Projects:CustodyBalanceCheck"], CustodyBalancePolicies.Block, StringComparison.Ordinal))
        {
            var balance = await CustodyLedger.BalanceAsync(joined, custody, reservePending: true, cancellationToken);
            if (write.Amount > balance.Available)
            {
                throw new CustodyLedgerRefusedException(CustodyLedgerRefusedException.Insufficient, $"The office custody has {Math.Max(balance.Available, 0):0.00} available, less than {write.Amount:0.00}.");
            }
        }

        var sourceType = write.SourceType switch
        {
            CustodyLedgerSources.TransferReversal => OfficeLedgerSources.FinanceTransferReversal,
            CustodyLedgerSources.Adjustment => OfficeLedgerSources.FinanceAdjustment,
            _ => OfficeLedgerSources.FinanceTransfer,
        };
        var entry = CustodyLedger.Add(joined, custody.Id, cashIn ? OfficeLedgerKind.Funding : OfficeLedgerKind.Return, cashIn ? write.Amount : -write.Amount, write.EntryDate,
            sourceType, write.SourceId, write.ActorUserId, clock.UtcNow, reason: write.Notes, reference: write.Reference);
        await joined.SaveChangesAsync(cancellationToken);
        log.FinanceCustodyWritten(write.ActorUserId, custody.Id, entry.Id, sourceType, write.SourceId);
    }
}
