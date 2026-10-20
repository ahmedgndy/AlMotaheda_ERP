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

    /// <summary>Supply only, optional: the supply agreement (same project and supplier) the delivery is under.</summary>
    int? SupplyAgreementId => null;

    /// <summary>A direct purchase paid from custody with no registered supplier: who was paid (the shop).</summary>
    string? PayeeName => null;
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
    string? PriceJustification = null,
    int? SupplyAgreementId = null,
    string? PayeeName = null)
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

        var payeeName = Input.OptionalText(request.PayeeName, ProjectExpenseConfiguration.PayeeNameMaxLength, errors, "payeeName", "The payee");
        if (request.SupplyAgreementId is < 1)
        {
            errors.Add("supplyAgreementId", "supplyAgreementId is not valid.");
        }

        if (payeeName is not null && request.SupplierId is not null)
        {
            errors.Add("payeeName", "A payee name is for a direct purchase with no registered supplier.");
        }

        if (payeeName is not null && hasChannel && channel != PaymentChannel.Custody)
        {
            errors.Add("payeeName", "A direct purchase with no registered supplier is paid from custody.");
        }

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
            // A supply names its supplier; a direct purchase paid in cash from custody may instead name who was paid.
            if (request.SupplierId is null && payeeName is null)
            {
                errors.Add("supplierId", "A supply needs its supplier (or, for a cash purchase from custody, the shop it was bought from).");
            }

            if (request.SupplyAgreementId is not null && request.SupplierId is null)
            {
                errors.Add("supplyAgreementId", "A supply agreement is with a registered supplier.");
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

            // A site expense may name a price list item of a lump-sum or recurring kind (a meal, a subscription, a
            // fee): its amount is compared with the range as one unit.
            if (request.SupplyAgreementId is not null)
            {
                errors.Add("supplyAgreementId", "A supply agreement applies to supplies only.");
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
            justification,
            request.SupplyAgreementId,
            payeeName);
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
        expense.SupplyAgreementId = SupplyAgreementId;
        expense.PayeeName = PayeeName;
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
        if (fields.PriceItemId is { } priceItemId)
        {
            var item = await db.PriceItems.AsNoTracking()
                .Where(candidate => candidate.Id == priceItemId)
                .Select(candidate => new { candidate.Unit, candidate.Status })
                .SingleOrDefaultAsync(cancellationToken);

            // A supply compares its unit price per the item's unit; a site expense compares its amount as one unit
            // (a meal, a monthly subscription, a lump-sum service).
            var (comparedPrice, comparedQuantity) = fields.Kind == ExpenseKind.Supply ? (fields.UnitPrice, fields.Quantity) : (fields.Amount, 1m);

            if (item is null)
            {
                errors.Add("priceItemId", "The price list item was not found.");
            }
            else if (fields.Kind == ExpenseKind.Supply && !string.Equals(item.Unit, fields.Unit, StringComparison.Ordinal))
            {
                // A price per ton can't be compared with a price per bag: the supply must use the item's unit.
                errors.Add("unit", $"The price list item is priced per {item.Unit}: record the quantity in {item.Unit}.");
            }
            else if (comparedPrice is { } unitPrice && comparedQuantity is { } quantity)
            {
                var range = await PriceRanges.ApplicableAsync(db, priceItemId, fields.SupplierId, fields.ExpenseDate, cancellationToken);
                var comparison = PriceCheck.Compare(unitPrice, quantity, range?.MinPrice, range?.MaxPrice);
                price = new PriceReference(priceItemId, item.Status == PriceItemStatus.Active, range, comparison, item.Unit, unitPrice, quantity);
            }
        }

        return new ExpenseReferences(itemStatus, catalogPrice, supplierKind, price);
    }
}

/// <summary>The price list item a supply names and how its unit price compares with the range on its date.</summary>
/// <param name="Range"><c>null</c> when no approved range applies on the purchase date.</param>
/// <param name="Unit">The item's unit (the record's unit for a supply; the lump-sum unit for a site expense).</param>
/// <param name="ComparedPrice">What was compared: the unit price of a supply, the amount of a site expense.</param>
internal sealed record PriceReference(int PriceItemId, bool IsActive, ApplicableRange? Range, PriceComparison Comparison, string Unit, decimal ComparedPrice, decimal ComparedQuantity);

/// <summary>A supply recorded under a supply agreement: the agreement decides its work item and its unit.</summary>
internal static class ExpenseAgreements
{
    /// <summary>
    /// Checks the agreement (this project, this supplier, same unit, active) and returns the form with the agreement's
    /// work item. A form error is added to <paramref name="errors"/>; a closed agreement is the returned conflict.
    /// </summary>
    /// <param name="keptAgreementId">On resubmit, the record's current agreement: keeping one that was closed since is allowed.</param>
    public static async Task<(ExpenseFields Fields, Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult? Problem)> ApplyAsync(
        ProjectsDbContext db, int projectId, ExpenseFields fields, ValidationErrors errors, CancellationToken cancellationToken, int? keptAgreementId = null)
    {
        if (fields.SupplyAgreementId is not { } agreementId)
        {
            return (fields, null);
        }

        var agreement = await db.SupplyAgreements.AsNoTracking()
            .Where(candidate => candidate.Id == agreementId && candidate.ProjectId == projectId)
            .Select(candidate => new { candidate.SupplierId, candidate.ProjectItemId, candidate.Unit, candidate.Status })
            .SingleOrDefaultAsync(cancellationToken);

        if (agreement is null)
        {
            errors.Add("supplyAgreementId", "The supply agreement was not found on this project.");
            return (fields, null);
        }

        if (agreement.SupplierId != fields.SupplierId)
        {
            errors.Add("supplyAgreementId", "The supply agreement is with another supplier.");
        }

        if (!string.Equals(agreement.Unit, fields.Unit, StringComparison.Ordinal))
        {
            errors.Add("unit", $"The supply agreement is in {agreement.Unit}: record the quantity in {agreement.Unit}.");
        }

        if (fields.ProjectItemId is { } itemId && itemId != agreement.ProjectItemId)
        {
            errors.Add("projectItemId", "A supply under an agreement goes to the agreement's work item.");
        }

        if (agreement.Status == SupplyAgreementStatus.Closed && agreementId != keptAgreementId)
        {
            return (fields, WorkspaceProblems.Conflict("supply_agreement_closed", "This supply agreement is closed. Choose another one or record the supply without it."));
        }

        return (fields with { ProjectItemId = agreement.ProjectItemId }, null);
    }
}
