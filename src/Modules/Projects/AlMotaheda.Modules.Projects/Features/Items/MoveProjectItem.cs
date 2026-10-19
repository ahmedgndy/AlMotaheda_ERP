using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
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
/// <c>PUT /api/projects/{pid}/items/{itemId}/parent</c>: make an item a sub-item of a main item, move it to another
/// main item, or (<c>parentItemId: null</c>) make it a main item again. The way to organise a project's existing
/// flat items into main items and sub-items.
/// </summary>
/// <remarks>
/// <para>Only the link to the parent changes: the item's records stay linked to it, so no amount moves between
/// records and the project totals don't change. Only the roll-ups of the two main items change.</para>
/// <para>Rules: the parent is an active main item of the same project and not the item itself; an item that has
/// sub-items can't become a sub-item (two levels only); names stay unique among the new siblings. Under the project
/// lock, like every hierarchy change. A separate endpoint, so the item edit form (<c>PUT …/items/{id}</c>), which
/// doesn't send a parent, can never move an item by accident.</para>
/// </remarks>
internal static class MoveProjectItem
{
    internal sealed record Request(int? ParentItemId, string? RowVersion);

    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapPut("/{projectId:int}/items/{itemId:int}/parent", HandleAsync)
            .RequirePermission(ProjectPermissions.Module, PermissionAction.Edit)
            .WithName("MoveProjectItem")
            .WithSummary("Set the main item a project item belongs to (null makes it a main item; an Executive or the project's manager)");

    internal static async Task<Results<Ok<ItemRow>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId,
        int itemId,
        Request request,
        HttpContext httpContext,
        ProjectsDbContext db,
        IProjectAccess access,
        ItemsLedgerSource source,
        CompanyClock clock,
        ProjectsLog log,
        CancellationToken cancellationToken)
    {
        if (ProjectCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (request.ParentItemId is < 1)
        {
            errors.Add("parentItemId", "parentItemId is not valid.");
        }
        else if (request.ParentItemId == itemId)
        {
            errors.Add("parentItemId", "An item can't be its own main item.");
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
            log.ActionRefused(caller.UserId, nameof(MoveProjectItem), "not an Executive or the project's manager, or project completed");
            return refusal;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await ProjectLock.TryAcquireAsync(db, projectId, cancellationToken))
        {
            return ProjectProblems.TryAgainLater();
        }

        var item = await db.ProjectItems.SingleOrDefaultAsync(candidate => candidate.Id == itemId && candidate.ProjectId == projectId, cancellationToken);
        if (item is null)
        {
            return CoreProblems.ItemNotFound();
        }

        var previousParent = item.ParentItemId;
        if (request.ParentItemId is { } parentItemId)
        {
            var parentProblem = await ItemRules.ParentProblemAsync(db, projectId, parentItemId, errors, cancellationToken);
            if (errors.HasErrors)
            {
                return ProjectProblems.Validation(errors);
            }

            if (parentProblem is not null)
            {
                return parentProblem;
            }

            if (await db.ProjectItems.AnyAsync(child => child.ParentItemId == item.Id, cancellationToken))
            {
                return CoreProblems.StillHasSubItems();
            }
        }

        if (request.ParentItemId != previousParent
            && await ItemFields.NameTakenAsync(db, projectId, request.ParentItemId, item.Id, item.NameAr, item.NameEn, cancellationToken))
        {
            return CoreProblems.ItemNameAlreadyUsed();
        }

        item.ParentItemId = request.ParentItemId;
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
        catch (DbUpdateException exception) when (ItemRules.IsNameRace(exception))
        {
            return CoreProblems.ItemNameAlreadyUsed();
        }

        await transaction.CommitAsync(cancellationToken);
        log.ItemMoved(caller.UserId, projectId, item.Id, item.ParentItemId, previousParent);

        var data = await source.LoadAsync(projectId, cancellationToken);
        return TypedResults.Ok(ItemRow.From(item, data));
    }
}
