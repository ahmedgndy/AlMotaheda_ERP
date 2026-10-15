using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Features.Catalog;

/// <summary>
/// <c>PUT /api/projects/{pid}/catalog-items/{id}</c>: change an item's names, category, unit, estimated quantity,
/// usual supplier and status. Never its price: a price changes only through a request an Executive approves.
/// </summary>
internal static class UpdateCatalogItem
{
    internal sealed record Request(
        string? NameAr,
        string? NameEn,
        string? Category,
        string? Unit,
        decimal? EstimatedQuantity,
        int? SupplierId,
        string? Status,
        string? RowVersion) : ICatalogItemRequest;

    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapPut("/{projectId:int}/catalog-items/{itemId:int}", HandleAsync)
            .RequirePermission(CatalogPermissions.Module, PermissionAction.Create)
            .WithName("UpdateCatalogItem")
            .WithSummary("Edit a catalog item's names, category, unit, estimated quantity, supplier and status (not its price)");

    internal static async Task<Results<Ok<GetCatalogItem.Response>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId,
        int itemId,
        Request request,
        HttpContext httpContext,
        ProjectsDbContext db,
        IProjectAccess access,
        IUserDirectory users,
        CompanyClock clock,
        ProjectsLog log,
        CancellationToken cancellationToken)
    {
        if (ProjectCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var fields = CatalogItemFields.Validate(request, errors);
        if (!Input.TryParseName<CatalogItemStatus>(request.Status, out var status))
        {
            errors.Add("status", "status must be Active or Inactive.");
        }

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

        if (project.WritesBlocked)
        {
            return ProjectProblems.CompletedProject();
        }

        var item = await db.CatalogItems
            .SingleOrDefaultAsync(candidate => candidate.Id == itemId && candidate.ProjectId == projectId, cancellationToken);
        if (item is null)
        {
            return PlanningProblems.CatalogItemNotFound();
        }

        await fields!.CheckSupplierAsync(db, item.SupplierId, errors, cancellationToken);
        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        if (await fields.NameTakenAsync(db, projectId, item.Id, cancellationToken))
        {
            return PlanningProblems.CatalogNameTaken();
        }

        var oldStatus = item.Status;
        fields.ApplyTo(item);
        item.Status = status;
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
        catch (DbUpdateException exception) when (CatalogItemFields.IsNameRace(exception))
        {
            return PlanningProblems.CatalogNameTaken();
        }

        log.CatalogItemUpdated(caller.UserId, projectId, item.Id, oldStatus.ToString(), item.Status.ToString());

        var view = await GetCatalogItem.LoadAsync(db, users, projectId, item.Id, clock.Today,
            httpContext.User.HasPermission(CatalogPermissions.Module, PermissionAction.SeeMoney), cancellationToken);
        return view is null ? PlanningProblems.CatalogItemNotFound() : TypedResults.Ok(view);
    }
}
