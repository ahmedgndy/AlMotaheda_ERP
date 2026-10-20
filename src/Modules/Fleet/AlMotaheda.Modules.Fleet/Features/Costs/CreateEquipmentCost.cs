using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Fleet.Common;
using AlMotaheda.Modules.Fleet.Data;
using AlMotaheda.Modules.Fleet.Security;
using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Fleet.Features.Costs;

/// <summary>
/// <c>POST /api/projects/{pid}/equipment-costs</c>: record a rental, fuel, maintenance, repair, transport or other
/// cost of a machine on this project. Saved as <c>Recorded</c>: a cost only once someone else approves it.
/// </summary>
/// <remarks>
/// Needs equipment Create or equipment Edit; the endpoint checks the two itself, because <c>RequirePermission</c>
/// asks for exactly one action. The server computes the amount, and the machine must have been on the project
/// (409 <c>never_on_project</c>). Equipment fuel, rental and maintenance are recorded only here, never as project
/// expenses, so they are counted once. An optional <c>projectItemId</c> puts the cost on a work item (main or
/// sub-item) of this project: 400 when it is not on the project, 409 <c>item_inactive</c> when it is inactive.
/// </remarks>
internal static class CreateEquipmentCost
{
    internal sealed record Request(
        int? EquipmentId,
        string? CostDate,
        string? Type,
        string? Basis,
        decimal? Quantity,
        decimal? Rate,
        decimal? Liters,
        decimal? PricePerLiter,
        decimal? Amount,
        string? Description,
        int? SupplierId,
        string? Channel,
        string? InvoiceNo,
        int? ProjectItemId = null) : IEquipmentCostFieldsRequest;

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/projects/{projectId:int}/equipment-costs", HandleAsync)
            .WithName("CreateEquipmentCost")
            .WithSummary("Record an equipment cost on a project, optionally on one of its work items (equipment Create or Edit; saved as Recorded, waiting for review). 409 never_on_project, item_inactive");

    internal static async Task<Results<Created<EquipmentCostView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId,
        Request request,
        HttpContext httpContext,
        FleetDbContext db,
        IProjectAccess access,
        IUserDirectory users,
        ISupplierDirectory suppliers,
        IProjectItems projectItems,
        CompanyClock clock,
        FleetLog log,
        CancellationToken cancellationToken)
    {
        if (FleetCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        if (!caller.CanRecordCosts)
        {
            return FleetProblems.RecordingNeedsCreateOrEdit();
        }

        var errors = new ValidationErrors();
        var fields = EquipmentCostFields.Validate(request, clock.Today, errors);
        if (errors.HasErrors)
        {
            return FleetProblems.Validation(errors);
        }

        var project = await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken);
        if (project is null)
        {
            return FleetProblems.ProjectNotFound();
        }

        if (project.WritesBlocked)
        {
            return FleetProblems.CompletedProject();
        }

        var conflict = await EquipmentCostReferences.CheckAsync(db, suppliers, projectItems, projectId, fields!, currentItemId: null, errors, cancellationToken);
        if (errors.HasErrors)
        {
            return FleetProblems.Validation(errors);
        }

        if (EquipmentCostReferences.ProblemOf(conflict) is { } problem)
        {
            return problem;
        }

        var cost = new EquipmentCost
        {
            ProjectId = projectId,
            Status = EquipmentCostStatus.Recorded,
            CreatedAt = clock.UtcNow,
            CreatedBy = caller.UserId,
        };
        fields!.ApplyTo(cost);

        db.EquipmentCosts.Add(cost);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (cost.ProjectItemId is not null && SqlViolations.IsReferenceOrCheck(exception))
        {
            // The work item was deleted by someone else after our check: a lost race is a 409, never a 500.
            return FleetProblems.ItemRemoved();
        }
        log.CostRecorded(caller.UserId, projectId, cost.Id, cost.EquipmentId, cost.Type.ToString(), cost.Channel.ToString());

        var view = await EquipmentCostView.LoadAsync(db, users, suppliers, projectId, cost.Id, cancellationToken);
        return TypedResults.Created($"/api/projects/{projectId}/equipment-costs/{cost.Id}", view!);
    }
}

/// <summary>A rule conflict found on the ids of the cost form (409), or none.</summary>
internal enum CostReferenceConflict
{
    None,

    /// <summary>The machine never had a stay on the project (409 <c>never_on_project</c>).</summary>
    NeverOnProject,

    /// <summary>The work item was newly chosen and is inactive (409 <c>item_inactive</c>).</summary>
    InactiveItem,
}

/// <summary>What the database says about the ids on the cost form.</summary>
internal static class EquipmentCostReferences
{
    /// <summary>
    /// Checks that the machine is in the register, that the supplier exists and is active, and that the work item
    /// is on this project, reporting each problem on its own field. The work item is asked from Projects through
    /// <see cref="IProjectItems"/>, which leaves out the items of other projects; a main item or a sub-item may be
    /// chosen. Returns the rule conflict, if any: not form errors but conflicts (409), so they are returned, not
    /// judged.
    /// </summary>
    /// <param name="currentItemId">
    /// The work item the cost has now (<c>null</c> for a new cost). Keeping it is allowed even if it was deactivated
    /// since; only a newly chosen item must be active.
    /// </param>
    public static async Task<CostReferenceConflict> CheckAsync(
        FleetDbContext db,
        ISupplierDirectory suppliers,
        IProjectItems projectItems,
        int projectId,
        EquipmentCostFields fields,
        int? currentItemId,
        ValidationErrors errors,
        CancellationToken cancellationToken)
    {
        if (!await db.Equipment.AnyAsync(equipment => equipment.Id == fields.EquipmentId, cancellationToken))
        {
            errors.Add("equipmentId", "The machine was not found in the register.");
        }

        if (fields.SupplierId is { } supplierId)
        {
            var found = await suppliers.FindAsync([supplierId], cancellationToken);
            SupplierChoice.Check(supplierId, found, errors, "supplierId");
        }

        var inactiveItem = false;
        if (fields.ProjectItemId is { } itemId)
        {
            var items = await projectItems.FindAsync(projectId, [itemId], cancellationToken);
            if (!items.TryGetValue(itemId, out var item))
            {
                errors.Add("projectItemId", "The work item was not found on this project.");
            }
            else
            {
                inactiveItem = !item.IsActive && itemId != currentItemId;
            }
        }

        if (!await EquipmentCostQueries.StaysOn(db, projectId, fields.EquipmentId).AnyAsync(cancellationToken))
        {
            return CostReferenceConflict.NeverOnProject;
        }

        return inactiveItem ? CostReferenceConflict.InactiveItem : CostReferenceConflict.None;
    }

    /// <summary>The answer to a conflict, or <c>null</c> when there is none.</summary>
    public static ProblemHttpResult? ProblemOf(CostReferenceConflict conflict) => conflict switch
    {
        CostReferenceConflict.NeverOnProject => FleetProblems.NotOnProject(),
        CostReferenceConflict.InactiveItem => FleetProblems.InactiveItem(),
        _ => null,
    };
}
