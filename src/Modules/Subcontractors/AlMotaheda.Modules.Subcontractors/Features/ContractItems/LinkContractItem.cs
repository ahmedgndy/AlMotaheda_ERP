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
/// <c>PUT /api/projects/{pid}/subcontracts/{cid}/items/{itemId}/project-item</c>: link a contract item to a project
/// item, move it to another item, or remove the link (<c>projectItemId: null</c>).
/// </summary>
/// <remarks>
/// Every approved measurement of the contract item then counts on that project item. Only the link changes: no
/// amount is copied and no row is created, so the same work is never counted twice. Allowed on a completed
/// contract too, because it changes no money.
/// </remarks>
internal static class LinkContractItem
{
    internal sealed record Request(int? ProjectItemId, string? RowVersion);

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPut("/projects/{projectId:int}/subcontracts/{contractId:int}/items/{itemId:int}/project-item", HandleAsync)
            .RequirePermission(SubPermissions.Module, PermissionAction.Edit)
            .WithName("LinkContractItemToProjectItem")
            .WithSummary("Link a contract item to a project item, move it, or remove the link");

    internal static async Task<Results<Ok<ContractItemView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId,
        int contractId,
        int itemId,
        Request request,
        HttpContext httpContext,
        SubcontractorsDbContext db,
        IProjectAccess access,
        IProjectItems projectItems,
        ContractDetails details,
        CompanyClock clock,
        SubcontractorsLog log,
        CancellationToken cancellationToken)
    {
        if (SubCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        if (request.ProjectItemId is < 1)
        {
            errors.Add("projectItemId", "projectItemId is not valid.");
        }

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

        var item = await db.ContractItems.Include(candidate => candidate.Contract).SingleOrDefaultAsync(
            candidate => candidate.Id == itemId && candidate.ContractId == contractId && candidate.Contract.ProjectId == projectId,
            cancellationToken);
        if (item is null)
        {
            return SubcontractorsProblems.ContractItemNotFound();
        }

        if (request.ProjectItemId is { } projectItemId && projectItemId != item.ProjectItemId)
        {
            switch (await ProjectItemLinks.CheckAsync(projectItems, projectId, projectItemId, cancellationToken, item.Contract.ProjectItemId))
            {
                case ProjectItemCheck.NotFound:
                    errors.Add("projectItemId", "The project item was not found on this project.");
                    return SubcontractorsProblems.Validation(errors);
                case ProjectItemCheck.Inactive:
                    return SubcontractorsProblems.InactiveItem();
                case ProjectItemCheck.OutsideScope:
                    return SubcontractorsProblems.OutsideContractScope();
            }
        }

        var previousItemId = item.ProjectItemId;
        item.ProjectItemId = request.ProjectItemId;
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
        catch (DbUpdateException exception) when (request.ProjectItemId is not null && SqlViolations.IsReferenceOrCheck(exception))
        {
            // The project item was deleted by someone else after our check: a lost race is a 409, never a 500.
            return SubcontractorsProblems.ItemRemoved();
        }

        log.ContractItemLinked(caller.UserId, projectId, item.Id, item.ProjectItemId, previousItemId);

        var view = await details.ItemAsync(projectId, contractId, item.Id, caller.SeesMoney, cancellationToken);
        return view is null ? SubcontractorsProblems.ContractItemNotFound() : TypedResults.Ok(view);
    }
}
