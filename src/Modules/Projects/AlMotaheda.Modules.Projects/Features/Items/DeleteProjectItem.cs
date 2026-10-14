using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Features.Items;

/// <summary>
/// <c>DELETE /api/projects/{pid}/items/{itemId}</c>: remove an item that was created by mistake. Only while no
/// record anywhere is linked to it: not a supplier record here, not labor (Workforce), not subcontract work
/// (Subcontractors), not a budget line. An item with history is deactivated instead.
/// </summary>
/// <remarks>
/// The three "has links" checks and the delete are not one atomic step. The foreign keys are the last guard: a
/// record linked in between makes the delete fail, and that failure is answered with the same 409.
/// </remarks>
internal static class DeleteProjectItem
{
    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapDelete("/{projectId:int}/items/{itemId:int}", HandleAsync)
            .RequirePermission(ProjectPermissions.Module, PermissionAction.Edit)
            .WithName("DeleteProjectItem")
            .WithSummary("Delete a project item that has no linked record anywhere (otherwise deactivate it)");

    internal static async Task<Results<NoContent, ProblemHttpResult>> HandleAsync(
        int projectId,
        int itemId,
        HttpContext httpContext,
        ProjectsDbContext db,
        IProjectAccess access,
        ILaborItemCosts labor,
        ISubcontractItemCosts subcontracts,
        ProjectsLog log,
        CancellationToken cancellationToken)
    {
        if (ProjectCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var project = await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken);
        if (project is null)
        {
            return ProjectProblems.ProjectNotFound();
        }

        if (ItemRules.ManageProblem(project) is { } refusal)
        {
            log.ActionRefused(caller.UserId, nameof(DeleteProjectItem), "not an Executive or the project's manager, or project completed");
            return refusal;
        }

        var item = await db.ProjectItems.SingleOrDefaultAsync(candidate => candidate.Id == itemId && candidate.ProjectId == projectId, cancellationToken);
        if (item is null)
        {
            return CoreProblems.ItemNotFound();
        }

        if (await ItemQueries.LinkedExpenses(db, itemId).AnyAsync(cancellationToken)
            || await labor.HasLinksAsync(projectId, itemId, cancellationToken)
            || await subcontracts.HasLinksAsync(projectId, itemId, cancellationToken))
        {
            return CoreProblems.ItemStillHasRecords();
        }

        db.ProjectItems.Remove(item);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (SqlViolations.IsReferenceOrCheck(exception))
        {
            return CoreProblems.ItemStillHasRecords();
        }

        log.ItemDeleted(caller.UserId, projectId, item.Id);
        return TypedResults.NoContent();
    }
}
