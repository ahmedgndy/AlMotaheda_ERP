using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Data.Configurations;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Features.Budgets;

/// <summary>One budget line as sent by the client. The amount of a priced line is computed by the server.</summary>
internal sealed record BudgetLineRequest(
    string? CostGroup,
    int? ProjectItemId,
    string? Description,
    string? Unit,
    decimal? Quantity,
    decimal? UnitPrice,
    decimal? Amount);

/// <summary>
/// One budget line after validation. A priced line has a quantity and a unit price and its amount is their rounded
/// product; a lump sum has neither, and its amount is the estimate itself.
/// </summary>
internal sealed record BudgetLineInput(
    string CostGroup,
    int? ProjectItemId,
    string Description,
    string? Unit,
    decimal? Quantity,
    decimal? UnitPrice,
    decimal Amount)
{
    public BudgetLine ToLine(int lineNo) => new()
    {
        LineNo = lineNo,
        CostGroup = CostGroup,
        ProjectItemId = ProjectItemId,
        Description = Description,
        Unit = Unit,
        Quantity = Quantity,
        UnitPrice = UnitPrice,
        Amount = Amount,
    };
}

/// <summary>The input rules of a budget: its title, notes and lines. Every failing field is reported at once.</summary>
internal static class BudgetFields
{
    /// <summary>Far more than any real estimate; stops one request from writing an unbounded number of rows.</summary>
    public const int MaxLines = 500;

    public static string? Title(string? value, ValidationErrors errors) =>
        Input.OptionalText(value, BudgetConfiguration.TitleMaxLength, errors, "title", "The title");

    public static string? Notes(string? value, ValidationErrors errors) =>
        Input.OptionalText(value, BudgetConfiguration.NotesMaxLength, errors, "notes", "The notes");

    /// <summary>Validates the lines in order. Field names are <c>lines[i].field</c>, like the JSON.</summary>
    public static List<BudgetLineInput> Lines(IReadOnlyList<BudgetLineRequest?> lines, ValidationErrors errors)
    {
        if (lines.Count > MaxLines)
        {
            errors.Add("lines", $"A budget can have at most {MaxLines} lines.");
            return [];
        }

        var result = new List<BudgetLineInput>(lines.Count);
        for (var index = 0; index < lines.Count; index++)
        {
            if (Line(lines[index], $"lines[{index}]", errors) is { } line)
            {
                result.Add(line);
            }
        }

        return result;
    }

    /// <summary>
    /// Every line naming a project item must name an item of this project. Inactive items are accepted: a budget
    /// is a plan, and an inactive item still has its cost.
    /// </summary>
    public static async Task CheckItemsAsync(
        ProjectsDbContext db, int projectId, IReadOnlyList<BudgetLineInput> lines, ValidationErrors errors, CancellationToken cancellationToken)
    {
        var ids = lines.Where(line => line.ProjectItemId is not null).Select(line => line.ProjectItemId!.Value).Distinct().ToList();
        if (ids.Count == 0)
        {
            return;
        }

        var known = await db.ProjectItems.AsNoTracking()
            .Where(item => item.ProjectId == projectId && ids.Contains(item.Id))
            .Select(item => item.Id)
            .ToListAsync(cancellationToken);

        for (var index = 0; index < lines.Count; index++)
        {
            if (lines[index].ProjectItemId is { } itemId && !known.Contains(itemId))
            {
                errors.Add($"lines[{index}].projectItemId", "This project item was not found on this project.");
            }
        }
    }

    private static BudgetLineInput? Line(BudgetLineRequest? line, string prefix, ValidationErrors errors)
    {
        if (line is null)
        {
            errors.Add(prefix, "The line is empty.");
            return null;
        }

        var before = errors.ToDictionary().Count;

        if (!CostGroups.TryParse(line.CostGroup, out var costGroup))
        {
            errors.Add($"{prefix}.costGroup", $"costGroup must be one of: {string.Join(", ", CostGroups.All)}.");
        }

        if (line.ProjectItemId is < 1)
        {
            errors.Add($"{prefix}.projectItemId", "projectItemId is not valid.");
        }

        var description = Input.RequiredText(line.Description, BudgetLineConfiguration.DescriptionMaxLength, errors, $"{prefix}.description", "The description");

        string? unit = null;
        if (Input.Clean(line.Unit) is { } unitText && !MeasurementUnits.TryParse(unitText, out unit))
        {
            errors.Add($"{prefix}.unit", $"unit must be one of: {string.Join(", ", MeasurementUnits.All)}.");
        }

        decimal? quantity = null, unitPrice = null, amount = null;
        if (line.Quantity is null && line.UnitPrice is null)
        {
            // A lump sum: the amount is the estimate.
            amount = NumberInput.RequiredMoney(line.Amount, errors, $"{prefix}.amount", "The amount");
        }
        else
        {
            // A priced line: both are needed, and the client's amount is ignored.
            quantity = NumberInput.RequiredQuantity(line.Quantity, errors, $"{prefix}.quantity", "The quantity");
            unitPrice = NumberInput.RequiredMoney(line.UnitPrice, errors, $"{prefix}.unitPrice", "The unit price");
            if (quantity is { } q && unitPrice is { } p)
            {
                amount = BudgetArithmetic.LineAmount(q, p);
                if (amount <= 0 || amount > NumberInput.MaxMoney)
                {
                    errors.Add($"{prefix}.amount", $"Quantity × unit price must be between 0.01 and {NumberInput.MaxMoney:0.00}.");
                }
            }
        }

        return errors.ToDictionary().Count == before
            ? new BudgetLineInput(costGroup, line.ProjectItemId, description!, unit, quantity, unitPrice, amount!.Value)
            : null;
    }
}

/// <summary>The money rule of a budget line, in one place so the check constraint and the server agree.</summary>
internal static class BudgetArithmetic
{
    /// <summary>Quantity × unit price, rounded to the piastre (halves away from zero).</summary>
    public static decimal LineAmount(decimal quantity, decimal unitPrice) => Money.Round(quantity * unitPrice);
}
