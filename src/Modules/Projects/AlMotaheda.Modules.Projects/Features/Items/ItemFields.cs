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

    /// <summary>True when another item with the same parent (or another main item) already has one of the names.</summary>
    public Task<bool> NameTakenAsync(ProjectsDbContext db, int projectId, int? parentItemId, int? exceptItemId, CancellationToken cancellationToken) =>
        NameTakenAsync(db, projectId, parentItemId, exceptItemId, NameAr, NameEn, cancellationToken);

    public static Task<bool> NameTakenAsync(
        ProjectsDbContext db, int projectId, int? parentItemId, int? exceptItemId, string nameAr, string nameEn, CancellationToken cancellationToken) =>
        db.ProjectItems.AnyAsync(
            other => other.ProjectId == projectId && other.ParentItemId == parentItemId && other.Id != exceptItemId
                     && (other.NameAr == nameAr || other.NameEn == nameEn),
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

    /// <summary>
    /// Checks that <paramref name="parentItemId"/> can be the parent of a sub-item: an item of this project
    /// (otherwise a form error on <c>parentItemId</c>), a main item, and active. Run under the project lock, so the
    /// parent can't become a sub-item or be deactivated between this check and the save.
    /// </summary>
    /// <returns>The rule conflict to send back, or <c>null</c>; a parent that is not found is added to <paramref name="errors"/>.</returns>
    public static async Task<ProblemHttpResult?> ParentProblemAsync(
        ProjectsDbContext db, int projectId, int parentItemId, ValidationErrors errors, CancellationToken cancellationToken)
    {
        var parent = await db.ProjectItems.AsNoTracking()
            .Where(item => item.Id == parentItemId && item.ProjectId == projectId)
            .Select(item => new { item.ParentItemId, item.Status })
            .SingleOrDefaultAsync(cancellationToken);

        if (parent is null)
        {
            errors.Add("parentItemId", "The main item was not found on this project.");
            return null;
        }

        if (parent.ParentItemId is not null)
        {
            return CoreProblems.ParentIsSubItem();
        }

        return parent.Status == ProjectItemStatus.Active ? null : CoreProblems.ParentInactive();
    }

    /// <summary>True when the save failed on one of the item name indexes.</summary>
    public static bool IsNameRace(DbUpdateException exception) =>
        SqlViolations.IsUnique(exception, ProjectItemConfiguration.NameArIndex)
        || SqlViolations.IsUnique(exception, ProjectItemConfiguration.NameEnIndex);
}
