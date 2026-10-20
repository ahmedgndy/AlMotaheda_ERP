using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Office.Common;
using AlMotaheda.Modules.Office.Data;
using AlMotaheda.Modules.Office.Features.Alerts;
using AlMotaheda.Modules.Office.Features.Ledger;
using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Office.Integration;

/// <summary>
/// The Executive decided the price case of an office expense in Price Control (in Projects' transaction). Approved
/// (once, or with a new central price): an expense whose approvals are all in is posted now, if the cash covers it.
/// Rejected: nothing is posted; the expense waits for the reviewers to reject it or return it for correction. The
/// expense keeps the price comparison it was submitted with, whatever the catalog says later.
/// </summary>
internal sealed class OfficePriceExceptionSource(
    DbContextOptions<OfficeDbContext> options,
    OfficeAlerts alerts,
    CompanyClock clock,
    OfficeLog log) : IPriceExceptionSource
{
    public string SourceType => PriceExceptionSources.OfficeExpense;

    public async Task OnDecidedAsync(PriceExceptionDecision decision, SharedTransaction transaction, CancellationToken cancellationToken)
    {
        await using var joined = await transaction.JoinAsync(options, OfficeDatabase.Configure, joinedOptions => new OfficeDbContext(joinedOptions), cancellationToken);

        var custodyId = await joined.Expenses.Where(expense => expense.Id == decision.SourceId).Select(expense => (int?)expense.CustodyId).SingleOrDefaultAsync(cancellationToken);
        if (custodyId is null)
        {
            return;
        }

        log.PriceCaseDecided(decision.SourceId, decision.ExceptionId, decision.Status);
        if (!decision.Approved)
        {
            return;
        }

        if (!await AppLock.TryAcquireAsync(joined, AppLock.Custody(custodyId.Value), cancellationToken))
        {
            throw new InvalidOperationException("The office custody is busy. Try the decision again in a moment.");
        }

        var expense = await joined.Expenses.SingleAsync(candidate => candidate.Id == decision.SourceId, cancellationToken);
        if (expense.PriceExceptionId != decision.ExceptionId)
        {
            // An older case of a corrected expense: the current one decides.
            return;
        }

        var custody = await joined.Custodies.SingleAsync(candidate => candidate.Id == custodyId, cancellationToken);
        await CustodyLedger.PostReadyAsync(joined, custody, [expense], _ => true, decision.DecidedBy, clock.UtcNow, alerts, log, cancellationToken);
    }
}
