using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
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

namespace AlMotaheda.Modules.Projects.Features.SupplierPayments;

/// <summary>
/// <c>POST /api/projects/{pid}/supplier-payments/{id}/void</c>: cancel a wrong payment. The row stays, marked with
/// who voided it, when and why; its allocations stop counting, so the records it settled are open again.
/// </summary>
internal static class VoidSupplierPayment
{
    internal sealed record Request(string? Reason, string? RowVersion);

    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapPost("/{projectId:int}/supplier-payments/{paymentId:int}/void", HandleAsync)
            .RequirePermission(PaymentPermissions.Module, PermissionAction.Approve)
            .WithName("VoidSupplierPayment")
            .WithSummary("Void a supplier payment (payments Approve): its allocations stop counting");

    internal static async Task<Results<Ok<SupplierPaymentView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId,
        int paymentId,
        Request request,
        HttpContext httpContext,
        ProjectsDbContext db,
        IProjectAccess access,
        CompanyClock clock,
        ProjectsLog log,
        CancellationToken cancellationToken)
    {
        if (ProjectCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var reason = Input.RequiredText(request.Reason, SupplierPaymentConfiguration.VoidReasonMaxLength, errors, "reason", "The reason");
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

        var payment = await db.SupplierPayments
            .SingleOrDefaultAsync(candidate => candidate.Id == paymentId && candidate.ProjectId == projectId, cancellationToken);
        if (payment is null)
        {
            return CoreProblems.PaymentNotFound();
        }

        if (payment.IsVoided)
        {
            return CoreProblems.AlreadyVoided();
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

        log.SupplierPaymentVoided(caller.UserId, projectId, payment.Id);

        var view = await PaymentQueries.One(db, projectId, payment.Id).SingleAsync(cancellationToken);
        return TypedResults.Ok(SupplierPaymentView.From(view));
    }
}
