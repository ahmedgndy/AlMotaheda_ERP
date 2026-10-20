using AlMotaheda.Modules.Office.Common;
using AlMotaheda.Modules.Office.Data;
using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Office.Features.Expenses;

/// <summary>
/// The office approval policy, decided by the server on every submission: which risks an expense carries and so
/// whether it needs one approval or two (two distinct reviewers, one of them the Executive, never the submitter).
/// </summary>
/// <remarks>
/// Two approvals when the total is above the routine limit, the price is above its central range or its unit can't
/// be compared, it looks like a duplicate, it is a repair (always) or an emergency repair recorded after the fact,
/// the final cost is above the approved estimate, or it is more than the custody's available balance.
/// </remarks>
internal static class ExpensePolicy
{
    private static readonly string[] ElevatingReasons =
    [
        OfficeReviewReasons.AboveRange, OfficeReviewReasons.UnitMismatch, OfficeReviewReasons.AboveLimit, OfficeReviewReasons.DuplicateRisk,
        OfficeReviewReasons.Repair, OfficeReviewReasons.Emergency, OfficeReviewReasons.AboveEstimate, OfficeReviewReasons.AboveAvailable,
    ];

    public static int RequiredApprovals(IEnumerable<string> reasons) => reasons.Any(ElevatingReasons.Contains) ? 2 : 1;

    public static IReadOnlyList<string> Split(string? reasons) =>
        string.IsNullOrEmpty(reasons) ? [] : reasons.Split(',', StringSplitOptions.RemoveEmptyEntries);

    public static string? Join(IEnumerable<string> reasons)
    {
        var list = reasons.Distinct().ToList();
        return list.Count == 0 ? null : string.Join(',', list);
    }
}

/// <summary>The central price comparison of an expense line (<see cref="IPriceControl"/>), in the office's terms.</summary>
internal static class ExpensePricing
{
    /// <summary>
    /// Compares the expense with Price Control when it names a price item. Refuses an item outside the office
    /// categories, an inactive item, a price the policy blocks, and an above-range or unit-mismatched price without a
    /// justification. Returns the verdict (or <c>null</c> when no item is named) and the refusal.
    /// </summary>
    public static async Task<(PriceVerdict? Verdict, ProblemHttpResult? Refused, ValidationErrors? Errors)> CheckAsync(
        IPriceControl priceControl, ExpenseFields fields, IReadOnlyCollection<string> officeCategories, CancellationToken cancellationToken)
    {
        if (fields.PriceItemId is not { } itemId)
        {
            return (null, null, null);
        }

        var items = await priceControl.FindItemsAsync([itemId], cancellationToken);
        if (!items.TryGetValue(itemId, out var item) || !item.IsActive || !officeCategories.Contains(item.CategoryCode))
        {
            var errors = new ValidationErrors();
            errors.Add("priceItemId", "Choose an active item of the office price list, or leave it empty.");
            return (null, null, errors);
        }

        var verdict = await priceControl.CheckAsync(
            new PriceCheckRequest(itemId, null, fields.ExpenseDate, fields.Unit, fields.UnitPrice, fields.Quantity), cancellationToken);

        if (verdict.IsAboveRange && verdict.Blocked)
        {
            return (verdict, OfficeProblems.PriceRefused(fields.UnitPrice, verdict.MaxPrice ?? 0), null);
        }

        if ((verdict.IsAboveRange || verdict.Result == PriceVerdict.UnitMismatch) && fields.PriceJustification is null)
        {
            var errors = new ValidationErrors();
            errors.Add("priceJustification", verdict.IsAboveRange
                ? $"The unit price is above the allowed range ({verdict.MinPrice:0.00} – {verdict.MaxPrice:0.00} per {item.Unit}). Explain why."
                : $"The price list quotes this item per {item.Unit}. Use that unit, or explain why it can't be compared.");
            return (verdict, null, errors);
        }

        return (verdict, null, null);
    }

    public static void Apply(OfficeExpense expense, PriceVerdict? verdict)
    {
        expense.PriceCheck = verdict?.Result;
        expense.PriceRangeId = verdict?.RangeId;
        expense.ReferencePrice = verdict?.ReferencePrice;
        expense.ReferenceMinPrice = verdict?.MinPrice;
        expense.ReferenceMaxPrice = verdict?.MaxPrice;
        expense.VariancePerUnit = verdict?.VariancePerUnit;
        expense.VarianceTotal = verdict?.VarianceTotal;
        expense.VariancePercent = verdict is null ? null : Math.Clamp(verdict.VariancePercent, -9_999_999m, 9_999_999m);
    }

    public static IEnumerable<string> Reasons(PriceVerdict? verdict) => verdict?.Result switch
    {
        PriceVerdict.AboveRange => [OfficeReviewReasons.AboveRange],
        PriceVerdict.UnitMismatch => [OfficeReviewReasons.UnitMismatch],
        PriceVerdict.NoReference => [OfficeReviewReasons.NoReference],
        _ => [],
    };
}

/// <summary>
/// Spots an expense that looks like one already recorded: the same receipt photo (SHA-256), the same vendor invoice
/// number, or the same vendor, amount and date. A match is never refused (two identical coffees are real): it is
/// flagged for review and makes the expense need two approvals.
/// </summary>
internal static class DuplicateCheck
{
    public static async Task<IReadOnlyList<int>> FindAsync(
        OfficeDbContext db, int? expenseId, ExpenseFields fields, IReadOnlyCollection<string> receiptHashes, CancellationToken cancellationToken)
    {
        var live = db.Expenses.AsNoTracking().Where(expense => expense.Id != expenseId && expense.Status != OfficeExpenseStatus.Rejected);
        var matches = new HashSet<int>();

        if (receiptHashes.Count > 0)
        {
            var sameReceipt = await db.Attachments.AsNoTracking()
                .Where(attachment => attachment.OwnerType == OfficeAttachmentOwners.Expense && attachment.OwnerId != expenseId
                                     && receiptHashes.Contains(attachment.Sha256))
                .Select(attachment => attachment.OwnerId!.Value)
                .Where(ownerId => live.Any(expense => expense.Id == ownerId))
                .ToListAsync(cancellationToken);
            matches.UnionWith(sameReceipt);
        }

        if (fields.InvoiceNo is { } invoiceNo && fields.Vendor is { } vendor)
        {
            matches.UnionWith(await live.Where(expense => expense.Vendor == vendor && expense.InvoiceNo == invoiceNo)
                .Select(expense => expense.Id).ToListAsync(cancellationToken));
        }

        if (fields.Vendor is { } sameVendor)
        {
            matches.UnionWith(await live.Where(expense => expense.Vendor == sameVendor && expense.Amount == fields.Amount && expense.ExpenseDate == fields.ExpenseDate)
                .Select(expense => expense.Id).ToListAsync(cancellationToken));
        }

        return matches.OrderBy(id => id).Take(10).ToList();
    }

    public static string? Format(IReadOnlyList<int> ids) => ids.Count == 0 ? null : string.Join(',', ids);

    public static IReadOnlyList<int> Parse(string? value) =>
        string.IsNullOrEmpty(value) ? [] : value.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList();
}
