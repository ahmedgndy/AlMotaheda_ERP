using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Data.Configurations;
using AlMotaheda.Modules.Projects.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Features.Items;

/// <summary>One planned component, as loaded.</summary>
internal sealed record ComponentPlan(
    int Id,
    ComponentKind Kind,
    int? CatalogItemId,
    string? CatalogItemNameAr,
    string? CatalogItemNameEn,
    string? CatalogUnit,
    decimal? PlannedQuantity,
    decimal? QuantityPerUnit,
    string? Notes);

/// <summary>One planned component, as the API returns it.</summary>
internal sealed record ComponentPlanView(
    int Id, string Kind, int? CatalogItemId, string? CatalogItemNameAr, string? CatalogItemNameEn, string? Unit,
    decimal? PlannedQuantity, decimal? QuantityPerUnit, string? Notes)
{
    public static ComponentPlanView From(ComponentPlan plan) => new(
        plan.Id, plan.Kind.ToString(), plan.CatalogItemId, plan.CatalogItemNameAr, plan.CatalogItemNameEn, plan.CatalogUnit,
        plan.PlannedQuantity, plan.QuantityPerUnit, plan.Notes);
}

/// <summary>One approved supplier-side record of the item, as the breakdown needs it.</summary>
internal sealed record ComponentSupplyRow(int? CatalogItemId, string? Unit, decimal? Quantity, decimal Cost, decimal Paid);

/// <summary>The cost, paid and record count of one source of the item.</summary>
internal sealed record SourceTotal(decimal Cost, decimal Paid, int RecordCount)
{
    public static SourceTotal Empty { get; } = new(0, 0, 0);

    public static SourceTotal Of(IEnumerable<(decimal Cost, decimal Paid)> lines)
    {
        decimal cost = 0, paid = 0;
        var count = 0;
        foreach (var (lineCost, linePaid) in lines)
        {
            cost += lineCost;
            paid += linePaid;
            count++;
        }

        return new SourceTotal(cost, paid, count);
    }

    public SourceTotal Add(SourceTotal other) => new(Cost + other.Cost, Paid + other.Paid, RecordCount + other.RecordCount);
}

/// <summary>One component of an item with what was really spent on it.</summary>
/// <param name="ActualQuantity">Materials only: what was bought, in the catalog item's unit.</param>
internal sealed record ComponentView(
    int Id,
    string Kind,
    int? CatalogItemId,
    string? CatalogItemNameAr,
    string? CatalogItemNameEn,
    string? Unit,
    decimal? PlannedQuantity,
    decimal? QuantityPerUnit,
    string? Notes,
    decimal? ActualQuantity,
    decimal Cost,
    decimal Paid,
    decimal Outstanding,
    int RecordCount);

/// <summary>
/// The item's own cost broken down by its components. <see cref="Other"/> is what matches no component, so
/// components + other = <see cref="Total"/> = the item's cost in the items table. Nothing here is added to the project
/// total again: it is the same records, sorted.
/// </summary>
internal sealed record ComponentBreakdownView(IReadOnlyList<ComponentView> Components, SourceTotal Other, SourceTotal Total);

