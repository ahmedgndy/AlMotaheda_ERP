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
/// expenses, so they are counted once.
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
            .WithSummary("Record an equipment cost on a project (equipment Create or Edit; saved as Recorded, waiting for review). 409 never_on_project");

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

        var wasOnProject = await EquipmentCostReferences.CheckAsync(db, suppliers, projectId, fields!, errors, cancellationToken);
        if (errors.HasErrors)
        {
            return FleetProblems.Validation(errors);
        }

        if (!wasOnProject)
        {
            return FleetProblems.NotOnProject();
        }

        if (await EquipmentCostDuplicates.ExistsAsync(db, fields!, exceptCostId: null, cancellationToken))
        {
            return FleetProblems.Duplicate();
        }

        if (request.ProjectItemId is { } itemId)
        {
            var found = await projectItems.FindAsync(projectId, [itemId], cancellationToken);
            if (!found.TryGetValue(itemId, out var item))
            {
                errors.Add("projectItemId", "The project item was not found on this project.");
                return FleetProblems.Validation(errors);
            }

            if (!item.IsActive)
            {
                return FleetProblems.InactiveItem();
            }
        }

        var now = clock.UtcNow;
        var cost = new EquipmentCost
        {
            ProjectId = projectId,
            Status = EquipmentCostStatus.Recorded,
            CreatedAt = now,
            CreatedBy = caller.UserId,
            ProjectItemId = request.ProjectItemId,
            ItemLinkedAt = request.ProjectItemId is null ? null : now,
            ItemLinkedBy = request.ProjectItemId is null ? null : caller.UserId,
        };
        fields!.ApplyTo(cost);

        db.EquipmentCosts.Add(cost);
        await db.SaveChangesAsync(cancellationToken);
        log.CostRecorded(caller.UserId, projectId, cost.Id, cost.EquipmentId, cost.Type.ToString(), cost.Channel.ToString());

        var view = await EquipmentCostView.LoadAsync(db, users, suppliers, projectId, cost.Id, cancellationToken);
        return TypedResults.Created($"/api/projects/{projectId}/equipment-costs/{cost.Id}", view!);
    }
}

/// <summary>What the database says about the ids on the cost form.</summary>
internal static class EquipmentCostReferences
{
    /// <summary>
    /// Checks that the machine is in the register and that the supplier exists and is active, reporting each
    /// problem on its own field. Returns whether the machine has, or had, a stay on the project: not a form error
    /// but a rule conflict (409), so it is returned, not judged.
    /// </summary>
    public static async Task<bool> CheckAsync(
        FleetDbContext db, ISupplierDirectory suppliers, int projectId, EquipmentCostFields fields, ValidationErrors errors, CancellationToken cancellationToken)
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

        return await EquipmentCostQueries.StaysOn(db, projectId, fields.EquipmentId).AnyAsync(cancellationToken);
    }
}
