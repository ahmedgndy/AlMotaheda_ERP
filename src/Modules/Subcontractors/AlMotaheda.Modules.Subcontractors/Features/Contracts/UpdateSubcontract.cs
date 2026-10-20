using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Subcontractors.Common;
using AlMotaheda.Modules.Subcontractors.Data;
using AlMotaheda.Modules.Subcontractors.Data.Configurations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Subcontractors.Features.Contracts;

/// <summary>
/// <c>PUT /api/projects/{pid}/subcontracts/{cid}</c>: edit a contract. The company can't change (that is another
/// contract). The value can't go below the executed work or the payments, and the model can't change once work
/// was recorded under it.
/// </summary>
/// <remarks>
/// Runs under the project lock: the value check reads sums that an approval or a payment could change at the same
/// moment.
/// </remarks>
internal static class UpdateSubcontract
{
    internal sealed record Request(
        string? Model,
        decimal? Value,
        decimal? DailyRate,
        decimal? RetentionPercent,
        string? StartDate,
        string? EndDate,
        string? Status,
        string? ContractNo,
        string? Scope,
        string? RowVersion,
        int? ProjectItemId = null) : IContractFieldsRequest;

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPut("/projects/{projectId:int}/subcontracts/{contractId:int}", HandleAsync)
            .RequirePermission(SubPermissions.Module, PermissionAction.Edit)
            .WithName("UpdateSubcontract")
            .WithSummary("Edit a subcontract (value not below executed work or payments; model fixed once work exists)");

    internal static async Task<Results<Ok<SubcontractDetailView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId,
        int contractId,
        Request request,
        HttpContext httpContext,
        SubcontractorsDbContext db,
        IProjectAccess access,
        IProjectItems projectItems,
        ContractDetails details,
        CompanyClock clock,
        SubcontractorsLog log,
        CancellationToken cancellationToken)
    {
        if (SubCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var fields = ContractFields.Validate(request, errors);
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
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

        var contract = await db.SubContracts
            .SingleOrDefaultAsync(candidate => candidate.Id == contractId && candidate.ProjectId == projectId, cancellationToken);
        if (contract is null)
        {
            return SubcontractorsProblems.ContractNotFound();
        }

        var executed = await db.ProgressMeasurements
                .Where(measurement => measurement.ContractItem.ContractId == contractId && measurement.Status == WorkStatus.Approved)
                .SumAsync(measurement => measurement.Amount, cancellationToken)
            + await db.ManpowerSheets
                .Where(sheet => sheet.ContractId == contractId && sheet.Status == WorkStatus.Approved)
                .SumAsync(sheet => sheet.Amount, cancellationToken);
        var paid = await db.SubPayments
            .Where(payment => payment.ContractId == contractId && payment.VoidedAt == null)
            .SumAsync(payment => payment.Amount, cancellationToken);

        // The value that counts is the signed value plus the approved variations.
        var variations = await ContractValues.ApprovedVariations(db, contractId).SumAsync(cancellationToken);
        if (fields!.Value + variations < executed)
        {
            errors.Add("value", $"The contract value can't be less than the work already executed ({executed:0.00}).");
        }

        if (fields.Value + variations < paid)
        {
            errors.Add("value", $"The contract value can't be less than what was already paid ({paid:0.00}).");
        }

        // An edit that doesn't send the work item keeps it (older clients don't know the field).
        fields = fields with { ProjectItemId = fields.ProjectItemId ?? contract.ProjectItemId };
        if (await fields.CheckWorkItemAsync(projectItems, projectId, contract.ProjectItemId, errors, cancellationToken) is { } itemProblem)
        {
            return itemProblem;
        }

        if (fields.ProjectItemId is { } scope && scope != contract.ProjectItemId
            && await OutsideScopeAsync(db, projectItems, projectId, contractId, scope, cancellationToken))
        {
            return SubcontractorsProblems.OutsideContractScope();
        }

        if (fields.Model != contract.Model
            && (await db.ProgressMeasurements.AnyAsync(measurement => measurement.ContractItem.ContractId == contractId, cancellationToken)
                || await db.ManpowerSheets.AnyAsync(sheet => sheet.ContractId == contractId, cancellationToken)))
        {
            errors.Add("model", "Work was already recorded under this contract, so its model can't change.");
        }

        if (errors.HasErrors)
        {
            return SubcontractorsProblems.Validation(errors);
        }

        if (fields.ContractNo is { } contractNo && contractNo != contract.ContractNo
            && await db.SubContracts.AnyAsync(other => other.Id != contractId && other.ContractNo == contractNo, cancellationToken))
        {
            return SubcontractorsProblems.ContractNoAlreadyUsed();
        }

        var oldStatus = contract.Status;
        fields.ApplyTo(contract);
        contract.UpdatedAt = clock.UtcNow;
        contract.UpdatedBy = caller.UserId;
        db.Entry(contract).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, SubContractConfiguration.ContractNoIndex))
        {
            return SubcontractorsProblems.ContractNoAlreadyUsed();
        }

        await transaction.CommitAsync(cancellationToken);
        log.ContractUpdated(caller.UserId, projectId, contract.Id, oldStatus.ToString(), contract.Status.ToString());

        var view = await details.LoadAsync(projectId, contract.Id, caller.SeesMoney, cancellationToken);
        return view is null ? SubcontractorsProblems.ContractNotFound() : TypedResults.Ok(view);
    }

    /// <summary>True when work of the contract is already linked to an item outside the new main item and its sub-items.</summary>
    private static async Task<bool> OutsideScopeAsync(
        SubcontractorsDbContext db, IProjectItems projectItems, int projectId, int contractId, int scope, CancellationToken cancellationToken)
    {
        var linked = await db.ContractItems.Where(item => item.ContractId == contractId && item.ProjectItemId != null).Select(item => item.ProjectItemId!.Value)
            .Concat(db.ManpowerSheets.Where(sheet => sheet.ContractId == contractId && sheet.ProjectItemId != null).Select(sheet => sheet.ProjectItemId!.Value))
            .Distinct()
            .ToListAsync(cancellationToken);
        if (linked.Count == 0)
        {
            return false;
        }

        var items = await projectItems.FindAsync(projectId, linked, cancellationToken);
        return items.Values.Any(item => item.Id != scope && item.ParentItemId != scope);
    }
}