/// <summary>The breakdown arithmetic (docs/cost-flow §2.4), with no database access.</summary>
internal static class ComponentBreakdown
{
    /// <param name="supplies">The item's approved supplier-side records.</param>
    /// <param name="labor">Internal labor (Workforce) plus allocated staff salaries (HR) on the item.</param>
    public static ComponentBreakdownView Compute(
        IReadOnlyList<ComponentPlan> plans,
        IReadOnlyList<ComponentSupplyRow> supplies,
        SourceTotal labor,
        SourceTotal equipment,
        SourceTotal subcontract)
    {
        var views = new List<ComponentView>();
        var matched = SourceTotal.Empty;

        foreach (var plan in plans.OrderBy(plan => plan.Kind).ThenBy(plan => plan.CatalogItemNameEn).ThenBy(plan => plan.Id))
        {
            SourceTotal total;
            decimal? quantity = null;
            if (plan.Kind == ComponentKind.Material)
            {
                var rows = supplies.Where(row => row.CatalogItemId == plan.CatalogItemId).ToList();
                total = SourceTotal.Of(rows.Select(row => (row.Cost, row.Paid)));
                quantity = rows.Where(row => row.Unit == plan.CatalogUnit).Sum(row => row.Quantity ?? 0);
            }
            else
            {
                total = plan.Kind switch
                {
                    ComponentKind.Labor => labor,
                    ComponentKind.Equipment => equipment,
                    _ => subcontract,
                };
            }

            matched = matched.Add(total);
            views.Add(new ComponentView(
                plan.Id, plan.Kind.ToString(), plan.CatalogItemId, plan.CatalogItemNameAr, plan.CatalogItemNameEn, plan.CatalogUnit,
                plan.PlannedQuantity, plan.QuantityPerUnit, plan.Notes, quantity, total.Cost, total.Paid, total.Cost - total.Paid, total.RecordCount));
        }

        var all = SourceTotal.Of(supplies.Select(row => (row.Cost, row.Paid))).Add(labor).Add(equipment).Add(subcontract);
        var other = new SourceTotal(all.Cost - matched.Cost, all.Paid - matched.Paid, all.RecordCount - matched.RecordCount);
        return new ComponentBreakdownView(views, other, all);
    }
}

/// <summary>
/// <c>PUT /api/projects/{pid}/items/{itemId}/components</c>: replace an item's planned components (an Executive or the
/// project's manager, like every item change). A plan, never a cost: no amount is entered here.
/// </summary>
internal static class SetItemComponents
{
    internal sealed record ComponentRequest(string? Kind, int? CatalogItemId, decimal? PlannedQuantity, decimal? QuantityPerUnit, string? Notes);

    internal sealed record Request(IReadOnlyList<ComponentRequest>? Components);

    private const int MaxComponents = 50;

    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapPut("/{projectId:int}/items/{itemId:int}/components", HandleAsync)
            .RequirePermission(ProjectPermissions.Module, PermissionAction.Edit)
            .WithName("SetItemComponents")
            .WithSummary("Replace a project item's planned components: materials (catalog items), labor, equipment, subcontract work");

