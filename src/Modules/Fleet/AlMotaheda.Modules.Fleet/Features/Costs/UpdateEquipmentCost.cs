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
/// <c>PUT /api/projects/{pid}/equipment-costs/{id}</c>: the recorder fixes a cost and resubmits it. Only while it is
/// <c>Recorded</c> or <c>Returned</c>; it goes back to <c>Recorded</c> and the review note is cleared. An approved
/// cost is frozen. The work item may be changed or removed; a newly chosen one must be active (409
/// <c>item_inactive</c>).
/// </summary>
internal static class UpdateEquipmentCost
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
        string? RowVersion,
        int? ProjectItemId = null) : IEquipmentCostFieldsRequest;

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPut("/projects/{projectId:int}/equipment-costs/{costId:int}", HandleAsync)
            .WithName("UpdateEquipmentCost")
            .WithSummary("Fix and resubmit a recorded or returned equipment cost (the recorder only; rowVersion)");

    internal static async Task<Results<Ok<EquipmentCostView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId,
        int costId,
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
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
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

        var cost = await db.EquipmentCosts
            .SingleOrDefaultAsync(candidate => candidate.Id == costId && candidate.ProjectId == projectId, cancellationToken);
        if (cost is null)
        {
            return FleetProblems.CostNotFound();
        }

        switch (EquipmentCostRules.Resubmit(cost.Status, cost.CreatedBy, caller.UserId))
        {
            case CostDecision.NotRecorder:
                log.ActionRefused(caller.UserId, nameof(UpdateEquipmentCost), "not the recorder");
                return FleetProblems.NotTheRecorder();
            case CostDecision.WrongStatus:
                return FleetProblems.WrongStatus("Only a recorded or returned cost can be changed. An approved cost is frozen.");
        }

        var conflict = await EquipmentCostReferences.CheckAsync(
            db, suppliers, projectItems, projectId, fields!, currentItemId: cost.ProjectItemId, errors, cancellationToken);
        if (errors.HasErrors)
        {
            return FleetProblems.Validation(errors);
        }

        if (EquipmentCostReferences.ProblemOf(conflict) is { } problem)
        {
            return problem;
        }

        var oldStatus = cost.Status;
        fields!.ApplyTo(cost);
        cost.Resubmit(caller.UserId, clock.UtcNow);
        db.Entry(cost).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }
        catch (DbUpdateException exception) when (cost.ProjectItemId is not null && SqlViolations.IsReferenceOrCheck(exception))
        {
            // The work item was deleted by someone else after our check: a lost race is a 409, never a 500.
            return FleetProblems.ItemRemoved();
        }

        log.CostResubmitted(caller.UserId, projectId, cost.Id, oldStatus.ToString());

        var view = await EquipmentCostView.LoadAsync(db, users, suppliers, projectId, cost.Id, cancellationToken);
        return view is null ? FleetProblems.CostNotFound() : TypedResults.Ok(view);
    }
}
