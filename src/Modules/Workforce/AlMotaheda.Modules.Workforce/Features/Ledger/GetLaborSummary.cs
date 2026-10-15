using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Workforce.Common;
using AlMotaheda.Modules.Workforce.Data;
using AlMotaheda.Modules.Workforce.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Workforce.Features.Ledger;

/// <summary>
/// The labor of one project at a glance. Counts for everyone with workers View; the money (approved, paid,
/// outstanding, not linked, pending) only with workers See money, otherwise <c>null</c>.
/// </summary>
/// <param name="SheetsPending">Submitted sheets waiting for a reviewer (returned ones are back with their recorder).</param>
/// <param name="NotLinkedCost">Approved labor not linked to any project item yet (part of <paramref name="Approved"/>).</param>
/// <param name="PendingAmount">A preview of the pending sheets' pay with today's rates; never cost.</param>
internal sealed record LaborSummaryView(
    int ProjectId,
    int WorkersAssigned,
    int SheetsDraft,
    int SheetsSubmitted,
    int SheetsReturned,
    int SheetsApproved,
    int SheetsRejected,
    int SheetsPending,
    int ApprovedLines,
    decimal? Approved,
    decimal? Paid,
    decimal? Outstanding,
    decimal? NotLinkedCost,
    decimal? PendingAmount,
    int PendingLines);

/// <summary><c>GET /api/projects/{pid}/labor/summary</c>: the labor tab's header figures.</summary>
internal static class GetLaborSummary
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/projects/{projectId:int}/labor/summary", HandleAsync)
            .RequirePermission(WorkforcePermissions.Workers, PermissionAction.View)
            .WithName("GetLaborSummary")
            .WithSummary("A project's labor summary: workers assigned, sheets by status; with See money the approved, paid, outstanding and pending amounts");

    internal static async Task<Results<Ok<LaborSummaryView>, ProblemHttpResult>> HandleAsync(
        int projectId,
        HttpContext httpContext,
        WorkforceDbContext db,
        IProjectAccess access,
        LaborLedgerSource source,
        CancellationToken cancellationToken)
    {
        if (WorkforceCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        if (await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken) is null)
        {
            return WorkforceProblems.ProjectNotFound();
        }

        var workers = await LaborQueries.OpenAssignments(db, projectId).CountAsync(cancellationToken);
        var counts = (await LaborQueries.SheetCounts(db, projectId).ToListAsync(cancellationToken))
            .ToDictionary(count => count.Status, count => count.Count);
        var ledger = await source.LoadAsync(projectId, null, null, withDeductionReasons: false, cancellationToken);
        var pending = await source.PendingAsync(projectId, null, null, cancellationToken);
        var money = caller.SeesMoney;

        return TypedResults.Ok(new LaborSummaryView(
            projectId,
            workers,
            counts.GetValueOrDefault(SheetStatus.Draft),
            counts.GetValueOrDefault(SheetStatus.Submitted),
            counts.GetValueOrDefault(SheetStatus.Returned),
            counts.GetValueOrDefault(SheetStatus.Approved),
            counts.GetValueOrDefault(SheetStatus.Rejected),
            counts.GetValueOrDefault(SheetStatus.Submitted),
            ledger.RecognizedLines,
            money ? ledger.Recognized : null,
            money ? ledger.Paid : null,
            money ? ledger.Outstanding : null,
            money ? ledger.Totals.Where(total => total.ItemId is null).Sum(total => total.Cost) : null,
            money ? pending.Amount : null,
            pending.Lines));
    }
}
