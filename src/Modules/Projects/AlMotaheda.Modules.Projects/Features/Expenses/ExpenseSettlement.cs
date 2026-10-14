using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;

namespace AlMotaheda.Modules.Projects.Features.Expenses;

/// <summary>
/// One expense or supply as every calculation sees it: the columns that decide what it costs and what is paid.
/// Loaded flat, per project, by <see cref="ExpenseQueries.SettlementRows"/>.
/// </summary>
/// <param name="Allocated">The allocations of payments that are not voided. Only meaningful on credit.</param>
internal sealed record ExpenseSettlementRow(
    int ExpenseId,
    ExpenseKind Kind,
    ExpenseStatus Status,
    PaymentChannel Channel,
    ExpenseCategory? Category,
    int? SupplierId,
    int? ProjectItemId,
    decimal Amount,
    decimal Allocated);

/// <summary>
/// The one rule for what an expense costs and what of it is paid (overview, sections 3.1 and 3.3). The lists, the
/// item screens, the supplier balances and the summary all go through here, so they can't disagree.
/// </summary>
internal static class ExpenseSettlement
{
    /// <summary>The recognized cost: the amount once approved, otherwise nothing.</summary>
    public static decimal Recognized(ExpenseStatus status, decimal amount) =>
        status == ExpenseStatus.Approved ? amount : 0;

    /// <summary>
    /// What is paid: a custody record is paid when it is approved (the cash left on site); a credit record is paid
    /// only as far as payment allocations say. Nothing is paid on a record that is not approved.
    /// </summary>
    public static decimal Paid(ExpenseStatus status, PaymentChannel channel, decimal amount, decimal allocated)
    {
        if (status != ExpenseStatus.Approved)
        {
            return 0;
        }

        return channel == PaymentChannel.Custody ? amount : allocated;
    }

    public static decimal Recognized(ExpenseSettlementRow row) => Recognized(row.Status, row.Amount);

    public static decimal Paid(ExpenseSettlementRow row) => Paid(row.Status, row.Channel, row.Amount, row.Allocated);

    public static decimal Outstanding(ExpenseSettlementRow row) => Recognized(row) - Paid(row);

    /// <summary>The payment state of an approved record; <c>null</c> while nothing is owed because nothing is approved.</summary>
    public static string? PaymentState(ExpenseSettlementRow row) =>
        row.Status == ExpenseStatus.Approved ? PaymentStates.Of(row.Amount, Paid(row)) : null;

    /// <summary>
    /// True when the record belongs on the project items screens: a supply, anything from a supplier, or anything
    /// linked to an item. A site expense with no supplier and no link is an "other project cost".
    /// </summary>
    public static bool IsSupplierSide(ExpenseKind kind, int? supplierId, int? projectItemId) =>
        kind == ExpenseKind.Supply || supplierId is not null || projectItemId is not null;

    public static bool IsSupplierSide(ExpenseSettlementRow row) => IsSupplierSide(row.Kind, row.SupplierId, row.ProjectItemId);
}

/// <summary>The figures at the top of the expenses screen, for the whole project.</summary>
internal sealed record ExpenseSummary(
    decimal SuppliesRecognized,
    decimal ExpensesRecognized,
    decimal RecognizedTotal,
    decimal Paid,
    decimal Outstanding,
    int PendingReviewCount,
    decimal PendingReviewAmount,
    int ReturnedCount)
{
    /// <summary>Pending review = recorded, waiting for a reviewer. Returned records are counted apart.</summary>
    public static ExpenseSummary Compute(IReadOnlyList<ExpenseSettlementRow> rows)
    {
        decimal supplies = 0, expenses = 0, paid = 0, pendingAmount = 0;
        int pendingCount = 0, returnedCount = 0;

        foreach (var row in rows)
        {
            switch (row.Status)
            {
                case ExpenseStatus.Approved:
                    if (row.Kind == ExpenseKind.Supply)
                    {
                        supplies += row.Amount;
                    }
                    else
                    {
                        expenses += row.Amount;
                    }

                    paid += ExpenseSettlement.Paid(row);
                    break;
                case ExpenseStatus.Recorded:
                    pendingCount++;
                    pendingAmount += row.Amount;
                    break;
                case ExpenseStatus.Returned:
                    returnedCount++;
                    break;
            }
        }

        var recognized = supplies + expenses;
        return new ExpenseSummary(supplies, expenses, recognized, paid, recognized - paid, pendingCount, pendingAmount, returnedCount);
    }
}
