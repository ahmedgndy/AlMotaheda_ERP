using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
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

/// <summary>One payment that was recorded on a cost and later voided as wrong: the payment, and who voided it, when and why.</summary>
internal sealed record VoidedEquipmentPaymentView(
    int Id,
    DateOnly PaidOn,
    string Method,
    string? Reference,
    DateTime RecordedAt,
    FleetPerson RecordedBy,
    DateTime VoidedAt,
    FleetPerson VoidedBy,
    string Reason);

/// <summary>The rows <see cref="ListVoidedEquipmentPayments"/> reads, newest void first.</summary>
internal static class VoidedEquipmentPaymentQueries
{
    public static IQueryable<VoidedEquipmentCostPayment> ForCost(FleetDbContext db, int costId) =>
        db.VoidedEquipmentCostPayments.AsNoTracking()
            .Where(voided => voided.CostId == costId)
            .OrderByDescending(voided => voided.VoidedAt)
            .ThenByDescending(voided => voided.Id);
}

/// <summary>
/// <c>GET /api/projects/{pid}/equipment-costs/{id}/voided-payments</c>: every payment of one cost that was voided,
/// newest first. A void clears the payment from the cost row (so the cost can be paid again); this is where the
/// voided payment stays readable (decision F2).
/// </summary>
/// <remarks>
/// The same rule as the cost itself: equipment View on a visible project, and See money or being the recorder.
/// Anyone else gets the same 404 as for a cost that doesn't exist.
/// </remarks>
internal static class ListVoidedEquipmentPayments
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/projects/{projectId:int}/equipment-costs/{costId:int}/voided-payments", HandleAsync)
            .RequirePermission(FleetPermissions.Module, PermissionAction.View)
            .WithName("ListVoidedEquipmentPayments")
            .WithSummary("The voided payments of one equipment cost, newest first: who recorded and who voided each, and why");

    internal static async Task<Results<Ok<IReadOnlyList<VoidedEquipmentPaymentView>>, ProblemHttpResult>> HandleAsync(
        int projectId,
        int costId,
        HttpContext httpContext,
        FleetDbContext db,
        IProjectAccess access,
        IUserDirectory users,
        CancellationToken cancellationToken)
    {
        if (FleetCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        if (await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken) is null)
        {
            return FleetProblems.ProjectNotFound();
        }

        var cost = await db.EquipmentCosts.AsNoTracking()
            .Where(candidate => candidate.Id == costId && candidate.ProjectId == projectId)
            .Select(candidate => new { candidate.CreatedBy })
            .SingleOrDefaultAsync(cancellationToken);
        if (cost is null || (!caller.SeesMoney && cost.CreatedBy != caller.UserId))
        {
            return FleetProblems.CostNotFound();
        }

        var rows = await VoidedEquipmentPaymentQueries.ForCost(db, costId).ToListAsync(cancellationToken);
        var people = await Lookups.UsersAsync(
            users, rows.SelectMany(row => new int?[] { row.PaymentRecordedBy, row.VoidedBy }), cancellationToken);

        IReadOnlyList<VoidedEquipmentPaymentView> views = rows
            .Select(row => new VoidedEquipmentPaymentView(
                row.Id,
                row.PaidOn,
                row.PaymentMethod.ToString(),
                row.PaymentReference,
                row.PaymentRecordedAt,
                FleetPerson.From(row.PaymentRecordedBy, people),
                row.VoidedAt,
                FleetPerson.From(row.VoidedBy, people),
                row.VoidReason))
            .ToList();
        return TypedResults.Ok(views);
    }
}
