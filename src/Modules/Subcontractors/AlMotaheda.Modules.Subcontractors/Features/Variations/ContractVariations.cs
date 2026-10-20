using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Subcontractors.Common;
using AlMotaheda.Modules.Subcontractors.Data;
using AlMotaheda.Modules.Subcontractors.Data.Configurations;
using AlMotaheda.Modules.Subcontractors.Features.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Subcontractors.Features.Variations;

/// <summary>
/// <c>POST /api/projects/{pid}/subcontracts/{cid}/variations</c>: ask to change the agreed contract value (an approved
/// change order: more work, or less). It waits for someone else with subcontractors Approve; until then the
/// contract is worth what it was. subcontractors Edit, project scope, contract not completed.
/// </summary>
internal static class RequestVariation
{
    internal sealed record Request(decimal? Amount, string? Reason, string? Reference);

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/projects/{projectId:int}/subcontracts/{contractId:int}/variations", HandleAsync)
            .RequirePermission(SubPermissions.Module, PermissionAction.Edit)
            .WithName("RequestContractVariation")
            .WithSummary("Ask to change a subcontract's value (positive or negative); someone with subcontractors Approve decides");

    internal static async Task<Results<Created<VariationView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId,
        int contractId,
        Request request,
        HttpContext httpContext,
        SubcontractorsDbContext db,
        IProjectAccess access,
        IUserDirectory users,
        CompanyClock clock,
        SubcontractorsLog log,
        CancellationToken cancellationToken)
    {
        if (SubCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        if (request.Amount is not { } amount || amount == 0 || Math.Abs(amount) > NumberInput.MaxMoney || decimal.Round(amount, 2) != amount)
        {
            errors.Add("amount", "The change must be a non-zero amount with at most two decimals (negative to reduce the contract).");
        }

        var reason = Input.RequiredText(request.Reason, ContractVariationConfiguration.ReasonMaxLength, errors, "reason", "The reason");
        var reference = Input.OptionalText(request.Reference, ContractVariationConfiguration.ReferenceMaxLength, errors, "reference", "The reference");
        if (errors.HasErrors)
        {
            return SubcontractorsProblems.Validation(errors);
        }

        var project = await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken);
        if (project is null)
        {
            return SubcontractorsProblems.ProjectNotFound();
        }

        if (project.WritesBlocked)
        {
            return SubcontractorsProblems.CompletedProject();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await AppLock.TryAcquireAsync(db, AppLock.Project(projectId), cancellationToken))
        {
            return SubcontractorsProblems.TryAgainLater();
        }

        var contract = await db.SubContracts.AsNoTracking()
            .Where(candidate => candidate.Id == contractId && candidate.ProjectId == projectId)
            .Select(candidate => new { candidate.Status })
            .SingleOrDefaultAsync(cancellationToken);
        if (contract is null)
        {
            return SubcontractorsProblems.ContractNotFound();
        }

        if (contract.Status == ContractStatus.Completed)
        {
            return SubcontractorsProblems.CompletedContract();
        }

        if (await db.ContractVariations.AnyAsync(other => other.ContractId == contractId && other.Status == VariationStatus.Pending, cancellationToken))
        {
            return SubcontractorsProblems.VariationAlreadyPending();
        }

        var variation = new ContractVariation
        {
            ContractId = contractId,
            ProjectId = projectId,
            Amount = request.Amount!.Value,
            Reason = reason!,
            Reference = reference,
            Status = VariationStatus.Pending,
            RequestedAt = clock.UtcNow,
            RequestedBy = caller.UserId,
        };
        db.ContractVariations.Add(variation);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, ContractVariationConfiguration.PendingIndex))
        {
            return SubcontractorsProblems.VariationAlreadyPending();
        }

        await transaction.CommitAsync(cancellationToken);
        log.VariationRequested(caller.UserId, projectId, contractId, variation.Id);

        var people = await users.FindAsync([caller.UserId], cancellationToken);
        return TypedResults.Created($"/api/projects/{projectId}/subcontracts/{contractId}", VariationView.From(variation, people, caller.SeesMoney));
    }
}

/// <summary>The body of approve and reject. The note is required to reject.</summary>
internal sealed record VariationDecisionRequest(string? Note);

/// <summary><c>POST /api/projects/{pid}/subcontract-variations/{id}/approve</c>: the contract is worth its value plus this change.</summary>
internal static class ApproveVariation
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/projects/{projectId:int}/subcontract-variations/{variationId:int}/approve", HandleAsync)
            .RequirePermission(SubPermissions.Module, PermissionAction.Approve)
            .WithName("ApproveContractVariation")
            .WithSummary("Approve a change of a subcontract's value (not your own request; never below the work approved or the money paid)");

    internal static Task<Results<Ok<VariationView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId, int variationId, VariationDecisionRequest request, HttpContext httpContext, SubcontractorsDbContext db, IProjectAccess access,
        IUserDirectory users, CompanyClock clock, SubcontractorsLog log, CancellationToken cancellationToken) =>
        VariationDecision.HandleAsync(VariationStatus.Approved, projectId, variationId, request, httpContext, db, access, users, clock, log, cancellationToken);
}

