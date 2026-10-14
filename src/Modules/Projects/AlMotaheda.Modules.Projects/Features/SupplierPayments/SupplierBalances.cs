using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Features.Expenses;

namespace AlMotaheda.Modules.Projects.Features.SupplierPayments;

/// <summary>What one supplier has delivered to a project and what it is still owed.</summary>
/// <param name="CreditRecognized">Approved records bought on credit: the invoices the company pays later.</param>
/// <param name="CustodyRecognized">Approved records paid on site from custody.</param>
/// <param name="Paid">Custody amounts plus payment allocations that are not voided.</param>
/// <param name="OpenInvoices">Approved credit records that are not fully paid.</param>
internal sealed record SupplierBalance(
    int SupplierId,
    decimal CreditRecognized,
    decimal CustodyRecognized,
    decimal Paid,
    decimal Outstanding,
    int OpenInvoices);

/// <summary>Per-supplier balances of a project, from the same settlement rule as every other screen.</summary>
internal static class SupplierBalances
{
    /// <summary>One balance per supplier that has at least one approved record, by supplier id.</summary>
    public static IReadOnlyList<SupplierBalance> Compute(IReadOnlyList<ExpenseSettlementRow> rows) =>
        rows.Where(row => row.SupplierId is not null && row.Status == ExpenseStatus.Approved)
            .GroupBy(row => row.SupplierId!.Value)
            .OrderBy(group => group.Key)
            .Select(group =>
            {
                var credit = group.Where(row => row.Channel == PaymentChannel.Credit).Sum(row => row.Amount);
                var custody = group.Where(row => row.Channel == PaymentChannel.Custody).Sum(row => row.Amount);
                var paid = group.Sum(ExpenseSettlement.Paid);
                var open = group.Count(row => row.Channel == PaymentChannel.Credit && ExpenseSettlement.Outstanding(row) > 0);
                return new SupplierBalance(group.Key, credit, custody, paid, credit + custody - paid, open);
            })
            .ToList();
}
