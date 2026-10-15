using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Data.Configurations;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Features.DailyReports;

/// <summary>One piece of work done that day, as sent by the client.</summary>
internal sealed record DailyReportLineRequest(
    string? WorkType,
    string? Description,
    string? Unit,
    decimal? Quantity,
    string? PerformedBy,
    int? ProjectItemId,
    string? SubcontractorName,
    int? SubContractId,
    int? WorkersCount);

/// <summary>One material used that day, as sent by the client.</summary>
internal sealed record DailyReportMaterialRequest(string? MaterialName, string? Unit, decimal? Quantity);

/// <summary>One piece of equipment used that day, as sent by the client.</summary>
internal sealed record DailyReportEquipmentRequest(int? EquipmentId, string? EquipmentName, decimal? Hours);

/// <summary>The daily report form, as sent on create and on edit. The date is set on create only.</summary>
internal interface IDailyReportRequest
{
    int? WorkersPresent { get; }
    string? Weather { get; }
    string? Issues { get; }
    string? Notes { get; }
    IReadOnlyList<DailyReportLineRequest?>? Lines { get; }
    IReadOnlyList<DailyReportMaterialRequest?>? Materials { get; }
    IReadOnlyList<DailyReportEquipmentRequest?>? Equipment { get; }
    int? ReportRequestId { get; }
}

internal sealed record DailyReportLineInput(
    string WorkType,
    string Description,
    string Unit,
    decimal Quantity,
    WorkPerformer PerformedBy,
    int? ProjectItemId,
    string? SubcontractorName,
    int? SubContractId,
    int? WorkersCount);

internal sealed record DailyReportMaterialInput(string MaterialName, string Unit, decimal Quantity);

internal sealed record DailyReportEquipmentInput(int? EquipmentId, string EquipmentName, decimal? Hours);

