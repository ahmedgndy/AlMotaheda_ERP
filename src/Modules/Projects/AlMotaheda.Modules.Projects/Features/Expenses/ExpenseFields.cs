using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Data.Configurations;
using AlMotaheda.Modules.Projects.Features.Catalog;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Features.Expenses;

/// <summary>The supply or site expense form, as sent on create and on fix-and-resubmit.</summary>
internal interface IExpenseFieldsRequest
{
    string? Kind { get; }
    string? ExpenseDate { get; }
    int? SupplierId { get; }
    string? ItemName { get; }
    string? Unit { get; }
    decimal? Quantity { get; }
    decimal? UnitPrice { get; }
    string? Category { get; }
    string? Description { get; }
    decimal? Amount { get; }
    string? Channel { get; }
    string? InvoiceNo { get; }
    string? DeliveryNoteNo { get; }
    int? CatalogItemId { get; }
    int? ProjectItemId { get; }
    string? Notes { get; }
}

/// <summary>
/// The form after validation. A supply has a supplier, an item name, a unit, a quantity and a unit price, and its
/// amount is their product, computed here: the client's amount is never trusted. A site expense has a category, a
/// description and an amount.
/// </summary>
internal sealed record ExpenseFields(
    ExpenseKind Kind,
    DateOnly ExpenseDate,
    int? SupplierId,
    string? ItemName,
    string? Unit,
    decimal? Quantity,
    decimal? UnitPrice,
    ExpenseCategory? Category,
    string? Description,
    decimal Amount,
    PaymentChannel Channel,
    string? InvoiceNo,
    string? DeliveryNoteNo,
    int? CatalogItemId,
    int? ProjectItemId,
    string? Notes)
{
    /// <param name="today">The company's business date; a record can't be dated after it.</param>
    public static ExpenseFields? Validate(IExpenseFieldsRequest request, DateOnly today, ValidationErrors errors)
    {
        var hasKind = Input.TryParseName<ExpenseKind>(request.Kind, out var kind);
        if (!hasKind)
        {
            errors.Add("kind", "kind must be Supply or Expense.");
        }

        var date = Input.RequiredDate(request.ExpenseDate, errors, "expenseDate", "The date");
        if (date > today)
        {
            errors.Add("expenseDate", "The date can't be in the future.");
        }

        var hasChannel = Input.TryParseName<PaymentChannel>(request.Channel, out var channel);
        if (!hasChannel)
        {
            errors.Add("channel", "channel must be Custody or Credit.");
        }

        if (request.SupplierId is < 1)
        {
            errors.Add("supplierId", "supplierId is not valid.");
        }

        if (request.CatalogItemId is < 1)
        {
            errors.Add("catalogItemId", "catalogItemId is not valid.");
        }

        if (request.ProjectItemId is < 1)
        {
            errors.Add("projectItemId", "projectItemId is not valid.");
        }

        var description = Input.OptionalText(request.Description, ProjectExpenseConfiguration.DescriptionMaxLength, errors, "description", "The description");
        var invoiceNo = Input.OptionalText(request.InvoiceNo, ProjectExpenseConfiguration.ReferenceMaxLength, errors, "invoiceNo", "The invoice number");
        var deliveryNoteNo = Input.OptionalText(request.DeliveryNoteNo, ProjectExpenseConfiguration.ReferenceMaxLength, errors, "deliveryNoteNo", "The delivery note number");
        var notes = Input.OptionalText(request.Notes, ProjectExpenseConfiguration.NotesMaxLength, errors, "notes", "Notes");

        if (hasChannel && channel == PaymentChannel.Credit && request.SupplierId is null)
        {
            errors.Add("supplierId", "Buying on credit means owing a supplier: choose the supplier.");
        }

        string? itemName = null, unit = null;
        decimal? quantity = null, unitPrice = null;
        ExpenseCategory? category = null;
        decimal amount = 0;

        if (hasKind && kind == ExpenseKind.Supply)
        {
            if (request.SupplierId is null)
            {
                errors.Add("supplierId", "A supply needs its supplier.");
            }

            itemName = Input.RequiredText(request.ItemName, ProjectExpenseConfiguration.ItemNameMaxLength, errors, "itemName", "The item name");

            if (!MeasurementUnits.TryParse(request.Unit, out var parsedUnit))
            {
                errors.Add("unit", $"unit must be one of: {string.Join(", ", MeasurementUnits.All)}.");
            }
            else
            {
                unit = parsedUnit;
            }

            quantity = NumberInput.RequiredQuantity(request.Quantity, errors, "quantity", "The quantity");
            unitPrice = NumberInput.RequiredMoney(request.UnitPrice, errors, "unitPrice", "The unit price");

            if (Input.Clean(request.Category) is not null)
            {
                errors.Add("category", "A category applies to site expenses only.");
            }

            if (hasChannel && channel == PaymentChannel.Credit && invoiceNo is null)
            {
                errors.Add("invoiceNo", "A supply on credit needs the supplier's invoice number.");
            }

            if (quantity is { } q && unitPrice is { } p)
            {
                amount = Money.Round(q * p);
                if (amount <= 0 || amount > NumberInput.MaxMoney)
                {
                    errors.Add("quantity", $"Quantity times unit price must be between 0.01 and {NumberInput.MaxMoney:0.00}.");
                }
            }
        }
        else if (hasKind)
        {
            if (!Input.TryParseName<ExpenseCategory>(request.Category, out var parsedCategory))
            {
                errors.Add("category", $"category must be one of: {string.Join(", ", Enum.GetNames<ExpenseCategory>())}.");
            }
            else
            {
                category = parsedCategory;
            }

            if (description is null && !errors.Has("description"))
            {
                errors.Add("description", "A site expense needs a description.");
            }

            amount = NumberInput.RequiredMoney(request.Amount, errors, "amount", "The amount") ?? 0;

            if (Input.Clean(request.ItemName) is not null || Input.Clean(request.Unit) is not null
                || request.Quantity is not null || request.UnitPrice is not null)
            {
                errors.Add("kind", "Item name, unit, quantity and unit price apply to supplies only.");
            }

            if (request.CatalogItemId is not null)
            {
                errors.Add("catalogItemId", "A catalog item applies to supplies only.");
            }
        }

        if (errors.HasErrors)
        {
            return null;
        }

        return new ExpenseFields(
            kind,
            date!.Value,
            request.SupplierId,
            itemName,
            unit,
            quantity,
            unitPrice,
            category,
            description,
            amount,
            channel,
            invoiceNo,
            deliveryNoteNo,
            request.CatalogItemId,
            request.ProjectItemId,
            notes);
    }

    public void ApplyTo(ProjectExpense expense)
    {
        expense.Kind = Kind;
        expense.ExpenseDate = ExpenseDate;
        expense.SupplierId = SupplierId;
        expense.ItemName = ItemName;
        expense.Unit = Unit;
        expense.Quantity = Quantity;
        expense.UnitPrice = UnitPrice;
        expense.Category = Category;
        expense.Description = Description;
        expense.Amount = Amount;
        expense.Channel = Channel;
        expense.InvoiceNo = InvoiceNo;
        expense.DeliveryNoteNo = DeliveryNoteNo;
        expense.CatalogItemId = CatalogItemId;
        expense.Notes = Notes;
    }
}

