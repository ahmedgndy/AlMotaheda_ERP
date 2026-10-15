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
/// <c>POST /api/projects/{pid}/catalog-items</c>: add an item to the project's price catalog with its first
/// price. The first price is approved at once, as in the prototype: later changes go through a request that an
/// Executive decides.
/// </summary>
internal static class CreateCatalogItem
{
    /// <param name="EffectiveFrom">The first day the price applies (<c>yyyy-MM-dd</c>); today when empty.</param>
    internal sealed record Request(
        string? NameAr,
        string? NameEn,
        string? Category,
        string? Unit,
        decimal? Price,
        string? EffectiveFrom,
        decimal? EstimatedQuantity,
        int? SupplierId) : ICatalogItemRequest;

    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapPost("/{projectId:int}/catalog-items", HandleAsync)
            .RequirePermission(CatalogPermissions.Module, PermissionAction.Create)
            .WithName("CreateCatalogItem")
            .WithSummary("Add an item to a project's price catalog with its first price (approved at once)");

    internal static async Task<Results<Created<GetCatalogItem.Response>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId,
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
        var price = NumberInput.RequiredMoney(request.Price, errors, "price", "The price");
        var effectiveFrom = Input.OptionalDate(request.EffectiveFrom, errors, "effectiveFrom", "The effective date") ?? clock.Today;
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

        await fields!.CheckSupplierAsync(db, currentSupplierId: null, errors, cancellationToken);
        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        if (await fields.NameTakenAsync(db, projectId, exceptItemId: null, cancellationToken))
        {
            return PlanningProblems.CatalogNameTaken();
        }

        var now = clock.UtcNow;
        var item = new CatalogItem
        {
            ProjectId = projectId,
            NameAr = fields.NameAr,
            NameEn = fields.NameEn,
            Unit = fields.Unit,
            Status = CatalogItemStatus.Active,
            CreatedAt = now,
            CreatedBy = caller.UserId,
        };
        fields.ApplyTo(item);

        // The first price: requested and decided by the person who adds the item, with no reason, so it is never
        // listed as a price change.
        var firstPrice = new ItemPrice
        {
            Price = price!.Value,
            PreviousPrice = null,
            EffectiveFrom = effectiveFrom,
            Status = ItemPriceStatus.Approved,
            RequestedAt = now,
            RequestedBy = caller.UserId,
            DecidedAt = now,
            DecidedBy = caller.UserId,
        };
        item.Prices.Add(firstPrice);
        db.CatalogItems.Add(item);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (CatalogItemFields.IsNameRace(exception))
        {
            return PlanningProblems.CatalogNameTaken();
        }

        log.CatalogItemCreated(caller.UserId, projectId, item.Id, firstPrice.Id);

        var view = await GetCatalogItem.LoadAsync(db, users, projectId, item.Id, clock.Today,
            httpContext.User.HasPermission(CatalogPermissions.Module, PermissionAction.SeeMoney), cancellationToken);
        return TypedResults.Created($"/api/projects/{projectId}/catalog-items/{item.Id}", view!);
    }
}