    internal static async Task<Results<Ok<IReadOnlyList<ComponentPlanView>>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId,
        int itemId,
        Request request,
        HttpContext httpContext,
        ProjectsDbContext db,
        IProjectAccess access,
        CompanyClock clock,
        ProjectsLog log,
        CancellationToken cancellationToken)
    {
        if (ProjectCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var requested = request.Components ?? [];
        if (requested.Count > MaxComponents)
        {
            errors.Add("components", $"An item can have at most {MaxComponents} components.");
        }

        var parsed = new List<(ComponentKind Kind, int? CatalogItemId, decimal? Planned, decimal? PerUnit, string? Notes)>();
        for (var index = 0; index < requested.Count; index++)
        {
            var line = requested[index];
            if (!Input.TryParseName<ComponentKind>(line.Kind, out var kind))
            {
                errors.Add($"components[{index}].kind", $"kind must be one of: {string.Join(", ", Enum.GetNames<ComponentKind>())}.");
                continue;
            }

            if (kind == ComponentKind.Material && line.CatalogItemId is not > 0)
            {
                errors.Add($"components[{index}].catalogItemId", "A material component names a catalog item of this project.");
            }

            if (kind != ComponentKind.Material && line.CatalogItemId is not null)
            {
                errors.Add($"components[{index}].catalogItemId", "Only a material component names a catalog item.");
            }

            if (line.PlannedQuantity is { } planned && (planned <= 0 || planned > NumberInput.MaxQuantity || decimal.Round(planned, 3) != planned))
            {
                errors.Add($"components[{index}].plannedQuantity", "The planned quantity must be more than zero, with at most three decimals.");
            }

            if (line.QuantityPerUnit is { } perUnit && (perUnit <= 0 || perUnit > NumberInput.MaxQuantity || decimal.Round(perUnit, 4) != perUnit))
            {
                errors.Add($"components[{index}].quantityPerUnit", "The quantity per unit must be more than zero, with at most four decimals.");
            }

            var notes = Input.OptionalText(line.Notes, ProjectItemComponentConfiguration.NotesMaxLength, errors, $"components[{index}].notes", "The notes");
            parsed.Add((kind, line.CatalogItemId, line.PlannedQuantity, line.QuantityPerUnit, notes));
        }

        if (parsed.Where(line => line.Kind == ComponentKind.Material).GroupBy(line => line.CatalogItemId).Any(group => group.Count() > 1)
            || parsed.Where(line => line.Kind != ComponentKind.Material).GroupBy(line => line.Kind).Any(group => group.Count() > 1))
        {
            errors.Add("components", "Each material, and labor, equipment and subcontract work, appears once.");
        }

        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        var project = await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken);
        if (project is null)
        {
            return ProjectProblems.ProjectNotFound();
        }

        if (ItemRules.ManageProblem(project) is { } refusal)
        {
            log.ActionRefused(caller.UserId, nameof(SetItemComponents), "not an Executive or the project's manager, or project completed");
            return refusal;
        }

        var item = await db.ProjectItems.SingleOrDefaultAsync(candidate => candidate.Id == itemId && candidate.ProjectId == projectId, cancellationToken);
        if (item is null)
        {
            return CoreProblems.ItemNotFound();
        }

        // Catalog items of another project are refused like unknown ones: never a link across projects.
        var catalogIds = parsed.Where(line => line.CatalogItemId is not null).Select(line => line.CatalogItemId!.Value).ToList();
        var known = await db.CatalogItems.AsNoTracking()
            .Where(candidate => candidate.ProjectId == projectId && catalogIds.Contains(candidate.Id))
            .Select(candidate => candidate.Id)
            .ToListAsync(cancellationToken);
        for (var index = 0; index < parsed.Count; index++)
        {
            if (parsed[index].CatalogItemId is { } catalogItemId && !known.Contains(catalogItemId))
            {
                errors.Add($"components[{index}].catalogItemId", "The catalog item was not found on this project.");
            }
        }

        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var current = await db.ProjectItemComponents.Where(component => component.ProjectItemId == itemId).ToListAsync(cancellationToken);
        db.ProjectItemComponents.RemoveRange(current);
        await db.SaveChangesAsync(cancellationToken);

        var now = clock.UtcNow;
        db.ProjectItemComponents.AddRange(parsed.Select(line => new ProjectItemComponent
        {
            ProjectItemId = itemId,
            Kind = line.Kind,
            CatalogItemId = line.CatalogItemId,
            PlannedQuantity = line.Planned,
            QuantityPerUnit = line.PerUnit,
            Notes = line.Notes,
            CreatedAt = now,
            CreatedBy = caller.UserId,
        }));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        log.ItemComponentsSet(caller.UserId, projectId, itemId, parsed.Count);
        IReadOnlyList<ComponentPlanView> plans = (await ItemComponentQueries.PlansAsync(db, itemId, cancellationToken)).Select(ComponentPlanView.From).ToList();
        return TypedResults.Ok(plans);
    }
}

internal static class ItemComponentQueries
{
    public static async Task<IReadOnlyList<ComponentPlan>> PlansAsync(ProjectsDbContext db, int itemId, CancellationToken cancellationToken) =>
        await db.ProjectItemComponents.AsNoTracking()
            .Where(component => component.ProjectItemId == itemId)
            .OrderBy(component => component.Id)
            .Select(component => new ComponentPlan(
                component.Id,
                component.Kind,
                component.CatalogItemId,
                component.CatalogItem != null ? component.CatalogItem.NameAr : null,
                component.CatalogItem != null ? component.CatalogItem.NameEn : null,
                component.CatalogItem != null ? component.CatalogItem.Unit : null,
                component.PlannedQuantity,
                component.QuantityPerUnit,
                component.Notes))
            .ToListAsync(cancellationToken);
}
