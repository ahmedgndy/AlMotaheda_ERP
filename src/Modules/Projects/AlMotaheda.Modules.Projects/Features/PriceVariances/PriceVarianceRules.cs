using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Features.PriceVariances;

/// <summary>The figures of a variance, before it is stored.</summary>
internal sealed record VarianceFigures(decimal UnitVariance, decimal TotalVariance, decimal VariancePercent, VarianceDirection Direction);

/// <summary>
/// The price-variance rule (docs/cost-flow §2.3), with no database access: a unit price differs from the reference
/// price "beyond the threshold" when the difference is larger than both the rounding allowance and the tolerance
/// percent of the reference. Both directions are flagged: a much lower price can be a mistake too.
/// </summary>
internal static class PriceVarianceRules
{
    /// <summary>The allowed difference per unit: the larger of the two allowances.</summary>
    public static decimal Allowance(decimal referencePrice, decimal tolerancePercent, decimal roundingAllowance) =>
        Math.Max(Money.Round(referencePrice * tolerancePercent / 100m), roundingAllowance);

    /// <summary>The variance, or <c>null</c> when the price is within the allowance.</summary>
    public static VarianceFigures? Check(decimal referencePrice, decimal actualPrice, decimal quantity, decimal tolerancePercent, decimal roundingAllowance)
    {
        if (referencePrice <= 0 || actualPrice <= 0 || quantity <= 0)
        {
            return null;
        }

        var difference = actualPrice - referencePrice;
        if (Math.Abs(difference) <= Allowance(referencePrice, tolerancePercent, roundingAllowance))
        {
            return null;
        }

        return new VarianceFigures(
            Money.Round(difference),
            Money.Round(difference * quantity),
            Math.Round(difference / referencePrice * 100m, 2, MidpointRounding.AwayFromZero),
            difference > 0 ? VarianceDirection.Above : VarianceDirection.Below);
    }

    /// <summary>
    /// Checks a supply that names a catalog item and has a reference price, and adds a variance to the context when
    /// its unit price is outside the policy. A supply posted from a Procurement invoice is not checked: its price was
    /// set by the Executive's quotation choice and matched against the order (3-way match).
    /// </summary>
    public static async Task<PriceVariance?> RaiseIfNeededAsync(
        ProjectsDbContext db, ProjectExpense expense, int raisedBy, DateTime now, CancellationToken cancellationToken)
    {
        if (expense.Kind != ExpenseKind.Supply
            || expense.SourceType is not null
            || expense.CatalogItemId is not { } catalogItemId
            || expense.CatalogPriceAtRecording is not { } reference
            || expense.UnitPrice is not { } actual
            || expense.Quantity is not { } quantity)
        {
            return null;
        }

        var policy = await db.PricingPolicies.AsNoTracking().SingleAsync(cancellationToken);
        if (Check(reference, actual, quantity, policy.VarianceTolerancePercent, policy.RoundingAllowance) is not { } figures)
        {
            return null;
        }

        var variance = new PriceVariance
        {
            ProjectId = expense.ProjectId,
            Expense = expense,
            CatalogItemId = catalogItemId,
            ReferencePrice = reference,
            ActualPrice = actual,
            Quantity = quantity,
            UnitVariance = figures.UnitVariance,
            TotalVariance = figures.TotalVariance,
            VariancePercent = figures.VariancePercent,
            Direction = figures.Direction,
            TolerancePercent = policy.VarianceTolerancePercent,
            RoundingAllowance = policy.RoundingAllowance,
            Status = PriceVarianceStatus.Open,
            RaisedAt = now,
            RaisedBy = raisedBy,
        };
        db.PriceVariances.Add(variance);
        return variance;
    }

    /// <summary>Closes the supply's waiting or rejected variance: the supply changed, so it is checked again.</summary>
    public static async Task SupersedeActiveAsync(ProjectsDbContext db, int expenseId, int userId, DateTime now, CancellationToken cancellationToken)
    {
        var active = await db.PriceVariances
            .Where(variance => variance.ExpenseId == expenseId
                && (variance.Status == PriceVarianceStatus.Open || variance.Status == PriceVarianceStatus.Rejected))
            .ToListAsync(cancellationToken);
        foreach (var variance in active)
        {
            variance.Supersede(userId, now);
        }
    }

    /// <summary>True when the supply has a variance waiting or rejected: it can't be approved yet.</summary>
    public static Task<bool> BlocksApprovalAsync(ProjectsDbContext db, int expenseId, CancellationToken cancellationToken) =>
        db.PriceVariances.AnyAsync(
            variance => variance.ExpenseId == expenseId
                && (variance.Status == PriceVarianceStatus.Open || variance.Status == PriceVarianceStatus.Rejected),
            cancellationToken);
}
