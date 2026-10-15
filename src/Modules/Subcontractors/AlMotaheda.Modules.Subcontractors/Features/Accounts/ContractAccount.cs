using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Subcontractors.Data;

namespace AlMotaheda.Modules.Subcontractors.Features.Accounts;

/// <summary>
/// The account of one contract (04-subcontractors.md, section 2): three different numbers that are never mixed.
/// The contract value is a commitment, the executed (approved) work is the cost, and the payments are what left.
/// </summary>
/// <remarks>
/// <code>
/// executed            = approved measurements + approved manpower
/// pendingReview       = recorded (not approved) measurements + manpower
/// remainingToExecute  = max(0, Value − executed)                    (the commitment)
/// paidTotal           = Σ payments not voided
/// appliedToWork       = Σ allocations of payments not voided
/// unappliedAdvance    = paidTotal − appliedToWork
/// outstandingWork     = executed − appliedToWork
/// retentionHeld       = Completed ? 0 : round(executed × RetentionPercent ÷ 100)   (shown only, decision S2)
/// dueNow              = max(0, executed − retentionHeld − paidTotal)
/// completedPercent    = Value = 0 ? 0 : min(100, executed ÷ Value × 100)
/// </code>
/// The list, the detail and the cost figures all call this class, so they can't disagree.
/// </remarks>
internal sealed record ContractAccount(
    decimal Value,
    decimal Executed,
    int ExecutedRecords,
    decimal PendingReview,
    int PendingRecords,
    decimal RemainingToExecute,
    decimal PaidTotal,
    decimal AppliedToWork,
    decimal UnappliedAdvance,
    decimal OutstandingWork,
    decimal RetentionPercent,
    decimal RetentionHeld,
    decimal DueNow,
    decimal CompletedPercent)
{
    public static ContractAccount Compute(ContractRow contract, IEnumerable<WorkRow> work, IEnumerable<PaymentRow> payments) =>
        Compute(contract.Value, contract.RetentionPercent, contract.Status == ContractStatus.Completed, work, payments);

    /// <param name="work">The contract's measurements and manpower sheets, any status.</param>
    /// <param name="payments">The contract's payments, voided ones included (they are ignored).</param>
    public static ContractAccount Compute(
        decimal value, decimal retentionPercent, bool isCompleted, IEnumerable<WorkRow> work, IEnumerable<PaymentRow> payments)
    {
        decimal executed = 0, pending = 0, applied = 0;
        int executedRecords = 0, pendingRecords = 0;

        foreach (var row in work)
        {
            switch (row.Status)
            {
                case WorkStatus.Approved:
                    executed += row.Amount;
                    executedRecords++;
                    applied += row.Allocated;
                    break;
                case WorkStatus.Recorded:
                    pending += row.Amount;
                    pendingRecords++;
                    break;
            }
        }

        var paid = payments.Where(payment => !payment.IsVoided).Sum(payment => payment.Amount);
        var retentionHeld = isCompleted ? 0 : Money.Round(executed * retentionPercent / 100);

        return new ContractAccount(
            value,
            executed,
            executedRecords,
            pending,
            pendingRecords,
            RemainingToExecute: Math.Max(0, value - executed),
            PaidTotal: paid,
            AppliedToWork: applied,
            UnappliedAdvance: paid - applied,
            OutstandingWork: executed - applied,
            retentionPercent,
            retentionHeld,
            DueNow: Math.Max(0, executed - retentionHeld - paid),
            CompletedPercent: value == 0 ? 0 : Math.Min(100, Math.Round(executed / value * 100, 2, MidpointRounding.AwayFromZero)));
    }
}

/// <summary>The sums of the accounts of several contracts (the top of the subcontractors screen).</summary>
internal sealed record ContractTotals(
    int ContractCount,
    decimal Value,
    decimal Executed,
    decimal PendingReview,
    decimal RemainingToExecute,
    decimal PaidTotal,
    decimal AppliedToWork,
    decimal UnappliedAdvance,
    decimal OutstandingWork,
    decimal RetentionHeld,
    decimal DueNow)
{
    public static ContractTotals Sum(IReadOnlyCollection<ContractAccount> accounts) => new(
        accounts.Count,
        accounts.Sum(account => account.Value),
        accounts.Sum(account => account.Executed),
        accounts.Sum(account => account.PendingReview),
        accounts.Sum(account => account.RemainingToExecute),
        accounts.Sum(account => account.PaidTotal),
        accounts.Sum(account => account.AppliedToWork),
        accounts.Sum(account => account.UnappliedAdvance),
        accounts.Sum(account => account.OutstandingWork),
        accounts.Sum(account => account.RetentionHeld),
        accounts.Sum(account => account.DueNow));
}

/// <summary>
/// The figures this module reports to Projects (overview 4), from the same flat rows as the accounts. Only
/// approved work is cost; the contract value only shows as the commitment.
/// </summary>
internal static class SubcontractFigures
{
    /// <summary>
    /// One total per project item with approved work, plus one with a <c>null</c> item for approved work not
    /// linked yet. A measurement counts on its contract item's project item; a manpower sheet on its own.
    /// </summary>
    public static IReadOnlyList<ItemCostTotal> ItemTotals(IEnumerable<WorkRow> work) =>
        work.Where(row => row.IsApproved)
            .GroupBy(row => row.ProjectItemId)
            .OrderBy(group => group.Key ?? int.MaxValue)
            .Select(group => new ItemCostTotal(
                group.Key,
                group.Sum(row => row.Amount),
                group.Sum(row => row.Allocated),
                group.Count(),
                group.Select(row => row.SubcontractorId).Distinct().Count()))
            .ToList();

    /// <summary>
    /// The Subcontractors cost group of one project. <paramref name="from"/> and <paramref name="to"/> limit the
    /// business date of the work (inclusive). The commitment is always "as of now": the value of every contract
    /// that is not completed minus all of its executed work, whatever the dates.
    /// </summary>
    public static ProjectCostFigures CostFigures(
        IEnumerable<ContractRow> contracts, IReadOnlyCollection<WorkRow> work, DateOnly? from, DateOnly? to)
    {
        decimal recognized = 0, paid = 0, pending = 0;
        int recognizedRecords = 0, pendingRecords = 0;

        foreach (var row in work)
        {
            if ((from is { } start && row.Date < start) || (to is { } end && row.Date > end))
            {
                continue;
            }

            if (row.Status == WorkStatus.Approved)
            {
                recognized += row.Amount;
                paid += row.Allocated;
                recognizedRecords++;
            }
            else if (row.Status == WorkStatus.Recorded)
            {
                pending += row.Amount;
                pendingRecords++;
            }
        }

        var executedByContract = work
            .Where(row => row.IsApproved)
            .GroupBy(row => row.ContractId)
            .ToDictionary(group => group.Key, group => group.Sum(row => row.Amount));

        var committed = contracts
            .Where(contract => contract.Status != ContractStatus.Completed)
            .Sum(contract => Math.Max(0, contract.Value - executedByContract.GetValueOrDefault(contract.Id)));

        return new ProjectCostFigures(recognized, paid, recognizedRecords, pending, pendingRecords, committed);
    }
}