/// <summary>
/// The daily report form after validation (02-projects-planning.md, section C). It holds no money: a daily report
/// describes the day; labor and subcontract cost are recorded in their own modules.
/// </summary>
internal sealed record DailyReportFields(
    int? WorkersPresent,
    string? Weather,
    string? Issues,
    string? Notes,
    IReadOnlyList<DailyReportLineInput> Lines,
    IReadOnlyList<DailyReportMaterialInput> Materials,
    IReadOnlyList<DailyReportEquipmentInput> Equipment,
    int? ReportRequestId)
{
    public const int MaxLines = 200;
    public const int MaxMaterials = 100;
    public const int MaxEquipment = 100;

    public static DailyReportFields? Validate(IDailyReportRequest request, ValidationErrors errors)
    {
        if (request.WorkersPresent is < 0 or > DailyReportConfiguration.MaxWorkersPresent)
        {
            errors.Add("workersPresent", $"workersPresent must be between 0 and {DailyReportConfiguration.MaxWorkersPresent}.");
        }

        var weather = Input.OptionalText(request.Weather, DailyReportConfiguration.WeatherMaxLength, errors, "weather", "The weather");
        var issues = Input.OptionalText(request.Issues, DailyReportConfiguration.TextMaxLength, errors, "issues", "The issues");
        var notes = Input.OptionalText(request.Notes, DailyReportConfiguration.TextMaxLength, errors, "notes", "The notes");

        if (request.ReportRequestId is < 1)
        {
            errors.Add("reportRequestId", "reportRequestId is not valid.");
        }

        var lines = Each(request.Lines, MaxLines, "lines", errors, Line);
        var materials = Each(request.Materials, MaxMaterials, "materials", errors, Material);
        var equipment = Each(request.Equipment, MaxEquipment, "equipment", errors, EquipmentRow);

        // A report says what was done, or why nothing was (prototype rule "site.needWork").
        if ((request.Lines is null || request.Lines.Count == 0) && issues is null && !errors.Has("lines"))
        {
            errors.Add("lines", "Add at least one work line, or describe an issue.");
        }

        return errors.HasErrors
            ? null
            : new DailyReportFields(request.WorkersPresent, weather, issues, notes, lines, materials, equipment, request.ReportRequestId);
    }

    /// <summary>
    /// Project items must be on this project and active, except an item the report already named (it stays
    /// linked). A report request must be on this project.
    /// </summary>
    public async Task CheckReferencesAsync(
        ProjectsDbContext db, int projectId, IReadOnlyCollection<int> itemsAlreadyNamed, ValidationErrors errors, CancellationToken cancellationToken)
    {
        var itemIds = Lines.Where(line => line.ProjectItemId is not null).Select(line => line.ProjectItemId!.Value).Distinct().ToList();
        if (itemIds.Count > 0)
        {
            var items = await db.ProjectItems.AsNoTracking()
                .Where(item => item.ProjectId == projectId && itemIds.Contains(item.Id))
                .Select(item => new { item.Id, item.Status })
                .ToDictionaryAsync(item => item.Id, item => item.Status, cancellationToken);

            for (var index = 0; index < Lines.Count; index++)
            {
                if (Lines[index].ProjectItemId is not { } itemId)
                {
                    continue;
                }

                if (!items.TryGetValue(itemId, out var status))
                {
                    errors.Add($"lines[{index}].projectItemId", "This project item was not found on this project.");
                }
                else if (status == ProjectItemStatus.Inactive && !itemsAlreadyNamed.Contains(itemId))
                {
                    errors.Add($"lines[{index}].projectItemId", "This project item is inactive. New work can't be linked to it.");
                }
            }
        }

        if (ReportRequestId is { } requestId
            && !await db.ReportRequests.AsNoTracking().AnyAsync(candidate => candidate.Id == requestId && candidate.ProjectId == projectId, cancellationToken))
        {
            errors.Add("reportRequestId", "This report request was not found on this project.");
        }
    }

    /// <summary>Sets the report's fields and replaces its lines, materials and equipment, numbered from 1.</summary>
    public void ApplyTo(DailyReport report)
    {
        report.WorkersPresent = WorkersPresent;
        report.Weather = Weather;
        report.Issues = Issues;
        report.Notes = Notes;
        report.ReportRequestId = ReportRequestId;

        report.Lines.AddRange(Lines.Select((line, index) => new DailyReportLine
        {
            LineNo = index + 1,
            WorkType = line.WorkType,
            Description = line.Description,
            Unit = line.Unit,
            Quantity = line.Quantity,
            PerformedBy = line.PerformedBy,
            ProjectItemId = line.ProjectItemId,
            SubcontractorName = line.SubcontractorName,
            SubContractId = line.SubContractId,
            WorkersCount = line.WorkersCount,
        }));
        report.Materials.AddRange(Materials.Select(material => new DailyReportMaterial
        {
            MaterialName = material.MaterialName,
            Unit = material.Unit,
            Quantity = material.Quantity,
        }));
        report.Equipment.AddRange(Equipment.Select(equipment => new DailyReportEquipment
        {
            EquipmentId = equipment.EquipmentId,
            EquipmentName = equipment.EquipmentName,
            Hours = equipment.Hours,
        }));
    }

    private static List<TInput> Each<TRequest, TInput>(
        IReadOnlyList<TRequest?>? requests, int max, string field, ValidationErrors errors, Func<TRequest, string, ValidationErrors, TInput?> validate)
        where TRequest : class
        where TInput : class
    {
        if (requests is null)
        {
            return [];
        }

        if (requests.Count > max)
        {
            errors.Add(field, $"A report can have at most {max} {field} rows.");
            return [];
        }

        var result = new List<TInput>(requests.Count);
        for (var index = 0; index < requests.Count; index++)
        {
            var prefix = $"{field}[{index}]";
            if (requests[index] is not { } request)
            {
                errors.Add(prefix, "The row is empty.");
            }
            else if (validate(request, prefix, errors) is { } input)
            {
                result.Add(input);
            }
        }

        return result;
    }

    private static DailyReportLineInput? Line(DailyReportLineRequest line, string prefix, ValidationErrors errors)
    {
        var local = new ValidationErrors();

        if (!WorkTypes.TryParse(line.WorkType, out var workType))
        {
            local.Add("workType", $"workType must be one of: {string.Join(", ", WorkTypes.All)}.");
        }

        var description = Input.RequiredText(line.Description, DailyReportLineConfiguration.DescriptionMaxLength, local, "description", "The description");

        if (!MeasurementUnits.TryParse(line.Unit, out var unit))
        {
            local.Add("unit", $"unit must be one of: {string.Join(", ", MeasurementUnits.All)}.");
        }

        var quantity = NumberInput.RequiredQuantity(line.Quantity, local, "quantity", "The quantity");

        if (!Input.TryParseName<WorkPerformer>(line.PerformedBy, out var performedBy))
        {
            local.Add("performedBy", "performedBy must be Internal or Subcontractor.");
        }

        if (line.ProjectItemId is < 1)
        {
            local.Add("projectItemId", "projectItemId is not valid.");
        }

        var subcontractorName = Input.OptionalText(
            line.SubcontractorName, DailyReportLineConfiguration.SubcontractorNameMaxLength, local, "subcontractorName", "The subcontractor's name");

        if (line.SubContractId is < 1)
        {
            local.Add("subContractId", "subContractId is not valid.");
        }

        if (line.WorkersCount is < 0 or > DailyReportLineConfiguration.MaxWorkersCount)
        {
            local.Add("workersCount", $"workersCount must be between 0 and {DailyReportLineConfiguration.MaxWorkersCount}.");
        }

        if (!local.Has("performedBy"))
        {
            if (performedBy == WorkPerformer.Subcontractor && subcontractorName is null && line.SubContractId is null)
            {
                local.Add("subcontractorName", "A subcontractor line needs the subcontractor's name or a contract id.");
            }
            else if (performedBy == WorkPerformer.Internal && (subcontractorName is not null || line.SubContractId is not null))
            {
                local.Add("performedBy", "Only a subcontractor line names a subcontractor or a contract.");
            }
        }

        Copy(local, prefix, errors);
        return local.HasErrors
            ? null
            : new DailyReportLineInput(
                workType, description!, unit, quantity!.Value, performedBy, line.ProjectItemId, subcontractorName, line.SubContractId, line.WorkersCount);
    }

    private static DailyReportMaterialInput? Material(DailyReportMaterialRequest material, string prefix, ValidationErrors errors)
    {
        var local = new ValidationErrors();
        var name = Input.RequiredText(material.MaterialName, DailyReportMaterialConfiguration.NameMaxLength, local, "materialName", "The material");
        if (!MeasurementUnits.TryParse(material.Unit, out var unit))
        {
            local.Add("unit", $"unit must be one of: {string.Join(", ", MeasurementUnits.All)}.");
        }

        var quantity = NumberInput.RequiredQuantity(material.Quantity, local, "quantity", "The quantity");

        Copy(local, prefix, errors);
        return local.HasErrors ? null : new DailyReportMaterialInput(name!, unit, quantity!.Value);
    }

    private static DailyReportEquipmentInput? EquipmentRow(DailyReportEquipmentRequest equipment, string prefix, ValidationErrors errors)
    {
        var local = new ValidationErrors();
        if (equipment.EquipmentId is < 1)
        {
            local.Add("equipmentId", "equipmentId is not valid.");
        }

        var name = Input.RequiredText(equipment.EquipmentName, DailyReportEquipmentConfiguration.NameMaxLength, local, "equipmentName", "The equipment");

        if (equipment.Hours is { } hours
            && (hours <= 0 || hours > DailyReportEquipmentConfiguration.MaxHours || decimal.Round(hours, 1) != hours))
        {
            local.Add("hours", $"hours must be more than 0 and at most {DailyReportEquipmentConfiguration.MaxHours:0}, with at most one decimal.");
        }

        Copy(local, prefix, errors);
        return local.HasErrors ? null : new DailyReportEquipmentInput(equipment.EquipmentId, name!, equipment.Hours);
    }

    private static void Copy(ValidationErrors local, string prefix, ValidationErrors errors)
    {
        foreach (var (name, messages) in local.ToDictionary())
        {
            foreach (var message in messages)
            {
                errors.Add($"{prefix}.{name}", message);
            }
        }
    }
}
