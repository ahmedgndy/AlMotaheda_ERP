namespace AlMotaheda.Modules.Workforce.Features.Payments;

/// <summary>One approved, non-void labor line of a worker: what can be paid.</summary>
internal sealed record PayableLine(int LineId, int ProjectId, DateOnly WorkDate, decimal Days, decimal Net);

/// <summary>One payment allocation on a line, with whether its payment was voided.</summary>
internal sealed record PaidPart(int LineId, decimal Amount, bool PaymentVoided);

/// <summary>A payable line with what is already paid on it.</summary>
internal sealed record SettledLine(int LineId, int ProjectId, DateOnly WorkDate, decimal Days, decimal Net, decimal Paid)
{
    public decimal Outstanding => Net - Paid;

    public bool IsOpen => Outstanding > 0;
}

/// <summary>One allocation of a new payment.</summary>
internal sealed record FifoAllocation(int LineId, decimal Amount);

/// <summary>The plan for a new payment: its allocations, or the reason it doesn't fit.</summary>
/// <param name="Fits">False when the payment is more than the worker is owed (409 <c>over_payment</c>).</param>
/// <param name="Outstanding">What the worker was owed before this payment.</param>
internal sealed record FifoPlan(bool Fits, decimal Outstanding, IReadOnlyList<FifoAllocation> Allocations);

/// <summary>
/// Worker payments (03-workforce.md, "Worker payments"), with no database access: a payment settles the worker's
/// oldest approved, unpaid days first, across every project (by work date, then line id).
/// </summary>
internal static class PaymentFifo
{
    /// <summary>
    /// Every payable line with what is paid on it, oldest first. Allocations of voided payments are ignored.
    /// </summary>
    public static IReadOnlyList<SettledLine> Settle(IEnumerable<PayableLine> lines, IEnumerable<PaidPart> paidParts)
    {
        var paid = paidParts
            .Where(part => !part.PaymentVoided)
            .GroupBy(part => part.LineId)
            .ToDictionary(group => group.Key, group => group.Sum(part => part.Amount));

        return lines
            .OrderBy(line => line.WorkDate)
            .ThenBy(line => line.LineId)
            .Select(line => new SettledLine(line.LineId, line.ProjectId, line.WorkDate, line.Days, line.Net, paid.GetValueOrDefault(line.LineId)))
            .ToList();
    }

    /// <summary>
    /// The allocations of a payment of <paramref name="amount"/>: the oldest open line first, each filled before the
    /// next, the last one possibly in part. A payment above what is owed doesn't fit (no worker advances, decision W2).
    /// </summary>
    public static FifoPlan Plan(decimal amount, IReadOnlyList<SettledLine> lines)
    {
        var open = lines.Where(line => line.IsOpen).OrderBy(line => line.WorkDate).ThenBy(line => line.LineId).ToList();
        var outstanding = open.Sum(line => line.Outstanding);

        if (amount <= 0 || amount > outstanding)
        {
            return new FifoPlan(false, outstanding, []);
        }

        var allocations = new List<FifoAllocation>();
        var left = amount;
        foreach (var line in open)
        {
            if (left == 0)
            {
                break;
            }

            var part = Math.Min(left, line.Outstanding);
            allocations.Add(new FifoAllocation(line.LineId, part));
            left -= part;
        }

        return new FifoPlan(true, outstanding, allocations);
    }
}

/// <summary>A worker's money on one project, or on all of them.</summary>
internal sealed record WorkerAccountFigures(
    decimal ApprovedDays,
    decimal ApprovedPay,
    decimal Paid,
    decimal Outstanding,
    int OpenLines,
    DateOnly? OldestOpenDate);

/// <summary>The worker account screen's arithmetic: approved pay, paid and outstanding, in total and by project.</summary>
internal static class WorkerAccounts
{
    public static WorkerAccountFigures Of(IReadOnlyList<SettledLine> lines) => new(
        lines.Sum(line => line.Days),
        lines.Sum(line => line.Net),
        lines.Sum(line => line.Paid),
        lines.Sum(line => line.Outstanding),
        lines.Count(line => line.IsOpen),
        lines.Where(line => line.IsOpen).Select(line => (DateOnly?)line.WorkDate).Min());

    /// <summary>
    /// The settled lines of the projects the caller may see. The FIFO runs over every line first, so what is paid on
    /// a visible day is the same whoever asks; the totals are then summed from the visible days only, so the account
    /// never reveals pay earned on a project the caller can't see.
    /// </summary>
    public static IReadOnlyList<SettledLine> OnProjects(IReadOnlyList<SettledLine> lines, IReadOnlySet<int> projectIds) =>
        lines.Where(line => projectIds.Contains(line.ProjectId)).ToList();

    public static IReadOnlyList<(int ProjectId, WorkerAccountFigures Figures)> ByProject(IReadOnlyList<SettledLine> lines) =>
        lines
            .GroupBy(line => line.ProjectId)
            .OrderBy(group => group.Key)
            .Select(group => (group.Key, Of(group.ToList())))
            .ToList();
}
