using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Data.Configurations;
using AlMotaheda.Modules.Projects.Features.Catalog;
using AlMotaheda.Modules.Projects.Features.Prices;
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

    /// <summary>Supply only, optional: the company price list item the purchase is compared with.</summary>
    int? PriceItemId { get; }

    /// <summary>Required when the unit price is above the reference range (and the policy lets it through).</summary>
    string? PriceJustification { get; }
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
    string? Notes,
    int? PriceItemId = null,
    string? PriceJustification = null)
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
        var justification = Input.OptionalText(
            request.PriceJustification, ProjectExpenseConfiguration.PriceJustificationMaxLength, errors, "priceJustification", "The justification");

        if (request.PriceItemId is < 1)
        {
            errors.Add("priceItemId", "priceItemId is not valid.");
        }

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

            if (request.PriceItemId is not null)
            {
                errors.Add("priceItemId", "A price list item applies to supplies only.");
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
            notes,
            request.PriceItemId,
            justification);
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

    /// <summary>
    /// Copies the price comparison the server made (<see cref="ExpenseReferences.Price"/>) onto the record: the
    /// reference range as it was on the purchase date, so a later range never rewrites this comparison.
    /// </summary>
    public void ApplyPriceCheckTo(ProjectExpense expense, PriceReference? price)
    {
        expense.PriceItemId = price?.PriceItemId;
        expense.PriceRangeId = price?.Range?.RangeId;
        expense.ReferenceMinPrice = price?.Range?.MinPrice;
        expense.ReferenceMaxPrice = price?.Range?.MaxPrice;
        expense.PriceCheck = price?.Comparison.Result;
        expense.PriceJustification = price is null ? null : PriceJustification;
    }
}

/// <summary>What the database said about the ids on the form.</summary>
/// <param name="ItemStatus">The project item's status when one was named and found; <c>null</c> otherwise.</param>
/// <param name="CatalogPrice">The approved catalog price on the record's date, when a catalog item was named.</param>
/// <param name="SupplierKind">The supplier's kind when one was named and found; <c>null</c> otherwise.</param>
internal sealed record ExpenseReferences(ProjectItemStatus? ItemStatus, decimal? CatalogPrice, SupplierKind? SupplierKind = null, PriceReference? Price = null)
{
    /// <summary>The supplier sells fuel or equipment: such costs belong in Site equipment (409 <c>use_equipment_costs</c>).</summary>
    public bool IsEquipmentCost => SupplierKind is { } kind && ExpenseRules.IsEquipmentSupplier(kind);

    /// <summary>
    /// Checks that the supplier exists and is active, and that the catalog item and the project item belong to
    /// this project, reporting each problem on its own field. The item's status is returned, not judged: an
    /// inactive item is a rule conflict (409), not a form error.
    /// </summary>
    public static async Task<ExpenseReferences> ResolveAsync(
        ProjectsDbContext db, int projectId, ExpenseFields fields, ValidationErrors errors, CancellationToken cancellationToken)
    {
        SupplierKind? supplierKind = null;
        if (fields.SupplierId is { } supplierId)
        {
            var supplier = await db.Suppliers.AsNoTracking()
                .Where(candidate => candidate.Id == supplierId)
                .Select(candidate => new { candidate.Status, candidate.Kind })
                .FirstOrDefaultAsync(cancellationToken);

            if (supplier is null)
            {
                errors.Add("supplierId", "The supplier was not found.");
            }
            else if (supplier.Status != SupplierStatus.Active)
            {
                errors.Add("supplierId", "This supplier is inactive and can't be chosen for new records.");
            }
            else
            {
                supplierKind = supplier.Kind;
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

        PriceReference? price = null;
        if (fields.PriceItemId is { } priceItemId && fields.Unit is { } unit && fields.UnitPrice is { } unitPrice && fields.Quantity is { } quantity)
        {
            var item = await db.PriceItems.AsNoTracking()
                .Where(candidate => candidate.Id == priceItemId)
                .Select(candidate => new { candidate.Unit, candidate.Status })
                .SingleOrDefaultAsync(cancellationToken);

            if (item is null)
            {
                errors.Add("priceItemId", "The price list item was not found.");
            }
            else if (!string.Equals(item.Unit, unit, StringComparison.Ordinal))
            {
                // A price per ton can't be compared with a price per bag: the supply must use the item's unit.
                errors.Add("unit", $"The price list item is priced per {item.Unit}: record the quantity in {item.Unit}.");
            }
            else
            {
                var range = await PriceRanges.ApplicableAsync(db, priceItemId, fields.SupplierId, fields.ExpenseDate, cancellationToken);
                var comparison = PriceCheck.Compare(unitPrice, quantity, range?.MinPrice, range?.MaxPrice);
                price = new PriceReference(priceItemId, item.Status == PriceItemStatus.Active, range, comparison);
            }
        }

        return new ExpenseReferences(itemStatus, catalogPrice, supplierKind, price);
    }
}

/// <summary>The price list item a supply names and how its unit price compares with the range on its date.</summary>
/// <param name="Range"><c>null</c> when no approved range applies on the purchase date.</param>
internal sealed record PriceReference(int PriceItemId, bool IsActive, ApplicableRange? Range, PriceComparison Comparison);
