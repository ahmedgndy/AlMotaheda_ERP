using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Features.Expenses;
using AlMotaheda.Modules.Projects.Features.SupplierPayments;

namespace AlMotaheda.Modules.Projects.Features.Reports;

/// <summary>Recognized, paid and outstanding of one group of creditors.</summary>
internal sealed record OwedFigures(decimal Recognized, decimal Paid, decimal Outstanding)
{
    public static OwedFigures Of(decimal recognized, decimal paid) => new(recognized, paid, recognized - paid);
}

/// <summary>What the company owes for one cost group reported by another module.</summary>
internal sealed record OwedGroup(string CostGroup, bool HasSource, decimal Recognized, decimal Paid, decimal Outstanding);

/// <summary>Everything <see cref="Obligations"/> computed for one project, before names are added.</summary>
/// <param name="Suppliers">One balance per supplier with approved records (<see cref="SupplierBalances"/>).</param>
/// <param name="SupplierTotals">The sums over <paramref name="Suppliers"/>.</param>
/// <param name="WithoutSupplier">Approved records with no supplier (most are paid from custody).</param>
/// <param name="Groups">Labor, subcontractors and equipment, from the modules that own them.</param>
/// <param name="TotalOutstanding">Everything still owed: suppliers, records without supplier and the groups.</param>
internal sealed record ObligationsResult(
    IReadOnlyList<SupplierBalance> Suppliers,
    OwedFigures SupplierTotals,
    OwedFigures WithoutSupplier,
    IReadOnlyList<OwedGroup> Groups,
    decimal TotalOutstanding);

/// <summary>
/// What a project still owes (02-projects-planning.md, section E). Pure. Supplier balances come from the same
/// <see cref="SupplierBalances"/> as the supplier balances screen, and the other groups from the same
/// <see cref="CostGroupFigures"/> as the overview, so the screens can't disagree.
/// </summary>
/// <remarks>Only approved records are owed. A record waiting for review is not a debt yet.</remarks>
internal static class Obligations
{
    public static ObligationsResult Compute(IReadOnlyList<ExpenseSettlementRow> expenses, IEnumerable<CostGroupFigures> groups)
    {
        var suppliers = SupplierBalances.Compute(expenses);
        var supplierTotals = OwedFigures.Of(
            suppliers.Sum(balance => balance.CreditRecognized + balance.CustodyRecognized),
            suppliers.Sum(balance => balance.Paid));

        var withoutSupplier = expenses.Where(row => row.SupplierId is null && row.Status == ExpenseStatus.Approved).ToList();
        var noSupplier = OwedFigures.Of(withoutSupplier.Sum(ExpenseSettlement.Recognized), withoutSupplier.Sum(ExpenseSettlement.Paid));

        var owed = groups
            .Where(group => ProjectCosts.SourceGroups.Contains(group.CostGroup))
            .Select(group => new OwedGroup(group.CostGroup, group.HasSource, group.Recognized, group.Paid, group.Outstanding))
            .ToList();

        var total = supplierTotals.Outstanding + noSupplier.Outstanding + owed.Sum(group => group.Outstanding);
        return new ObligationsResult(suppliers, supplierTotals, noSupplier, owed, total);
    }
}
