using AlMotaheda.Modules.Workforce.Data;
using AlMotaheda.Modules.Workforce.Features.Payments;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Workforce.Features.Settlements;

/// <summary>One worker's approved, unpaid days of a period on a project, with what is held for other payments.</summary>
/// <param name="Held">Held by pending settlements and by open Finance payment requests: not paid yet, but taken.</param>
/// <param name="Outstanding">Approved − paid − held: what a new settlement may still pay.</param>
internal sealed record WorkerPayable(
    int WorkerId, decimal Days, decimal Approved, decimal Paid, decimal Held, decimal Outstanding, IReadOnlyList<SettledLine> Lines);

/// <summary>What one worker gets from a new settlement, and which days it pays.</summary>
internal sealed record SettlementLinePlan(int WorkerId, decimal Amount, IReadOnlyList<FifoAllocation> Allocations);

/// <summary>A new settlement's plan, or the worker it doesn't fit for.</summary>
/// <param name="TooMuchFor">The first worker asked more than they are owed for the period (409 <c>over_payment</c>).</param>
internal sealed record SettlementPlanResult(IReadOnlyList<SettlementLinePlan> Lines, int? TooMuchFor, decimal? OutstandingOfThatWorker)
{
    public decimal Amount => Lines.Sum(line => line.Amount);
}

/// <summary>
/// The weekly settlement's arithmetic, with no database access. It uses the worker payments' own rule
/// (<see cref="PaymentFifo"/>: oldest day first), restricted to one project and one period, with the days held by
/// pending settlements and open payment requests counted as taken, so one day is never paid twice.
/// </summary>
internal static class SettlementPlan
{
    /// <summary>Each worker's payable figures, from the lines of the period, what is paid on them, and what is held.</summary>
    public static IReadOnlyList<WorkerPayable> Payables(
        IReadOnlyList<(int WorkerId, PayableLine Line)> lines, IEnumerable<PaidPart> paid, IEnumerable<PaidPart> held)
    {
        var paidParts = paid.ToList();
        var heldParts = held.ToList();
        return lines
            .GroupBy(row => row.WorkerId)
            .OrderBy(group => group.Key)
            .Select(group =>
            {
                var own = group.Select(row => row.Line).ToList();
                var ids = own.Select(line => line.LineId).ToHashSet();
                var settledPaid = PaymentFifo.Settle(own, paidParts.Where(part => ids.Contains(part.LineId)));
                var settledAll = PaymentFifo.Settle(own, paidParts.Concat(heldParts.Select(part => part with { PaymentVoided = false })).Where(part => ids.Contains(part.LineId)));
                var approved = own.Sum(line => line.Net);
                var paidTotal = settledPaid.Sum(line => line.Paid);
                var outstanding = settledAll.Sum(line => Math.Max(line.Outstanding, 0));
                return new WorkerPayable(group.Key, own.Sum(line => line.Days), approved, paidTotal, approved - paidTotal - outstanding, outstanding, settledAll);
            })
            .ToList();
    }

    /// <summary>
    /// The plan of a new settlement. <paramref name="asked"/> gives each worker's amount; <c>null</c> pays every
    /// worker everything still outstanding for the period. A worker asked for 0 is left out.
    /// </summary>
    public static SettlementPlanResult Plan(IReadOnlyList<WorkerPayable> payables, IReadOnlyDictionary<int, decimal>? asked)
    {
        var lines = new List<SettlementLinePlan>();
        var wanted = asked ?? payables.Where(payable => payable.Outstanding > 0).ToDictionary(payable => payable.WorkerId, payable => payable.Outstanding);
        foreach (var (workerId, amount) in wanted.OrderBy(pair => pair.Key))
        {
            if (amount == 0)
            {
                continue;
            }

            var payable = payables.FirstOrDefault(candidate => candidate.WorkerId == workerId);
            var plan = PaymentFifo.Plan(amount, payable?.Lines ?? []);
            if (!plan.Fits)
            {
                return new SettlementPlanResult([], workerId, payable?.Outstanding ?? 0);
            }

            lines.Add(new SettlementLinePlan(workerId, amount, plan.Allocations));
        }

        return new SettlementPlanResult(lines, null, null);
    }
}

/// <summary>The settlement queries, as <see cref="IQueryable{T}"/> so tests can prove they translate.</summary>
internal static class SettlementQueries
{
    /// <summary>Approved, non-void labor lines of a project whose work day is in the period, every worker.</summary>
    public static IQueryable<ProjectPayableLine> PayableLines(WorkforceDbContext db, int projectId, DateOnly from, DateOnly to) =>
        db.AttendanceLines.AsNoTracking()
            .Where(line => line.Sheet.ProjectId == projectId && !line.IsVoid && line.Sheet.Status == SheetStatus.Approved && line.NetAmount != null
                           && line.WorkDate >= from && line.WorkDate <= to)
            .Select(line => new ProjectPayableLine(line.WorkerId, line.Id, line.Sheet.ProjectId, line.WorkDate, line.Days, line.NetAmount!.Value));

    /// <summary>The payment allocations on these lines, with whether their payment was voided.</summary>
    public static IQueryable<PaidPart> PaidParts(WorkforceDbContext db, IReadOnlyCollection<int> lineIds) =>
        db.WorkerPaymentAllocations.AsNoTracking()
            .Where(allocation => lineIds.Contains(allocation.LineId))
            .Select(allocation => new PaidPart(allocation.LineId, allocation.Amount, allocation.Payment.VoidedAt != null));

    /// <summary>What pending settlements (other than <paramref name="exceptSettlementId"/>) hold on these lines.</summary>
    public static IQueryable<PaidPart> HeldParts(WorkforceDbContext db, IReadOnlyCollection<int> lineIds, int? exceptSettlementId = null) =>
        db.PayrollSettlementAllocations.AsNoTracking()
            .Where(allocation => lineIds.Contains(allocation.LineId)
                                 && allocation.SettlementLine.Settlement.Status == SettlementStatus.Pending
                                 && allocation.SettlementLine.SettlementId != exceptSettlementId)
            .Select(allocation => new PaidPart(allocation.LineId, allocation.Amount, false));

    /// <summary>Lines of the period not approved yet (draft, submitted or returned sheets), per worker: shown apart, never paid.</summary>
    public static IQueryable<WorkerCount> WaitingLines(WorkforceDbContext db, int projectId, DateOnly from, DateOnly to) =>
        db.AttendanceLines.AsNoTracking()
            .Where(line => line.Sheet.ProjectId == projectId && !line.IsVoid && line.Sheet.Status != SheetStatus.Approved
                           && line.Sheet.Status != SheetStatus.Rejected && line.WorkDate >= from && line.WorkDate <= to)
            .GroupBy(line => line.WorkerId)
            .Select(group => new WorkerCount(group.Key, group.Count()));
}

internal sealed record ProjectPayableLine(int WorkerId, int LineId, int ProjectId, DateOnly WorkDate, decimal Days, decimal Net)
{
    public PayableLine Line => new(LineId, ProjectId, WorkDate, Days, Net);
}

internal sealed record WorkerCount(int WorkerId, int Count);
