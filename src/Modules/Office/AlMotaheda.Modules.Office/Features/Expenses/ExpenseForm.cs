using AlMotaheda.Modules.Office.Common;
using AlMotaheda.Modules.Office.Data;
using AlMotaheda.Modules.Office.Data.Configurations;

namespace AlMotaheda.Modules.Office.Features.Expenses;

/// <summary>The office expense form, as sent by the app (create, or correct and resubmit after a return).</summary>
internal sealed record OfficeExpenseRequest(
    int? CustodyId,
    string? Category,
    int? PriceItemId,
    string? Description,
    string? Purpose,
    string? Vendor,
    string? InvoiceNo,
    decimal? Quantity,
    string? Unit,
    decimal? UnitPrice,
    decimal? Discount,
    decimal? Tax,
    string? ExpenseDate,
    int? SpendRequestId,
    string? PriceJustification,
    IReadOnlyList<int>? AttachmentIds,
    Guid? IdempotencyKey,
    string? RowVersion);

/// <summary>The checked values of the form, with the total the server computed.</summary>
internal sealed record ExpenseFields(
    string Category,
    int? PriceItemId,
    string Description,
    string? Purpose,
    string? Vendor,
    string? InvoiceNo,
    decimal Quantity,
    string Unit,
    decimal UnitPrice,
    decimal Discount,
    decimal Tax,
    decimal Amount,
    DateOnly ExpenseDate,
    int? SpendRequestId,
    string? PriceJustification,
    IReadOnlyList<int> AttachmentIds)
{
    /// <summary>round(quantity × unit price, 2) − discount + tax. The client's own total is never trusted.</summary>
    public static decimal Total(decimal quantity, decimal unitPrice, decimal discount, decimal tax) =>
        decimal.Round(quantity * unitPrice, 2, MidpointRounding.AwayFromZero) - discount + tax;

    public static ExpenseFields? Validate(OfficeExpenseRequest request, DateOnly today, int maxAgeDays, ValidationErrors errors)
    {
        var category = Input.Clean(request.Category);
        if (category is null || !OfficeExpenseCategories.All.Contains(category))
        {
            errors.Add("category", $"category must be one of: {string.Join(", ", OfficeExpenseCategories.All)}.");
        }

        var description = Input.RequiredText(request.Description, OfficeLengths.Description, errors, "description", "The description");
        var purpose = Input.OptionalText(request.Purpose, OfficeLengths.Description, errors, "purpose", "The purpose");
        var vendor = Input.OptionalText(request.Vendor, OfficeLengths.Vendor, errors, "vendor", "The vendor");
        var invoiceNo = Input.OptionalText(request.InvoiceNo, OfficeLengths.InvoiceNo, errors, "invoiceNo", "The invoice number");
        var quantity = NumberInput.RequiredQuantity(request.Quantity, errors, "quantity", "The quantity");
        var unit = Input.RequiredText(request.Unit, OfficeLengths.Unit, errors, "unit", "The unit");

        decimal? unitPrice = null;
        if (request.UnitPrice is null)
        {
            errors.Add("unitPrice", "The unit price is required.");
        }
        else
        {
            unitPrice = NumberInput.RequiredMoney(request.UnitPrice, errors, "unitPrice", "The unit price");
        }

        var discount = OfficeInput.NonNegativeMoney(request.Discount, errors, "discount", "The discount");
        var tax = OfficeInput.NonNegativeMoney(request.Tax, errors, "tax", "The tax");
        var date = Input.RequiredDate(request.ExpenseDate, errors, "expenseDate", "The expense date");
        if (date is { } day)
        {
            if (day > today)
            {
                errors.Add("expenseDate", "The expense date can't be in the future.");
            }
            else if (day < today.AddDays(-maxAgeDays))
            {
                errors.Add("expenseDate", $"The expense date can be at most {maxAgeDays} days ago. Ask finance to record older purchases.");
            }
        }

        var justification = Input.OptionalText(request.PriceJustification, OfficeLengths.Note, errors, "priceJustification", "The price justification");

        decimal? amount = null;
        if (quantity is { } q && unitPrice is { } price && !errors.Has("discount") && !errors.Has("tax"))
        {
            var gross = decimal.Round(q * price, 2, MidpointRounding.AwayFromZero);
            if (discount > gross)
            {
                errors.Add("discount", "The discount can't be more than quantity × unit price.");
            }
            else
            {
                amount = Total(q, price, discount, tax);
                if (amount <= 0)
                {
                    errors.Add("unitPrice", "The total must be greater than zero.");
                }
                else if (amount > NumberInput.MaxMoney)
                {
                    errors.Add("unitPrice", "The total is too large.");
                }
            }
        }

        if (errors.HasErrors)
        {
            return null;
        }

        return new ExpenseFields(
            category!, request.PriceItemId, description!, purpose, vendor, invoiceNo, quantity!.Value, unit!, unitPrice!.Value, discount, tax,
            amount!.Value, date!.Value, request.SpendRequestId, justification, (request.AttachmentIds ?? []).Distinct().ToList());
    }

    public void ApplyTo(OfficeExpense expense)
    {
        expense.Category = Category;
        expense.PriceItemId = PriceItemId;
        expense.Description = Description;
        expense.Purpose = Purpose;
        expense.Vendor = Vendor;
        expense.InvoiceNo = InvoiceNo;
        expense.Quantity = Quantity;
        expense.Unit = Unit;
        expense.UnitPrice = UnitPrice;
        expense.Discount = Discount;
        expense.Tax = Tax;
        expense.Amount = Amount;
        expense.ExpenseDate = ExpenseDate;
        expense.SpendRequestId = SpendRequestId;
        expense.PriceJustification = PriceJustification;
    }
}