/// <summary><c>POST /api/projects/{pid}/subcontract-variations/{id}/reject</c>: the contract keeps its value.</summary>
internal static class RejectVariation
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/projects/{projectId:int}/subcontract-variations/{variationId:int}/reject", HandleAsync)
            .RequirePermission(SubPermissions.Module, PermissionAction.Approve)
            .WithName("RejectContractVariation")
            .WithSummary("Reject a change of a subcontract's value, with a note (not your own request)");

    internal static Task<Results<Ok<VariationView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId, int variationId, VariationDecisionRequest request, HttpContext httpContext, SubcontractorsDbContext db, IProjectAccess access,
        IUserDirectory users, CompanyClock clock, SubcontractorsLog log, CancellationToken cancellationToken) =>
        VariationDecision.HandleAsync(VariationStatus.Rejected, projectId, variationId, request, httpContext, db, access, users, clock, log, cancellationToken);
}

/// <summary>
/// The one decision flow: subcontractors Approve, never on your own request, only while pending, under the project
/// lock. An approval may not take the contract below what is already approved as work or paid (the commitment
/// would turn negative and the payments would exceed the contract).
/// </summary>
internal static class VariationDecision
{
    public static async Task<Results<Ok<VariationView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        VariationStatus target,
        int projectId,
        int variationId,
        VariationDecisionRequest request,
        HttpContext httpContext,
        SubcontractorsDbContext db,
        IProjectAccess access,
        IUserDirectory users,
        CompanyClock clock,
        SubcontractorsLog log,
        CancellationToken cancellationToken)
    {
        if (SubCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var note = target == VariationStatus.Approved
            ? Input.OptionalText(request.Note, ContractVariationConfiguration.ReasonMaxLength, errors, "note", "The note")
            : Input.RequiredText(request.Note, ContractVariationConfiguration.ReasonMaxLength, errors, "note", "The note");
        if (errors.HasErrors)
        {
            return SubcontractorsProblems.Validation(errors);
        }

        var project = await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken);
        if (project is null)
        {
            return SubcontractorsProblems.ProjectNotFound();
        }

        if (project.WritesBlocked)
        {
            return SubcontractorsProblems.CompletedProject();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await AppLock.TryAcquireAsync(db, AppLock.Project(projectId), cancellationToken))
        {
            return SubcontractorsProblems.TryAgainLater();
        }

        var variation = await db.ContractVariations
            .SingleOrDefaultAsync(candidate => candidate.Id == variationId && candidate.ProjectId == projectId, cancellationToken);
        if (variation is null)
        {
            return SubcontractorsProblems.VariationNotFound();
        }

        if (variation.RequestedBy == caller.UserId)
        {
            log.ActionRefused(caller.UserId, $"Variation{target}", "deciding own request");
            return SubcontractorsProblems.ReviewerIsRecorder();
        }

        if (variation.Status != VariationStatus.Pending)
        {
            return SubcontractorsProblems.WrongStatus($"Only a pending variation can be decided. This one is {variation.Status}.");
        }

        if (target == VariationStatus.Approved)
        {
            var effective = await ContractValues.EffectiveAsync(db, variation.ContractId, cancellationToken);
            var executed = await db.ProgressMeasurements
                    .Where(measurement => measurement.ContractItem.ContractId == variation.ContractId && measurement.Status == WorkStatus.Approved)
                    .SumAsync(measurement => measurement.Amount, cancellationToken)
                + await db.ManpowerSheets
                    .Where(sheet => sheet.ContractId == variation.ContractId && sheet.Status == WorkStatus.Approved)
                    .SumAsync(sheet => sheet.Amount, cancellationToken);
            var paid = await db.SubPayments
                .Where(payment => payment.ContractId == variation.ContractId && payment.VoidedAt == null)
                .SumAsync(payment => payment.Amount, cancellationToken);
            var floor = Math.Max(Math.Max(executed, paid), 0.01m);
            if (effective + variation.Amount < floor)
            {
                return SubcontractorsProblems.BelowExecuted(floor);
            }
        }

        variation.Decide(target, caller.UserId, clock.UtcNow, note);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        log.VariationDecided(caller.UserId, projectId, variation.ContractId, variation.Id, target.ToString());

        var people = await users.FindAsync([variation.RequestedBy, caller.UserId], cancellationToken);
        return TypedResults.Ok(VariationView.From(variation, people, caller.SeesMoney));
    }
}
