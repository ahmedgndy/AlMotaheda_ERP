using AlMotaheda.Modules.Projects.Common;

namespace AlMotaheda.Modules.Projects.Features.SupplierPayments;

/// <summary>One line of a payment form: how much of the payment settles one record.</summary>
internal sealed record AllocationInput(int ExpenseId, decimal Amount);

/// <summary>What a record can still take: its approved amount and what earlier payments already cover.</summary>
internal sealed record AllocationTarget(int ExpenseId, decimal Amount, decimal AlreadyAllocated)
{
    public decimal Remaining => Amount - AlreadyAllocated;
}

/// <summary>
/// The arithmetic of a supplier payment, with no database access: the allocations must add up to the payment,
/// name each record once, and never pay a record more than what is left on it.
/// </summary>
internal static class PaymentAllocations
{
    public const int MaxAllocations = 200;

    /// <summary>
    /// Checks the shape of the form and reports every problem on the <c>allocations</c> field. Returns the clean
    /// lines, or <c>null</c> when a line is unusable.
    /// </summary>
    public static IReadOnlyList<AllocationInput>? Validate(
        IReadOnlyList<(int? ExpenseId, decimal? Amount)>? allocations,
        decimal? paymentAmount,
        ValidationErrors errors)
    {
        const string field = "allocations";

        if (allocations is null || allocations.Count == 0)
        {
            errors.Add(field, "At least one allocation is required.");
            return null;
        }

        if (allocations.Count > MaxAllocations)
        {
            errors.Add(field, $"A payment can settle at most {MaxAllocations} records.");
            return null;
        }

        var clean = new List<AllocationInput>(allocations.Count);
        var seen = new HashSet<int>();
        var usable = true;

        for (var index = 0; index < allocations.Count; index++)
        {
            var (expenseId, amount) = allocations[index];
            var lineField = $"{field}[{index}]";

            if (expenseId is null or < 1)
            {
                errors.Add(lineField, "expenseId is required.");
                usable = false;
            }
            else if (!seen.Add(expenseId.Value))
            {
                errors.Add(lineField, "The same record is listed twice. Add the amounts into one line.");
                usable = false;
            }

            var cleanAmount = NumberInput.RequiredMoney(amount, errors, lineField, "amount");
            if (cleanAmount is null)
            {
                usable = false;
            }

            if (usable)
            {
                clean.Add(new AllocationInput(expenseId!.Value, cleanAmount!.Value));
            }
        }

        if (!usable)
        {
            return null;
        }

        if (paymentAmount is { } total && clean.Sum(line => line.Amount) != total)
        {
            errors.Add(field, "The allocations must add up to the payment amount.");
            return null;
        }

        return clean;
    }

    /// <summary>
    /// The first record a line would overpay, or <c>null</c> when every line fits in what is left on its record.
    /// Called under the project lock, so "what is left" can't change before the save.
    /// </summary>
    public static int? FindOverAllocation(IReadOnlyList<AllocationInput> lines, IReadOnlyDictionary<int, AllocationTarget> targets)
    {
        foreach (var line in lines)
        {
            if (!targets.TryGetValue(line.ExpenseId, out var target) || line.Amount > target.Remaining)
            {
                return line.ExpenseId;
            }
        }

        return null;
    }
}
