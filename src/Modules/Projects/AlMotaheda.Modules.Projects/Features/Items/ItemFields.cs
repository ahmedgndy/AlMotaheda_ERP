using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Data.Configurations;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Features.Items;

/// <summary>The project item form, as sent on create and on edit.</summary>
internal interface IItemFieldsRequest
{
    string? NameAr { get; }
    string? NameEn { get; }
    string? Category { get; }
    string? Unit { get; }
    string? Description { get; }
    string? Notes { get; }
}

/// <summary>The project item form after validation. One set of rules for create and edit.</summary>
internal sealed record ItemFields(string NameAr, string NameEn, string Category, string Unit, string? Description, string? Notes)
{
    public static ItemFields? Validate(IItemFieldsRequest request, ValidationErrors errors)
    {
        var nameAr = Input.RequiredText(request.NameAr, ProjectItemConfiguration.NameMaxLength, errors, "nameAr", "The Arabic name");
        var nameEn = Input.RequiredText(request.NameEn, ProjectItemConfiguration.NameMaxLength, errors, "nameEn", "The English name");

        if (!WorkTypes.TryParse(request.Category, out var category))
        {
            errors.Add("category", $"category must be one of: {string.Join(", ", WorkTypes.All)}.");
        }

        if (!MeasurementUnits.TryParse(request.Unit, out var unit))
        {
            errors.Add("unit", $"unit must be one of: {string.Join(", ", MeasurementUnits.All)}.");
        }

        var description = Input.OptionalText(request.Description, ProjectItemConfiguration.TextMaxLength, errors, "description", "The description");
        var notes = Input.OptionalText(request.Notes, ProjectItemConfiguration.TextMaxLength, errors, "notes", "Notes");

        return errors.HasErrors ? null : new ItemFields(nameAr!, nameEn!, category, unit, description, notes);
    }

    public void ApplyTo(ProjectItem item)
    {
        item.NameAr = NameAr;
        item.NameEn = NameEn;
        item.Category = Category;
        item.Unit = Unit;
        item.Description = Description;
        item.Notes = Notes;
    }

    /// <summary>True when another item of the project already has one of the names.</summary>
    public Task<bool> NameTakenAsync(ProjectsDbContext db, int projectId, int? exceptItemId, CancellationToken cancellationToken) =>
        db.ProjectItems.AnyAsync(
            other => other.ProjectId == projectId && other.Id != exceptItemId && (other.NameAr == NameAr || other.NameEn == NameEn),
            cancellationToken);
}

/// <summary>The rules shared by every change to a project's items.</summary>
internal static class ItemRules
{
    /// <summary>
    /// Items are the structure of a project, so only an Executive or the project's manager changes them, with
    /// projects Edit (the permission is checked by the endpoint). A completed project is read-only except for an
    /// Executive. Returns the problem to send back, or <c>null</c> to go ahead.
    /// </summary>
    public static ProblemHttpResult? ManageProblem(ProjectAccessInfo project)
    {
        if (!project.CallerIsExecutive && !project.CallerIsManager)
        {
            return CoreProblems.ItemsManagedByManager();
        }

        return project.WritesBlocked ? ProjectProblems.CompletedProject() : null;
    }

    /// <summary>True when the save failed on one of the item name indexes.</summary>
    public static bool IsNameRace(DbUpdateException exception) =>
        SqlViolations.IsUnique(exception, ProjectItemConfiguration.NameArIndex)
        || SqlViolations.IsUnique(exception, ProjectItemConfiguration.NameEnIndex);
}
