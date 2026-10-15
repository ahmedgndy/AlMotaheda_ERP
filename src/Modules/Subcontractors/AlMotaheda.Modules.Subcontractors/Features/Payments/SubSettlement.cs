using AlMotaheda.Modules.Subcontractors.Features.Accounts;

namespace AlMotaheda.Modules.Subcontractors.Features.Payments;

/// <summary>A piece of work by kind and id: measurement 7 and manpower sheet 7 are different work.</summary>
internal readonly record struct WorkKey(WorkKind Kind, int Id);

/// <summary>A payment that is not voided, as the settlement sees it.</summary>
internal sealed record SettlementPayment(int PaymentId, DateOnly PaymentDate, decimal Amount);

/// <summary>A piece of approved work of the contract, as the settlement sees it.</summary>
internal sealed record SettlementWork(WorkKey Work, DateOnly Date, decimal Amount);

/// <summary>An amount of one payment applied to one piece of work.</summary>
internal sealed record SettlementAllocation(int PaymentId, WorkKey Work, decimal Amount);

/// <summary>
/// How subcontractor payments settle work (04-subcontractors.md, "Payments"), with no database access.
/// </summary>
/// <remarks>
/// <para>One rule covers the three moments it runs: after a payment is recorded, after work is approved, and after
/// a payment is voided. Every unapplied amount of the contract's payments (oldest payment first: date, then id)
/// goes to the contract's oldest approved, unpaid work (date, then measurements before manpower, then id), until
/// either the money or the open work runs out.</para>
/// <para>So a payment settles the oldest work first; what is left is an unapplied advance; work approved later
/// takes the advance; and when a payment is voided, its work is open again and other unapplied money moves to it.
/// After every run, either no payment has unapplied money or no work is open, and no allocation ever exceeds what
/// is left on its work or on its payment.</para>
/// </remarks>
internal static class SubSettlement
{
    /// <param name="payments">The contract's payments that are not voided.</param>
    /// <param name="work">The contract's approved work.</param>
    /// <param name="existing">The current allocations of those payments (voided payments' allocations left out).</param>
    /// <returns>The new allocations to add. The same payment and work can appear once; the caller adds it to an existing row.</returns>
    public static IReadOnlyList<SettlementAllocation> Apply(
        IReadOnlyCollection<SettlementPayment> payments,
        IReadOnlyCollection<SettlementWork> work,
        IReadOnlyCollection<SettlementAllocation> existing)
    {
        var appliedByPayment = existing
            .GroupBy(allocation => allocation.PaymentId)
            .ToDictionary(group => group.Key, group => group.Sum(allocation => allocation.Amount));
        var allocatedByWork = existing
            .GroupBy(allocation => allocation.Work)
            .ToDictionary(group => group.Key, group => group.Sum(allocation => allocation.Amount));

        var money = payments
            .OrderBy(payment => payment.PaymentDate)
            .ThenBy(payment => payment.PaymentId)
            .Select(payment => (payment.PaymentId, Left: payment.Amount - appliedByPayment.GetValueOrDefault(payment.PaymentId)))
            .Where(payment => payment.Left > 0)
            .ToList();

        var open = work
            .OrderBy(piece => piece.Date)
            .ThenBy(piece => piece.Work.Kind)
            .ThenBy(piece => piece.Work.Id)
            .Select(piece => (piece.Work, Left: piece.Amount - allocatedByWork.GetValueOrDefault(piece.Work)))
            .Where(piece => piece.Left > 0)
            .ToList();

        var result = new List<SettlementAllocation>();
        int paymentIndex = 0, workIndex = 0;

        while (paymentIndex < money.Count && workIndex < open.Count)
        {
            var (paymentId, moneyLeft) = money[paymentIndex];
            var (workKey, workLeft) = open[workIndex];
            var amount = Math.Min(moneyLeft, workLeft);

            result.Add(new SettlementAllocation(paymentId, workKey, amount));

            money[paymentIndex] = (paymentId, moneyLeft - amount);
            open[workIndex] = (workKey, workLeft - amount);

            if (money[paymentIndex].Left == 0)
            {
                paymentIndex++;
            }

            if (open[workIndex].Left == 0)
            {
                workIndex++;
            }
        }

        return result;
    }
}
