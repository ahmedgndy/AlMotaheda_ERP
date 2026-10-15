using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Subcontractors.Common;
using AlMotaheda.Modules.Subcontractors.Data;
using AlMotaheda.Modules.Subcontractors.Features.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Subcontractors.Features.ContractItems;

/// <summary>
/// <c>PUT /api/projects/{pid}/subcontracts/{cid}/items/{itemId}</c>: edit a contract item. The quantity can't go
/// below what is measured and not rejected, and the unit can't change once work was measured in it. A new unit
/// price only affects measurements recorded after it: each measurement keeps the price it was recorded with.
/// </summary>
/// <remarks>The link to a project item has its own endpoint; this one leaves it alone.</remarks>
internal static class UpdateContractItem
{
    internal sealed record Request(
        string? Name,
        string? WorkType,
        string? Unit,
        decimal? Quantity,
        decimal? UnitPrice,
        string? Description,
        string? Notes,
        string? RowVersion) : IContractItemFieldsRequest;

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPut("/projects/{projectId:int}/subcontracts/{contractId:int}/items/{itemId:int}", HandleAsync)
            .WithName("UpdateContractItem")
            .WithSummary("Edit a contract item (subcontractors Create or Edit); the quantity can't go below what is measured");

    internal static async Task<Results<Ok<ContractItemView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId,
        int contractId,
        int itemId,
        Request request,
        HttpContext httpContext,
        SubcontractorsDbContext db,
        IProjectAccess access,
        ContractDetails details,
        CompanyClock clock,
        SubcontractorsLog log,
        CancellationToken cancellationToken)
    {
        if (SubCaller.From(httpContext.User) is not { } caller || !ItemPermissions.CanWrite(caller))
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var fields = ContractItemFields.Validate(request, errors);
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (errors.HasErrors)
        {
            return SubcontractorsProblems.Validation(errors);
        }

        var project = await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken);
        if (project is null)
        {
            return SubcontractorsProblems.ProjectNotFound();
        }

        if (project.WritesBlocked)
        {
            return SubcontractorsProblems.CompletedProject();
        }

        // Under the lock, so a measurement can't be recorded between the quantity check and the save.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        if (!await AppLock.TryAcquireAsync(db, AppLock.Project(projectId), cancellationToken))
        {
            return SubcontractorsProblems.TryAgainLater();
        }

        var item = await db.ContractItems
            .Include(candidate => candidate.Contract)
            .SingleOrDefaultAsync(
                candidate => candidate.Id == itemId && candidate.ContractId == contractId && candidate.Contract.ProjectId == projectId,
                cancellationToken);
        if (item is null)
        {
            return SubcontractorsProblems.ContractItemNotFound();
        }

        if (item.Contract.IsCompleted)
        {
            return SubcontractorsProblems.CompletedContract();
        }

        var measured = await db.ProgressMeasurements
            .Where(measurement => measurement.ContractItemId == itemId && measurement.Status != WorkStatus.Rejected)
            .SumAsync(measurement => measurement.Quantity, cancellationToken);

        if (fields!.Quantity < measured)
        {
            errors.Add("quantity", $"The quantity can't be less than what is already measured ({measured:0.###}).");
        }

        if (fields.Unit != item.Unit && await db.ProgressMeasurements.AnyAsync(measurement => measurement.ContractItemId == itemId, cancellationToken))
        {
            errors.Add("unit", "Work was already measured in this unit, so it can't change.");
        }

        if (errors.HasErrors)
        {
            return SubcontractorsProblems.Validation(errors);
        }

        fields.ApplyTo(item);
        item.UpdatedAt = clock.UtcNow;
        item.UpdatedBy = caller.UserId;
        db.Entry(item).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        await transaction.CommitAsync(cancellationToken);
        log.ContractItemUpdated(caller.UserId, projectId, contractId, item.Id);

        var view = await details.ItemAsync(projectId, contractId, item.Id, caller.SeesMoney, cancellationToken);
        return view is null ? SubcontractorsProblems.ContractItemNotFound() : TypedResults.Ok(view);
    }
}

/// <summary>
/// <c>DELETE /api/projects/{pid}/subcontracts/{cid}/items/{itemId}</c>: remove an item added by mistake. Only while
/// no measurement (of any status) points to it; measured work is history and stays.
/// </summary>
internal static class DeleteContractItem
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapDelete("/projects/{projectId:int}/subcontracts/{contractId:int}/items/{itemId:int}", HandleAsync)
            .RequirePermission(SubPermissions.Module, PermissionAction.Edit)
            .WithName("DeleteContractItem")
            .WithSummary("Delete a contract item that has no measurement");

    internal static async Task<Results<NoContent, ProblemHttpResult>> HandleAsync(
        int projectId,
        int contractId,
        int itemId,
        HttpContext httpContext,
        SubcontractorsDbContext db,
        IProjectAccess access,
        SubcontractorsLog log,
        CancellationToken cancellationToken)
    {
        if (SubCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var project = await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken);
        if (project is null)
        {
            return SubcontractorsProblems.ProjectNotFound();
        }

        if (project.WritesBlocked)
        {
            return SubcontractorsProblems.CompletedProject();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        if (!await AppLock.TryAcquireAsync(db, AppLock.Project(projectId), cancellationToken))
        {
            return SubcontractorsProblems.TryAgainLater();
        }

        var item = await db.ContractItems
            .Include(candidate => candidate.Contract)
            .SingleOrDefaultAsync(
                candidate => candidate.Id == itemId && candidate.ContractId == contractId && candidate.Contract.ProjectId == projectId,
                cancellationToken);
        if (item is null)
        {
            return SubcontractorsProblems.ContractItemNotFound();
        }

        if (item.Contract.IsCompleted)
        {
            return SubcontractorsProblems.CompletedContract();
        }

        if (await db.ProgressMeasurements.AnyAsync(measurement => measurement.ContractItemId == itemId, cancellationToken))
        {
            return SubcontractorsProblems.ItemStillHasMeasurements();
        }

        db.ContractItems.Remove(item);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (SqlViolations.IsReferenceOrCheck(exception))
        {
            // The foreign key is the last guard: a measurement saved in between still blocks the delete.
            return SubcontractorsProblems.ItemStillHasMeasurements();
        }

        await transaction.CommitAsync(cancellationToken);
        log.ContractItemDeleted(caller.UserId, projectId, contractId, itemId);

        return TypedResults.NoContent();
    }
}
