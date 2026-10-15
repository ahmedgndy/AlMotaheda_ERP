using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Subcontractors.Common;
using AlMotaheda.Modules.Subcontractors.Data;
using AlMotaheda.Modules.Subcontractors.Data.Configurations;

namespace AlMotaheda.Modules.Subcontractors.Features.ContractItems;

/// <summary>The contract item form, as sent on create and on edit.</summary>
internal interface IContractItemFieldsRequest
{
    string? Name { get; }
    string? WorkType { get; }
    string? Unit { get; }
    decimal? Quantity { get; }
    decimal? UnitPrice { get; }
    string? Description { get; }
    string? Notes { get; }
}

/// <summary>The contract item form after validation.</summary>
internal sealed record ContractItemFields(
    string Name,
    string WorkType,
    string Unit,
    decimal Quantity,
    decimal UnitPrice,
    string? Description,
    string? Notes)
{
    public static ContractItemFields? Validate(IContractItemFieldsRequest request, ValidationErrors errors)
    {
        var name = Input.RequiredText(request.Name, ContractItemConfiguration.NameMaxLength, errors, "name", "The item name");

        if (!WorkTypes.TryParse(request.WorkType, out var workType))
        {
            errors.Add("workType", $"workType must be one of: {string.Join(", ", WorkTypes.All)}.");
        }

        if (!MeasurementUnits.TryParse(request.Unit, out var unit))
        {
            errors.Add("unit", $"unit must be one of: {string.Join(", ", MeasurementUnits.All)}.");
        }

        var quantity = NumberInput.RequiredQuantity(request.Quantity, errors, "quantity", "The quantity");
        var unitPrice = NumberInput.RequiredMoney(request.UnitPrice, errors, "unitPrice", "The unit price");
        var description = Input.OptionalText(request.Description, ContractItemConfiguration.DescriptionMaxLength, errors, "description", "The description");
        var notes = Input.OptionalText(request.Notes, ContractItemConfiguration.NotesMaxLength, errors, "notes", "Notes");

        return errors.HasErrors
            ? null
            : new ContractItemFields(name!, workType, unit, quantity!.Value, unitPrice!.Value, description, notes);
    }

    public void ApplyTo(ContractItem item)
    {
        item.Name = Name;
        item.WorkType = WorkType;
        item.Unit = Unit;
        item.Quantity = Quantity;
        item.UnitPrice = UnitPrice;
        item.Description = Description;
        item.Notes = Notes;
    }
}

/// <summary>Who may add and edit contract items and record measurements: subcontractors Create or Edit.</summary>
internal static class ItemPermissions
{
    public static bool CanWrite(SubCaller caller) => caller.Can(PermissionAction.Create) || caller.Can(PermissionAction.Edit);
}

/// <summary>The answer to "may work be linked to this project item?".</summary>
internal enum ProjectItemCheck
{
    Fits,

    /// <summary>No such item on this project (400 on the form field).</summary>
    NotFound,

    /// <summary>The item is inactive (409 <c>item_inactive</c>).</summary>
    Inactive,
}

/// <summary>
/// The one check before work is linked to a project item: the item must be on the same project and active. Asked
/// from Projects through <see cref="IProjectItems"/>, which leaves out items of other projects.
/// </summary>
internal static class ProjectItemLinks
{
    public static async Task<ProjectItemCheck> CheckAsync(IProjectItems projectItems, int projectId, int projectItemId, CancellationToken cancellationToken)
    {
        var found = await projectItems.FindAsync(projectId, [projectItemId], cancellationToken);
        if (!found.TryGetValue(projectItemId, out var item))
        {
            return ProjectItemCheck.NotFound;
        }

        return item.IsActive ? ProjectItemCheck.Fits : ProjectItemCheck.Inactive;
    }
}