/// <summary>What the database said about the ids on the form.</summary>
/// <param name="ItemStatus">The project item's status when one was named and found; <c>null</c> otherwise.</param>
/// <param name="CatalogPrice">The approved catalog price on the record's date, when a catalog item was named.</param>
internal sealed record ExpenseReferences(ProjectItemStatus? ItemStatus, decimal? CatalogPrice)
{
    /// <summary>
    /// Checks that the supplier exists and is active, and that the catalog item and the project item belong to
    /// this project, reporting each problem on its own field. The item's status is returned, not judged: an
    /// inactive item is a rule conflict (409), not a form error.
    /// </summary>
    public static async Task<ExpenseReferences> ResolveAsync(
        ProjectsDbContext db, int projectId, ExpenseFields fields, ValidationErrors errors, CancellationToken cancellationToken)
    {
        if (fields.SupplierId is { } supplierId)
        {
            var status = await db.Suppliers.AsNoTracking()
                .Where(supplier => supplier.Id == supplierId)
                .Select(supplier => (SupplierStatus?)supplier.Status)
                .FirstOrDefaultAsync(cancellationToken);

            if (status is null)
            {
                errors.Add("supplierId", "The supplier was not found.");
            }
            else if (status != SupplierStatus.Active)
            {
                errors.Add("supplierId", "This supplier is inactive and can't be chosen for new records.");
            }
        }

        decimal? catalogPrice = null;
        if (fields.CatalogItemId is { } catalogItemId)
        {
            var exists = await db.CatalogItems.AsNoTracking()
                .AnyAsync(item => item.Id == catalogItemId && item.ProjectId == projectId, cancellationToken);

            if (!exists)
            {
                errors.Add("catalogItemId", "The catalog item was not found on this project.");
            }
            else
            {
                catalogPrice = await CatalogPrices.CurrentAsync(db, catalogItemId, fields.ExpenseDate, cancellationToken);
            }
        }

        ProjectItemStatus? itemStatus = null;
        if (fields.ProjectItemId is { } itemId)
        {
            itemStatus = await db.ProjectItems.AsNoTracking()
                .Where(item => item.Id == itemId && item.ProjectId == projectId)
                .Select(item => (ProjectItemStatus?)item.Status)
                .FirstOrDefaultAsync(cancellationToken);

            if (itemStatus is null)
            {
                errors.Add("projectItemId", "The project item was not found on this project.");
            }
        }

        return new ExpenseReferences(itemStatus, catalogPrice);
    }
}
