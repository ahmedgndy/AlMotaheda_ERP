using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Data.Configurations;
using AlMotaheda.Modules.Projects.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Features.Catalog;

/// <summary>
/// <c>POST /api/projects/{pid}/catalog-items/{id}/price-changes</c>: ask an Executive to change an item's price.
/// Until it is approved the current price stays; supplies already recorded keep the price they copied.
/// </summary>
internal static class RequestPriceChange
{
    /// <param name="EffectiveFrom">The first day the new price applies (<c>yyyy-MM-dd</c>); today when empty. Past and future dates are both accepted.</param>
    internal sealed record Request(decimal? Price, string? EffectiveFrom, string? Reason);

    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapPost("/{projectId:int}/catalog-items/{itemId:int}/price-changes", HandleAsync)
            .RequirePermission(CatalogPermissions.Module, PermissionAction.Edit)
            .WithName("RequestPriceChange")
            .WithSummary("Ask an Executive to change a catalog item's price (one pending request per item)");

    internal static async Task<Results<Created<PriceChangeView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
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
        var price = NumberInput.RequiredMoney(request.Price, errors, "price", "The new price");
        var effectiveFrom = Input.OptionalDate(request.EffectiveFrom, errors, "effectiveFrom", "The effective date") ?? clock.Today;
        var reason = Input.RequiredText(request.Reason, ItemPriceConfiguration.ReasonMaxLength, errors, "reason", "The reason");
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

        var item = await CatalogQueries.One(db, projectId, itemId).SingleOrDefaultAsync(cancellationToken);
        if (item is null)
        {
            return PlanningProblems.CatalogItemNotFound();
        }

        if (item.Status != CatalogItemStatus.Active)
        {
            return WorkspaceProblems.WrongStatus("This catalog item is inactive. Reactivate it before changing its price.");
        }

        var prices = await CatalogQueries.Prices(db, projectId, itemId).ToListAsync(cancellationToken);
        var position = CatalogPricing.Position(prices, clock.Today);

        if (position.Pending is not null)
        {
            return PlanningProblems.PendingChangeExists();
        }

        if (position.Current?.Price == price)
        {
            return PlanningProblems.PriceUnchanged();
        }

        if (prices.Any(row => row.Status == ItemPriceStatus.Approved && row.EffectiveFrom == effectiveFrom))
        {
            return PlanningProblems.PriceDateAlreadyUsed();
        }

        var change = new ItemPrice
        {
            CatalogItemId = item.Id,
            Price = price!.Value,
            PreviousPrice = position.Current?.Price,
            EffectiveFrom = effectiveFrom,
            Status = ItemPriceStatus.Pending,
            Reason = reason,
            RequestedAt = clock.UtcNow,
            RequestedBy = caller.UserId,
        };
        db.ItemPrices.Add(change);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, ItemPriceConfiguration.PendingIndex))
        {
            return PlanningProblems.PendingChangeExists();
        }

        log.PriceChangeRequested(caller.UserId, projectId, item.Id, change.Id);

        var people = await PlanningPeople.FindAsync(users, [caller.UserId], cancellationToken);
        return TypedResults.Created(
            $"/api/projects/{projectId}/price-changes/{change.Id}",
            PriceChangeView.From(PriceRow.From(change), item.NameAr, item.NameEn, people));
    }
}
