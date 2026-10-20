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
/// <c>POST /api/projects/{pid}/subcontracts</c>: give an existing, active company a contract on this project for one
/// main work item (<c>projectItemId</c>). One contract per company, project and main item; the same company keeps its
/// other items' and projects' contracts apart, under one company identity.
/// </summary>
/// <remarks>The contract value is recorded as a commitment. It never becomes cost by itself.</remarks>
internal static class CreateSubcontract
{
    internal sealed record Request(
        int? SubcontractorId,
        string? Model,
        decimal? Value,
        decimal? DailyRate,
        decimal? RetentionPercent,
        string? StartDate,
        string? EndDate,
        string? Status,
        string? ContractNo,
        string? Scope,
        int? ProjectItemId = null) : IContractFieldsRequest;

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/projects/{projectId:int}/subcontracts", HandleAsync)
            .RequirePermission(SubPermissions.Module, PermissionAction.Create)
            .WithName("CreateSubcontract")
            .WithSummary("Give an existing subcontractor company a contract on this project");

    internal static async Task<Results<Created<SubcontractDetailView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId,
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
        if (request.SubcontractorId is null or < 1)
        {
            errors.Add("subcontractorId", "subcontractorId is required.");
        }

        var fields = ContractFields.Validate(request, errors);
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

        var subcontractorId = request.SubcontractorId!.Value;
        var companyStatus = await db.Subcontractors.AsNoTracking()
            .Where(company => company.Id == subcontractorId)
            .Select(company => (SubcontractorStatus?)company.Status)
            .SingleOrDefaultAsync(cancellationToken);

        if (companyStatus is null)
        {
            errors.Add("subcontractorId", "The subcontractor was not found.");
            return SubcontractorsProblems.Validation(errors);
        }

        if (companyStatus == SubcontractorStatus.Inactive)
        {
            return SubcontractorsProblems.InactiveSubcontractor();
        }

        if (await fields!.CheckWorkItemAsync(projectItems, projectId, null, errors, cancellationToken) is { } itemProblem)
        {
            return itemProblem;
        }

        if (errors.HasErrors)
        {
            return SubcontractorsProblems.Validation(errors);
        }

        if (await db.SubContracts.AnyAsync(
                contract => contract.ProjectId == projectId && contract.SubcontractorId == subcontractorId && contract.ProjectItemId == fields.ProjectItemId,
                cancellationToken))
        {
            return SubcontractorsProblems.CompanyAlreadyOnProject();
        }

        if (fields!.ContractNo is { } contractNo && await db.SubContracts.AnyAsync(contract => contract.ContractNo == contractNo, cancellationToken))
        {
            return SubcontractorsProblems.ContractNoAlreadyUsed();
        }

        var contract = new SubContract
        {
            ProjectId = projectId,
            SubcontractorId = subcontractorId,
            CreatedAt = clock.UtcNow,
            CreatedBy = caller.UserId,
        };
        fields.ApplyTo(contract);
        db.SubContracts.Add(contract);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, SubContractConfiguration.ProjectCompanyIndex))
        {
            return SubcontractorsProblems.CompanyAlreadyOnProject();
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, SubContractConfiguration.ContractNoIndex))
        {
            return SubcontractorsProblems.ContractNoAlreadyUsed();
        }

        log.ContractCreated(caller.UserId, projectId, contract.Id, subcontractorId, contract.Model.ToString());

        var view = await details.LoadAsync(projectId, contract.Id, caller.SeesMoney, cancellationToken);
        return TypedResults.Created($"/api/projects/{projectId}/subcontracts/{contract.Id}", view!);
    }
}
