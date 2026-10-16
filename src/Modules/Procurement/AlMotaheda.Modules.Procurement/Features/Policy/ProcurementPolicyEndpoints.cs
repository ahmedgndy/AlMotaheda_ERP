using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Procurement.Common;
using AlMotaheda.Modules.Procurement.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Procurement.Features.Policy;

internal sealed record PolicyView(bool AutoApproveRequisitions, string PolicyName, decimal PriceTolerancePercent, DateTime? UpdatedAt, int? UpdatedBy, string RowVersion)
{
    public static PolicyView From(ProcurementPolicy policy) => new(
        policy.AutoApproveRequisitions, ProcurementPolicy.AutoApprovalPolicyName, policy.PriceTolerancePercent, policy.UpdatedAt, policy.UpdatedBy,
        RowVersions.Format(policy.RowVersion));
}

/// <summary>
/// <c>GET /api/procurement/policy</c> (procurement View) and <c>PUT</c> (Executive, re-checked in the database):
/// requisition auto-approval and the 3-way match price tolerance. Every change is in the audit trail.
/// </summary>
internal static class ProcurementPolicyEndpoints
{
    internal sealed record Request(bool? AutoApproveRequisitions, decimal? PriceTolerancePercent, string? RowVersion);

    public static void Map(IEndpointRouteBuilder api)
    {
        api.MapGet("/procurement/policy", GetAsync)
            .RequirePermission(ProcurementPermissions.Module, PermissionAction.View)
            .WithName("GetProcurementPolicy")
            .WithSummary("The procurement policy: requisition auto-approval and the price tolerance");

        api.MapPut("/procurement/policy", UpdateAsync)
            .RequireRole(AccessRoles.Executive)
            .WithName("UpdateProcurementPolicy")
            .WithSummary("Change the procurement policy (Executive only, audit-logged)");
    }

    internal static async Task<Ok<PolicyView>> GetAsync(ProcurementDbContext db, CancellationToken cancellationToken) =>
        TypedResults.Ok(PolicyView.From(await db.Policies.AsNoTracking().SingleAsync(cancellationToken)));

    internal static async Task<Results<Ok<PolicyView>, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        Request request, HttpContext httpContext, ProcurementDbContext db, IExecutiveCheck executives, CompanyClock clock, ProcurementLog log,
        CancellationToken cancellationToken)
    {
        if (ProcurementCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        if (request.AutoApproveRequisitions is null)
        {
            errors.Add("autoApproveRequisitions", "autoApproveRequisitions is required (true or false).");
        }

        if (request.PriceTolerancePercent is not { } tolerance || tolerance < 0 || tolerance > 100 || decimal.Round(tolerance, 2) != tolerance)
        {
            errors.Add("priceTolerancePercent", "priceTolerancePercent must be between 0 and 100, with at most two decimals.");
        }

        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (errors.HasErrors)
        {
            return ProcurementProblems.Validation(errors);
        }

        if (!await caller.IsActiveExecutiveAsync(executives, cancellationToken))
        {
            return ProcurementProblems.Executive();
        }

        var policy = await db.Policies.SingleAsync(cancellationToken);
        policy.AutoApproveRequisitions = request.AutoApproveRequisitions!.Value;
        policy.PriceTolerancePercent = request.PriceTolerancePercent!.Value;
        policy.UpdatedAt = clock.UtcNow;
        policy.UpdatedBy = caller.UserId;
        db.Entry(policy).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        log.PolicyChanged(caller.UserId, policy.AutoApproveRequisitions, policy.PriceTolerancePercent);
        return TypedResults.Ok(PolicyView.From(policy));
    }
}
