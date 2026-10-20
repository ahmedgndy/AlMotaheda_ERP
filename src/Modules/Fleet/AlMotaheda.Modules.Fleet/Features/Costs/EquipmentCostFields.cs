using System.Globalization;
using AlMotaheda.Modules.Fleet.Common;
using AlMotaheda.Modules.Fleet.Data;
using AlMotaheda.Modules.Fleet.Data.Configurations;

namespace AlMotaheda.Modules.Fleet.Features.Costs;

/// <summary>The equipment cost form, as sent on record and on fix-and-resubmit.</summary>
internal interface IEquipmentCostFieldsRequest
{
    int? EquipmentId { get; }
    string? CostDate { get; }
    string? Type { get; }
    string? Basis { get; }
    decimal? Quantity { get; }
    decimal? Rate { get; }
    decimal? Liters { get; }
    decimal? PricePerLiter { get; }
    decimal? Amount { get; }
    string? Description { get; }
    int? SupplierId { get; }
    string? Channel { get; }
    string? InvoiceNo { get; }

    /// <summary>The work item the machine worked for (main or sub-item of the project); empty for the whole site.</summary>
    int? ProjectItemId { get; }
}

/// <summary>
/// The form after validation, with the amount set by the server (<see cref="EquipmentCostAmounts"/>). Fuel has
/// litres and a price per litre; every other type has a basis, and a quantity and rate unless it is a lump sum.
/// Rental and credit need a supplier. The work item is optional; whether it is on the project and active is asked
/// from Projects later (<see cref="EquipmentCostReferences"/>).
/// </summary>
internal sealed record EquipmentCostFields(
    int EquipmentId,
    DateOnly CostDate,
    EquipmentCostType Type,
    CostBasis? Basis,
    decimal? Quantity,
    decimal? Rate,
    decimal? Liters,
    decimal? PricePerLiter,
    decimal Amount,
    string Description,
    int? SupplierId,
    CostChannel Channel,
    string? InvoiceNo,
    int? ProjectItemId = null)
{
    /// <param name="today">The company's business date; a cost can't be dated after it.</param>
    public static EquipmentCostFields? Validate(IEquipmentCostFieldsRequest request, DateOnly today, ValidationErrors errors)
    {
        if (request.EquipmentId is null or < 1)
        {
            errors.Add("equipmentId", "equipmentId is required.");
        }

        var date = Input.RequiredDate(request.CostDate, errors, "costDate", "The date");
        if (date > today)
        {
            errors.Add("costDate", "The date can't be in the future.");
        }

        var type = Input.RequiredName<EquipmentCostType>(request.Type, errors, "type");
        var channel = Input.RequiredName<CostChannel>(request.Channel, errors, "channel");

        if (request.SupplierId is < 1)
        {
            errors.Add("supplierId", "supplierId is not valid.");
        }
        else if (request.SupplierId is null && (type == EquipmentCostType.Rental || channel == CostChannel.Credit))
        {
            // 05-fleet.md: rental needs a supplier, and so does credit (someone is owed).
            errors.Add("supplierId", "A rental, and anything bought on credit, needs its supplier.");
        }

        if (request.ProjectItemId is < 1)
        {
            errors.Add("projectItemId", "projectItemId is not valid.");
        }

        var description = Input.OptionalText(request.Description, EquipmentCostConfiguration.DescriptionMaxLength, errors, "description", "The description");
        var invoiceNo = Input.OptionalText(request.InvoiceNo, EquipmentCostConfiguration.InvoiceNoMaxLength, errors, "invoiceNo", "The invoice number");

        CostBasis? basis = null;
        decimal? quantity = null, rate = null, liters = null, pricePerLiter = null, lumpAmount = null;

        if (type == EquipmentCostType.Fuel)
        {
            liters = NumberInput.RequiredQuantity(request.Liters, errors, "liters", "Litres");
            pricePerLiter = NumberInput.RequiredMoney(request.PricePerLiter, errors, "pricePerLiter", "The price per litre");

            if (Input.Clean(request.Basis) is not null || request.Quantity is not null || request.Rate is not null)
            {
                errors.Add("basis", "Basis, quantity and rate don't apply to fuel: send litres and the price per litre.");
            }
        }
        else if (type is not null)
        {
            basis = Input.RequiredName<CostBasis>(request.Basis, errors, "basis");

            if (request.Liters is not null || request.PricePerLiter is not null)
            {
                errors.Add("liters", "Litres and the price per litre apply to fuel only.");
            }

            if (basis == CostBasis.Lump)
            {
                lumpAmount = NumberInput.RequiredMoney(request.Amount, errors, "amount", "The amount");
                if (request.Quantity is not null || request.Rate is not null)
                {
                    errors.Add("quantity", "A lump sum has no quantity or rate: send the amount only.");
                }
            }
            else if (basis is not null)
            {
                quantity = NumberInput.RequiredQuantity(request.Quantity, errors, "quantity", "The quantity");
                rate = NumberInput.RequiredMoney(request.Rate, errors, "rate", "The rate");
            }

            if (description is null && !errors.Has("description"))
            {
                errors.Add("description", "Describe the cost.");
            }
        }

        decimal amount = 0;
        if (!errors.HasErrors)
        {
            amount = EquipmentCostAmounts.Compute(type!.Value, basis, quantity, rate, liters, pricePerLiter, lumpAmount);
            if (amount <= 0 || amount > NumberInput.MaxMoney)
            {
                errors.Add(type == EquipmentCostType.Fuel ? "liters" : "quantity",
                    $"The amount must be between 0.01 and {NumberInput.MaxMoney:0.00}.");
            }
        }

        if (errors.HasErrors)
        {
            return null;
        }

        // Fuel needs no description: the litres and the price say what it was.
        description ??= string.Create(CultureInfo.InvariantCulture, $"{liters:0.###} L × {pricePerLiter:0.00}");

        return new EquipmentCostFields(
            request.EquipmentId!.Value,
            date!.Value,
            type!.Value,
            basis,
            quantity,
            rate,
            liters,
            pricePerLiter,
            amount,
            description,
            request.SupplierId,
            channel!.Value,
            invoiceNo,
            request.ProjectItemId);
    }

    public void ApplyTo(EquipmentCost cost)
    {
        cost.EquipmentId = EquipmentId;
        cost.CostDate = CostDate;
        cost.Type = Type;
        cost.Basis = Basis;
        cost.Quantity = Quantity;
        cost.Rate = Rate;
        cost.Liters = Liters;
        cost.PricePerLiter = PricePerLiter;
        cost.Amount = Amount;
        cost.Description = Description;
        cost.SupplierId = SupplierId;
        cost.Channel = Channel;
        cost.InvoiceNo = InvoiceNo;
        cost.ProjectItemId = ProjectItemId;
    }
}
