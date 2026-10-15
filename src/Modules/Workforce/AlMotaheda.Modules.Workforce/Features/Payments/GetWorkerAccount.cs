using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Workforce.Common;
using AlMotaheda.Modules.Workforce.Data;
using AlMotaheda.Modules.Workforce.Features.Workers;
using AlMotaheda.Modules.Workforce.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Workforce.Features.Payments;

/// <summary>A worker's money on one project.</summary>
internal sealed record WorkerProjectAccountView(
    int ProjectId,
    decimal ApprovedDays,
    decimal ApprovedPay,
    decimal Paid,
    decimal Outstanding,
    int OpenLines,
    DateOnly? OldestOpenDate);

/// <summary>
/// A worker's account: approved pay, paid and outstanding, in total and by project. Every figure, the totals
/// included, covers only the projects the caller may see.
/// </summary>
/// <param name="PendingLines">Lines on submitted or returned sheets: not pay yet, shown apart.</param>
internal sealed record WorkerAccountView(
    int WorkerId,
    string NameAr,
    string NameEn,
    decimal ApprovedDays,
    decimal ApprovedPay,
    decimal Paid,
    decimal Outstanding,
    int OpenLines,
    DateOnly? OldestOpenDate,
    int PendingLines,
    IReadOnlyList<WorkerProjectAccountView> Projects);

/// <summary>
/// <c>GET /api/workers/{id}/account</c>: what the worker has earned (approved days only), what was paid (payments not
/// voided) and what is still owed, with the open days by project. The same arithmetic as the payment itself.
/// </summary>
/// <remarks>
/// Only the projects the caller may see are shown, and the worker's totals are summed from those projects only
/// (the safer choice: a total over every project would reveal pay on projects the caller can't see). A caller who
/// sees every project, such as an Executive, gets the company-wide account.
/// </remarks>
internal static class GetWorkerAccount
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/workers/{workerId:int}/account", HandleAsync)
            .RequirePermission(WorkforcePermissions.Workers, PermissionAction.SeeMoney)
            .WithName("GetWorkerAccount")
            .WithSummary("A worker's approved pay, paid and outstanding, with the open days by project");

    internal static async Task<Results<Ok<WorkerAccountView>, ProblemHttpResult>> HandleAsync(
        int workerId,
        HttpContext httpContext,
        WorkforceDbContext db,
        IProjectAccess access,
        CancellationToken cancellationToken)
    {
        if (WorkforceCaller.From(httpContext.User) is null)
        {
            return ApiProblem.ForbiddenResult();
        }

        var worker = await WorkerQueries.One(db, workerId).SingleOrDefaultAsync(cancellationToken);
        if (worker is null)
        {
            return WorkforceProblems.WorkerNotFound();
        }

        var payable = await WorkerPaymentQueries.PayableLines(db, workerId).ToListAsync(cancellationToken);
        var paidParts = await WorkerPaymentQueries.PaidParts(db, workerId).ToListAsync(cancellationToken);
        var pending = await WorkerPaymentQueries.PendingLinesByProject(db, workerId).ToListAsync(cancellationToken);

        var visible = await ProjectVisibility.AmongAsync(
            access,
            httpContext.User,
            payable.Select(line => line.ProjectId).Concat(pending.Select(count => count.ProjectId)),
            cancellationToken);

        // The FIFO runs over every day, so a visible day shows what is really paid on it; then hidden projects go.
        var lines = WorkerAccounts.OnProjects(PaymentFifo.Settle(payable, paidParts), visible);
        var total = WorkerAccounts.Of(lines);

        return TypedResults.Ok(new WorkerAccountView(
            worker.Id,
            worker.NameAr,
            worker.NameEn,
            total.ApprovedDays,
            total.ApprovedPay,
            total.Paid,
            total.Outstanding,
            total.OpenLines,
            total.OldestOpenDate,
            pending.Where(count => visible.Contains(count.ProjectId)).Sum(count => count.Lines),
            WorkerAccounts.ByProject(lines)
                .Select(project => new WorkerProjectAccountView(
                    project.ProjectId,
                    project.Figures.ApprovedDays,
                    project.Figures.ApprovedPay,
                    project.Figures.Paid,
                    project.Figures.Outstanding,
                    project.Figures.OpenLines,
                    project.Figures.OldestOpenDate))
                .ToList()));
    }
}
