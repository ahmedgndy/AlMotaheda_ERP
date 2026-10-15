using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Workforce.Common;
using AlMotaheda.Modules.Workforce.Data;
using AlMotaheda.Modules.Workforce.Data.Configurations;

namespace AlMotaheda.Modules.Workforce.Features.Sheets;

/// <summary>An allowance or deduction typed on a line.</summary>
internal sealed record LaborAdjustmentInput(string? Kind, string? Type, decimal? Amount, string? Reason);

/// <summary>A part of a worker's day on a project item.</summary>
internal sealed record LaborPortionInput(int? ItemId, decimal? Portion);

/// <summary>One worker on a sheet form. The server computes every amount; the client sends no rate.</summary>
/// <param name="TradeId">The trade worked that day; the worker's own trade when empty.</param>
/// <param name="WorkType">One of <c>WorkTypes</c>; the trade's default when empty.</param>
internal sealed record LaborLineInput(
    int? WorkerId,
    int? TradeId,
    decimal? Days,
    decimal? OvertimeHours,
    string? WorkType,
    string? Notes,
    IReadOnlyList<LaborAdjustmentInput>? Adjustments,
    IReadOnlyList<LaborPortionInput>? Allocations);

/// <summary>The fields of the sheet form, shared by create and edit.</summary>
internal interface ISheetFormRequest
{
    string? WorkDate { get; }
    string? Shift { get; }
    string? Notes { get; }
    bool? Submit { get; }
    IReadOnlyList<LaborLineInput>? Lines { get; }
}

internal sealed record AdjustmentForm(AdjustmentKind Kind, AdjustmentType Type, decimal Amount, string? Reason);

/// <param name="Index">The line's position in the form, for error messages.</param>
internal sealed record LineForm(
    int Index,
    int WorkerId,
    int? TradeId,
    decimal Days,
    decimal OvertimeHours,
    string? WorkType,
    string? Notes,
    IReadOnlyList<AdjustmentForm> Adjustments,
    IReadOnlyList<PortionForm> Allocations);

/// <summary>A sheet form after validation.</summary>
internal sealed record SheetFields(DateOnly WorkDate, Shift Shift, string? Notes, bool Submit, IReadOnlyList<LineForm> Lines);

/// <summary>
/// The input rules of the sheet form, with no database access. Every failing field is reported at once, with
/// its path (<c>lines[2].adjustments[0].reason</c>).
/// </summary>
internal static class SheetForm
{
    public const int MaxLines = 200;
    public const int MaxAdjustmentsPerLine = 20;
    public const decimal MaxOvertimeHours = 12m;

    /// <summary>How many days back a sheet may be recorded: today or one of the 7 days before.</summary>
    public const int DaysBack = 7;

    /// <summary>The message when a work date is outside the window.</summary>
    public const string WorkDateWindowMessage = "The work date must be today or one of the 7 days before.";

    /// <param name="checkDateWindow">False on edit: there the 7-day window only applies when the date changes (checked
    /// once the saved date is known), so a sheet returned after a week can still be fixed.</param>
    public static SheetFields? Validate(ISheetFormRequest request, DateOnly today, bool checkDateWindow, ValidationErrors errors)
    {
        var workDate = Input.RequiredDate(request.WorkDate, errors, "workDate", "The work date");
        if (workDate is { } date && checkDateWindow && !SheetRules.WorkDateAllowed(date, today))
        {
            errors.Add("workDate", WorkDateWindowMessage);
        }

        if (!Input.TryParseName<Shift>(request.Shift, out var shift))
        {
            errors.Add("shift", $"shift must be one of: {string.Join(", ", Enum.GetNames<Shift>())}.");
        }

        var notes = Input.OptionalText(request.Notes, DailySheetConfiguration.NotesMaxLength, errors, "notes", "Notes");

        var lines = new List<LineForm>();
        if (request.Lines is null || request.Lines.Count == 0)
        {
            errors.Add("lines", "At least one worker is required.");
        }
        else if (request.Lines.Count > MaxLines)
        {
            errors.Add("lines", $"A sheet can have at most {MaxLines} workers.");
        }
        else
        {
            var workers = new HashSet<int>();
            for (var index = 0; index < request.Lines.Count; index++)
            {
                if (ValidateLine(request.Lines[index], index, workers, errors) is { } line)
                {
                    lines.Add(line);
                }
            }
        }

        return errors.HasErrors ? null : new SheetFields(workDate!.Value, shift, notes, request.Submit == true, lines);
    }

