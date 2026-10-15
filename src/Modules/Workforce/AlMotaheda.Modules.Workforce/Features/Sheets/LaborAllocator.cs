using AlMotaheda.Modules.Projects.Contracts;

namespace AlMotaheda.Modules.Workforce.Features.Sheets;

/// <summary>One part of a worker's day on one project item.</summary>
internal sealed record PortionForm(int ItemId, decimal Portion);

/// <summary>
/// The allocation of labor lines to project items (03-workforce.md, "Allocation to project items"), with no
/// database access.
/// </summary>
/// <remarks>
/// A line's portions add up to at most one day, so nobody's day is ever counted twice. At approval each item gets
/// its share of the line's net pay; the last share takes the rounding rest, so a fully allocated line's shares add
/// up to its net to the piastre. Payments are split over the same parts the same way.
/// </remarks>
internal static class LaborAllocator
{
    /// <summary>A whole day.</summary>
    public const decimal WholeDay = 1m;

    /// <summary>The largest number of items one line can be split over.</summary>
    public const int MaxItemsPerLine = 20;

    /// <summary>True when the portions add up to one day or less.</summary>
    public static bool FitsInOneDay(IEnumerable<decimal> portions) => portions.Sum() <= WholeDay;

    /// <summary>The part of the day linked to no item.</summary>
    public static decimal UnallocatedPortion(IEnumerable<decimal> portions) => Math.Max(0m, WholeDay - portions.Sum());

    /// <summary>
    /// The amount of each allocation: net × portion, to the piastre. When the portions add up to a whole day, the
    /// last allocation takes the rounding rest, so the amounts add up to <paramref name="net"/> exactly.
    /// </summary>
    public static IReadOnlyList<decimal> Amounts(decimal net, IReadOnlyList<decimal> portions)
    {
        if (portions.Count == 0)
        {
            return [];
        }

        var whole = portions.Sum() == WholeDay;
        return Prorate(net, portions, WholeDay, lastTakesRest: whole);
    }

    /// <summary>
    /// Splits <paramref name="total"/> over parts in proportion to their <paramref name="weights"/> (out of
    /// <paramref name="weightTotal"/>), each rounded to the piastre. With <paramref name="lastTakesRest"/>, the last
    /// part gets what is left, so the parts add up to <paramref name="total"/> exactly.
    /// </summary>
    /// <remarks>
    /// In the rare case where rounding each part on its own would push the last part below zero (many tiny
    /// portions of a tiny amount), the parts are rounded on their running total instead: still exact, never negative.
    /// </remarks>
    public static IReadOnlyList<decimal> Prorate(decimal total, IReadOnlyList<decimal> weights, decimal weightTotal, bool lastTakesRest)
    {
        if (weights.Count == 0)
        {
            return [];
        }

        if (total == 0 || weightTotal == 0)
        {
            return weights.Select(_ => 0m).ToList();
        }

        var parts = weights.Select(weight => Money.Round(total * weight / weightTotal)).ToList();
        if (lastTakesRest)
        {
            parts[^1] = total - parts.Take(parts.Count - 1).Sum();
        }

        var sound = parts.All(part => part >= 0) && parts.Sum() <= total;
        return sound ? parts : ProrateOnRunningTotal(total, weights, weightTotal, lastTakesRest);
    }

    private static List<decimal> ProrateOnRunningTotal(decimal total, IReadOnlyList<decimal> weights, decimal weightTotal, bool lastTakesRest)
    {
        var parts = new List<decimal>(weights.Count);
        var runningWeight = 0m;
        var previous = 0m;

        for (var index = 0; index < weights.Count; index++)
        {
            runningWeight += weights[index];
            var reached = lastTakesRest && index == weights.Count - 1 ? total : Money.Round(total * runningWeight / weightTotal);
            parts.Add(reached - previous);
            previous = reached;
        }

        return parts;
    }

    /// <summary>
    /// Splits a line into its parts: one per allocation, plus the "not linked" rest when the portions are below a
    /// whole day. The amounts add up to <paramref name="net"/> and the paid shares to <paramref name="paid"/>.
    /// </summary>
    /// <param name="allocations">The line's allocations with their stored amounts (<c>null</c> amounts are computed).</param>
    public static IReadOnlyList<LinePart> Split(decimal net, decimal paid, IReadOnlyList<(int ItemId, decimal Portion, decimal? Amount)> allocations)
    {
        var amounts = allocations.All(allocation => allocation.Amount is not null)
            ? allocations.Select(allocation => allocation.Amount!.Value).ToList()
            : Amounts(net, allocations.Select(allocation => allocation.Portion).ToList()).ToList();

        var parts = new List<(int? ItemId, decimal Portion, decimal Amount)>(allocations.Count + 1);
        for (var index = 0; index < allocations.Count; index++)
        {
            parts.Add((allocations[index].ItemId, allocations[index].Portion, amounts[index]));
        }

        var restPortion = UnallocatedPortion(allocations.Select(allocation => allocation.Portion));
        if (restPortion > 0)
        {
            parts.Add((null, restPortion, net - amounts.Sum()));
        }

        // The paid shares follow the amounts, with the rounding rest on the last part: they add up to what was paid.
        var paidShares = Prorate(paid, parts.Select(part => part.Amount).ToList(), net, lastTakesRest: true);

        return parts.Select((part, index) => new LinePart(part.ItemId, part.Portion, part.Amount, paidShares[index])).ToList();
    }
}

/// <summary>One part of an approved line: on an item, or not linked (<see cref="ItemId"/> <c>null</c>).</summary>
internal sealed record LinePart(int? ItemId, decimal Portion, decimal Amount, decimal Paid);
