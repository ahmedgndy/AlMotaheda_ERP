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

/// <summary>The body of deactivate and reactivate: the row version of the last read.</summary>
internal sealed record ItemStatusRequest(string? RowVersion);

/// <summary>
/// <c>POST /api/projects/{pid}/items/{itemId}/deactivate</c>: the item keeps its records and stays in the totals,
/// but no new record can be linked to it. The way to retire an item that has history.
/// </summary>
internal static class DeactivateProjectItem
{
    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapPost("/{projectId:int}/items/{itemId:int}/deactivate", HandleAsync)
            .RequirePermission(ProjectPermissions.Module, PermissionAction.Edit)
            .WithName("DeactivateProjectItem")
            .WithSummary("Deactivate a project item: its records stay, nothing new can be linked to it");

    internal static Task<Results<Ok<ItemRow>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId, int itemId, ItemStatusRequest request, HttpContext httpContext, ProjectsDbContext db, IProjectAccess access,
        ItemsLedgerSource source, CompanyClock clock, ProjectsLog log, CancellationToken cancellationToken) =>
        ProjectItemStatusChange.HandleAsync(ProjectItemStatus.Inactive, projectId, itemId, request, httpContext, db, access, source, clock, log, cancellationToken);
}

/// <summary><c>POST /api/projects/{pid}/items/{itemId}/reactivate</c>: the item can take new records again.</summary>
internal static class ReactivateProjectItem
{
    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapPost("/{projectId:int}/items/{itemId:int}/reactivate", HandleAsync)
            .RequirePermission(ProjectPermissions.Module, PermissionAction.Edit)
            .WithName("ReactivateProjectItem")
            .WithSummary("Reactivate a project item");

    internal static Task<Results<Ok<ItemRow>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId, int itemId, ItemStatusRequest request, HttpContext httpContext, ProjectsDbContext db, IProjectAccess access,
        ItemsLedgerSource source, CompanyClock clock, ProjectsLog log, CancellationToken cancellationToken) =>
        ProjectItemStatusChange.HandleAsync(ProjectItemStatus.Active, projectId, itemId, request, httpContext, db, access, source, clock, log, cancellationToken);
}

/// <summary>The one flow behind deactivate and reactivate.</summary>
internal static class ProjectItemStatusChange
{
    public static async Task<Results<Ok<ItemRow>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        ProjectItemStatus target,
        int projectId,
        int itemId,
        ItemStatusRequest request,
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
            log.ActionRefused(caller.UserId, $"SetItem{target}", "not an Executive or the project's manager, or project completed");
            return refusal;
        }

        var item = await db.ProjectItems.SingleOrDefaultAsync(candidate => candidate.Id == itemId && candidate.ProjectId == projectId, cancellationToken);
        if (item is null)
        {
            return CoreProblems.ItemNotFound();
        }

        if (item.Status == target)
        {
            return WorkspaceProblems.WrongStatus($"This item is already {target}.");
        }

        var now = clock.UtcNow;
        item.Status = target;
        item.DeactivatedAt = target == ProjectItemStatus.Inactive ? now : null;
        item.DeactivatedBy = target == ProjectItemStatus.Inactive ? caller.UserId : null;
        item.UpdatedAt = now;
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

        log.ItemStatusChanged(caller.UserId, projectId, item.Id, target.ToString());

        var data = await source.LoadAsync(projectId, cancellationToken);
        return TypedResults.Ok(ItemRow.From(item, data.Ledger.Of(item.Id)));
    }
}