    /// <summary>
    /// The allocation list of one line: distinct items, every portion above 0 and at most 1 with four decimals.
    /// The sum is checked later (409 <c>allocation_exceeds_day</c>), as a business rule.
    /// </summary>
    public static IReadOnlyList<PortionForm>? ValidatePortions(IReadOnlyList<LaborPortionInput>? allocations, string field, ValidationErrors errors)
    {
        if (allocations is null || allocations.Count == 0)
        {
            return [];
        }

        if (allocations.Count > LaborAllocator.MaxItemsPerLine)
        {
            errors.Add(field, $"A line can be split over at most {LaborAllocator.MaxItemsPerLine} items.");
            return null;
        }

        var clean = new List<PortionForm>(allocations.Count);
        var items = new HashSet<int>();
        var usable = true;

        for (var index = 0; index < allocations.Count; index++)
        {
            var (itemId, portion) = (allocations[index].ItemId, allocations[index].Portion);
            var itemField = $"{field}[{index}].itemId";
            var portionField = $"{field}[{index}].portion";

            if (itemId is null or < 1)
            {
                errors.Add(itemField, "itemId is required.");
                usable = false;
            }
            else if (!items.Add(itemId.Value))
            {
                errors.Add(itemField, "The same item is listed twice on this line. Add the portions into one.");
                usable = false;
            }

            if (portion is null)
            {
                errors.Add(portionField, "portion is required.");
                usable = false;
            }
            else if (portion <= 0 || portion > LaborAllocator.WholeDay || decimal.Round(portion.Value, 4) != portion.Value)
            {
                errors.Add(portionField, "portion must be above 0 and at most 1, with at most four decimals (0.5 = half the day).");
                usable = false;
            }

            if (usable)
            {
                clean.Add(new PortionForm(itemId!.Value, portion!.Value));
            }
        }

        return usable ? clean : null;
    }

    private static LineForm? ValidateLine(LaborLineInput? line, int index, HashSet<int> workers, ValidationErrors errors)
    {
        var field = $"lines[{index}]";
        if (line is null)
        {
            errors.Add(field, "The line is empty.");
            return null;
        }

        var before = errors.Count;

        if (line.WorkerId is null or < 1)
        {
            errors.Add($"{field}.workerId", "workerId is required.");
        }
        else if (!workers.Add(line.WorkerId.Value))
        {
            errors.Add($"{field}.workerId", "The same worker is listed twice on this sheet.");
        }

        if (line.TradeId is < 1)
        {
            errors.Add($"{field}.tradeId", "tradeId is not valid.");
        }

        if (line.Days is not (1m or 0.5m))
        {
            errors.Add($"{field}.days", "days must be 1 (a full day) or 0.5 (half a day).");
        }

        var overtime = line.OvertimeHours ?? 0m;
        if (overtime < 0 || overtime > MaxOvertimeHours || decimal.Round(overtime, 1) != overtime)
        {
            errors.Add($"{field}.overtimeHours", $"overtimeHours must be between 0 and {MaxOvertimeHours:0}, with at most one decimal.");
        }

        string? workType = null;
        if (Input.Clean(line.WorkType) is { } workTypeText)
        {
            if (WorkTypes.TryParse(workTypeText, out var parsed))
            {
                workType = parsed;
            }
            else
            {
                errors.Add($"{field}.workType", $"workType must be one of: {string.Join(", ", WorkTypes.All)}.");
            }
        }

        var notes = Input.OptionalText(line.Notes, AttendanceLineConfiguration.NotesMaxLength, errors, $"{field}.notes", "Notes");
        var adjustments = ValidateAdjustments(line.Adjustments, $"{field}.adjustments", errors);
        var allocations = ValidatePortions(line.Allocations, $"{field}.allocations", errors);

        if (errors.Count != before || adjustments is null || allocations is null)
        {
            return null;
        }

        return new LineForm(index, line.WorkerId!.Value, line.TradeId, line.Days!.Value, overtime, workType, notes, adjustments, allocations);
    }

    private static List<AdjustmentForm>? ValidateAdjustments(IReadOnlyList<LaborAdjustmentInput>? adjustments, string field, ValidationErrors errors)
    {
        if (adjustments is null || adjustments.Count == 0)
        {
            return [];
        }

        if (adjustments.Count > MaxAdjustmentsPerLine)
        {
            errors.Add(field, $"A line can have at most {MaxAdjustmentsPerLine} allowances and deductions.");
            return null;
        }

        var clean = new List<AdjustmentForm>(adjustments.Count);
        var before = errors.Count;

        for (var index = 0; index < adjustments.Count; index++)
        {
            var adjustment = adjustments[index];
            var itemField = $"{field}[{index}]";
            if (adjustment is null)
            {
                errors.Add(itemField, "The adjustment is empty.");
                continue;
            }

            var kindKnown = Input.TryParseName<AdjustmentKind>(adjustment.Kind, out var kind);
            if (!kindKnown)
            {
                errors.Add($"{itemField}.kind", $"kind must be one of: {string.Join(", ", Enum.GetNames<AdjustmentKind>())}.");
            }

            if (!Input.TryParseName<AdjustmentType>(adjustment.Type, out var type))
            {
                errors.Add($"{itemField}.type", $"type must be one of: {string.Join(", ", Enum.GetNames<AdjustmentType>())}.");
            }

            var amount = NumberInput.RequiredMoney(adjustment.Amount, errors, $"{itemField}.amount", "The amount");

            // Every deduction says why (the prototype's only rule on the team editor).
            var reason = Input.OptionalText(adjustment.Reason, LineAdjustmentConfiguration.ReasonMaxLength, errors, $"{itemField}.reason", "The reason");
            if (kindKnown && kind == AdjustmentKind.Deduction && reason is null)
            {
                errors.Add($"{itemField}.reason", "A reason is required for every deduction.");
            }

            if (amount is not null)
            {
                clean.Add(new AdjustmentForm(kind, type, amount.Value, reason));
            }
        }

        return errors.Count == before ? clean : null;
    }
}
