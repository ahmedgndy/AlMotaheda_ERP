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

/// <summary>The body of approve and reject. The note is required to reject.</summary>
internal sealed record PriceDecisionRequest(string? Note);

/// <summary>
/// <c>POST /api/projects/{pid}/price-changes/{priceId}/approve</c>: the new price applies from its effective date.
/// A date in the future makes it a scheduled price, which becomes current by itself on that day.
/// </summary>
internal static class ApprovePriceChange
{
    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapPost("/{projectId:int}/price-changes/{priceId:int}/approve", HandleAsync)
            .RequirePermission(CatalogPermissions.Module, PermissionAction.Approve)
            .WithName("ApprovePriceChange")
            .WithSummary("Approve a pending price change (catalog Approve, re-checked; never your own request)");

    internal static Task<Results<Ok<PriceChangeView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId, int priceId, PriceDecisionRequest request, HttpContext httpContext, ProjectsDbContext db, IProjectAccess access,
        IUserDirectory users, PermissionGate gate, CompanyClock clock, ProjectsLog log, CancellationToken cancellationToken) =>
        PriceDecision.HandleAsync(ItemPriceStatus.Approved, projectId, priceId, request, httpContext, db, access, users, gate, clock, log, cancellationToken);
}

/// <summary><c>POST /api/projects/{pid}/price-changes/{priceId}/reject</c>: the request is closed; the price stays.</summary>
internal static class RejectPriceChange
{
    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapPost("/{projectId:int}/price-changes/{priceId:int}/reject", HandleAsync)
            .RequirePermission(CatalogPermissions.Module, PermissionAction.Approve)
            .WithName("RejectPriceChange")
            .WithSummary("Reject a pending price change with a note (catalog Approve, re-checked; never your own request)");

    internal static Task<Results<Ok<PriceChangeView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId, int priceId, PriceDecisionRequest request, HttpContext httpContext, ProjectsDbContext db, IProjectAccess access,
        IUserDirectory users, PermissionGate gate, CompanyClock clock, ProjectsLog log, CancellationToken cancellationToken) =>
        PriceDecision.HandleAsync(ItemPriceStatus.Rejected, projectId, priceId, request, httpContext, db, access, users, gate, clock, log, cancellationToken);
}

/// <summary>
/// The one decision flow behind approve and reject: catalog Approve re-checked in the database (the Financial
/// Manager or an Executive by default, docs/cost-flow §2.1), never on your own
/// request, only while pending. A price row changes exactly once, here. It runs under the project lock, so two
/// Executives can't both decide the same request (the row has no row version: it is insert-only).
/// </summary>
internal static class PriceDecision
{
    public static async Task<Results<Ok<PriceChangeView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        ItemPriceStatus target,
        int projectId,
        int priceId,
        PriceDecisionRequest request,
        HttpContext httpContext,
        ProjectsDbContext db,
        IProjectAccess access,
        IUserDirectory users,
        PermissionGate gate,
        CompanyClock clock,
        ProjectsLog log,
        CancellationToken cancellationToken)
    {
        if (ProjectCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var note = target == ItemPriceStatus.Approved
            ? Input.OptionalText(request.Note, ItemPriceConfiguration.ReasonMaxLength, errors, "note", "The note")
            : Input.RequiredText(request.Note, ItemPriceConfiguration.ReasonMaxLength, errors, "note", "The note");
        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        var project = await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken);
        if (project is null)
        {
            return ProjectProblems.ProjectNotFound();
        }

        if (await gate.CheckAsync(httpContext.User, CatalogPermissions.Module, PermissionAction.Approve, $"PriceChange{target}", cancellationToken) is { } refused)
        {
            return refused;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        if (!await ProjectLock.TryAcquireAsync(db, projectId, cancellationToken))
        {
            return ProjectProblems.TryAgainLater();
        }

        var price = await db.ItemPrices
            .Include(candidate => candidate.CatalogItem)
            .SingleOrDefaultAsync(candidate => candidate.Id == priceId && candidate.CatalogItem.ProjectId == projectId, cancellationToken);
        if (price is null)
        {
            return PlanningProblems.PriceChangeNotFound();
        }

        if (price.RequestedBy == caller.UserId)
        {
            log.ActionRefused(caller.UserId, $"PriceChange{target}", "deciding own request");
            return WorkspaceProblems.ReviewerIsRecorder();
        }

        if (price.Status != ItemPriceStatus.Pending)
        {
            return WorkspaceProblems.WrongStatus($"Only a pending price change can be decided. This one is {price.Status}.");
        }

        if (target == ItemPriceStatus.Approved
            && await db.ItemPrices.AnyAsync(
                other => other.CatalogItemId == price.CatalogItemId && other.Status == ItemPriceStatus.Approved && other.EffectiveFrom == price.EffectiveFrom,
                cancellationToken))
        {
            return PlanningProblems.PriceDateAlreadyUsed();
        }

        price.Decide(target, caller.UserId, clock.UtcNow, note);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, ItemPriceConfiguration.ApprovedIndex))
        {
            return PlanningProblems.PriceDateAlreadyUsed();
        }

        await transaction.CommitAsync(cancellationToken);
        log.PriceChangeDecided(caller.UserId, projectId, price.CatalogItemId, price.Id, target.ToString());

        var people = await PlanningPeople.FindAsync(users, [price.RequestedBy, caller.UserId], cancellationToken);
        return TypedResults.Ok(PriceChangeView.From(PriceRow.From(price), price.CatalogItem.NameAr, price.CatalogItem.NameEn, people));
    }
}
