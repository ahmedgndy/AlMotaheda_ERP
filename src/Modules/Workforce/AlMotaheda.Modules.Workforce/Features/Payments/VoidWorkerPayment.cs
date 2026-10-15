using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Workforce.Common;
using AlMotaheda.Modules.Workforce.Data;
using AlMotaheda.Modules.Workforce.Data.Configurations;
using AlMotaheda.Modules.Workforce.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Workforce.Features.Payments;

/// <summary>
/// <c>POST /api/worker-payments/{id}/void</c>: cancel a wrong payment. The row stays, marked with who voided it, when
/// and why; its allocations stop counting, so the days it settled are open again. Under the worker's lock, so a
/// payment being recorded at the same moment sees either the old or the new state, never half.
/// </summary>
internal static class VoidWorkerPayment
{
    internal sealed record Request(string? Reason, string? RowVersion);

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/worker-payments/{paymentId:int}/void", HandleAsync)
            .RequirePermission(WorkforcePermissions.Payments, PermissionAction.Approve)
            .WithName("VoidWorkerPayment")
            .WithSummary("Void a worker payment (payments Approve): its allocations stop counting");

    internal static async Task<Results<Ok<WorkerPaymentView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int paymentId,
        Request request,
        HttpContext httpContext,
        WorkforceDbContext db,
        IProjectAccess access,
        CompanyClock clock,
        WorkforceLog log,
        CancellationToken cancellationToken)
    {
        if (WorkforceCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var reason = Input.RequiredText(request.Reason, WorkerPaymentConfiguration.VoidReasonMaxLength, errors, "reason", "The reason");
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (errors.HasErrors)
        {
            return WorkforceProblems.Validation(errors);
        }

        var workerId = await db.WorkerPayments.AsNoTracking()
            .Where(payment => payment.Id == paymentId)
            .Select(payment => (int?)payment.WorkerId)
            .SingleOrDefaultAsync(cancellationToken);
        if (workerId is null)
        {
            return WorkforceProblems.PaymentNotFound();
        }

        // Only someone who can see every project the payment settled may void it; to others it does not exist.
        var paidProjects = await db.WorkerPaymentAllocations.AsNoTracking()
            .Where(allocation => allocation.PaymentId == paymentId)
            .Select(allocation => allocation.Line.Sheet.ProjectId)
            .Distinct()
            .ToListAsync(cancellationToken);
        var visible = await ProjectVisibility.AmongAsync(access, httpContext.User, paidProjects, cancellationToken);
        if (visible.Count != paidProjects.Count)
        {
            return WorkforceProblems.PaymentNotFound();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        if (!await AppLock.TryAcquireAsync(db, AppLock.Worker(workerId.Value), cancellationToken))
        {
            return WorkforceProblems.TryAgainLater();
        }

        var payment = await db.WorkerPayments.SingleAsync(candidate => candidate.Id == paymentId, cancellationToken);
        if (payment.IsVoided)
        {
            return WorkforceProblems.AlreadyVoided();
        }

        payment.VoidedAt = clock.UtcNow;
        payment.VoidedBy = caller.UserId;
        payment.VoidReason = reason;

        db.Entry(payment).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        await transaction.CommitAsync(cancellationToken);
        log.PaymentVoided(caller.UserId, payment.WorkerId, payment.Id);

        return TypedResults.Ok(await WorkerPaymentVisibility.ViewForAsync(db, access, httpContext.User, payment.Id, cancellationToken));
    }
}
