using AlMotaheda.Modules.Finance.Contracts;
using AlMotaheda.Modules.Workforce.Data;
using AlMotaheda.Modules.Workforce.Features.Settlements;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Workforce.Integration;

/// <summary>
/// What a project's daily labor is owed for a period, for Finance's payroll batches (<see cref="IPayrollEntitlementSource"/>):
/// approved, non-void labor lines of the period, what is paid on them, what pending settlements and open payment
/// requests hold, and what is still outstanding. The same arithmetic as the weekly settlement screen
/// (<see cref="SettlementPlan.Payables"/>), so Finance can never fund more than Workforce says is owed.
/// </summary>
internal sealed class LaborEntitlementSource(WorkforceDbContext db, IPaymentRequests requests) : IPayrollEntitlementSource
{
    public string Kind => PayrollBatchKinds.DailyLabor;

    public async Task<PayrollEntitlement> EntitlementAsync(PayrollEntitlementQuery query, CancellationToken cancellationToken)
    {
        if (query.ProjectId is not { } projectId)
        {
            return PayrollEntitlement.Unknown;
        }

        var lines = await SettlementQueries.PayableLines(db, projectId, query.PeriodFrom, query.PeriodTo).ToListAsync(cancellationToken);
        if (lines.Count == 0)
        {
            return PayrollEntitlement.Unknown;
        }

        var lineIds = lines.Select(line => line.LineId).ToList();
        var paid = await SettlementQueries.PaidParts(db, lineIds).ToListAsync(cancellationToken);
        var held = await SettlementQueries.HeldParts(db, lineIds).ToListAsync(cancellationToken);

        // Open Finance requests on the days are read in a transaction, as the settlement screen does.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var reserved = await requests.ReservedAsync(PaidRecordTypes.AttendanceLine, lineIds, SharedTransaction.Of(db), cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var holds = held.Concat(reserved.Select(entry => new Features.Payments.PaidPart(entry.Key, entry.Value, false)));
        var payables = SettlementPlan.Payables(lines.Select(line => (line.WorkerId, line.Line)).ToList(), paid, holds);

        return new PayrollEntitlement(
            true,
            payables.Sum(payable => payable.Approved),
            payables.Sum(payable => payable.Paid),
            payables.Sum(payable => payable.Held),
            payables.Sum(payable => payable.Outstanding));
    }
}
