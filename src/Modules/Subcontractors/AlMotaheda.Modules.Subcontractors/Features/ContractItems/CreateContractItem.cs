using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Subcontractors.Common;
using AlMotaheda.Modules.Subcontractors.Data;
using AlMotaheda.Modules.Subcontractors.Features.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Subcontractors.Features.ContractItems;

/// <summary>
/// <c>POST /api/projects/{pid}/subcontracts/{cid}/items</c>: add a line of contracted work. It can be linked to a
/// project item at once (<c>projectItemId</c>), or later.
/// </summary>
/// <remarks>
/// Needs subcontractors Create or Edit. The endpoint takes the signed-in policy and checks the two permissions
/// itself, because <c>RequirePermission</c> asks for exactly one action.
/// </remarks>
internal static class CreateContractItem
{
    internal sealed record Request(
        string? Name,
        string? WorkType,
        string? Unit,
        decimal? Quantity,
        decimal? UnitPrice,
        string? Description,
        string? Notes,
        int? ProjectItemId) : IContractItemFieldsRequest;

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/projects/{projectId:int}/subcontracts/{contractId:int}/items", HandleAsync)
            .WithName("CreateContractItem")
            .WithSummary("Add a contract item (subcontractors Create or Edit)");

    internal static async Task<Results<Created<ContractItemView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
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
        if (SubCaller.From(httpContext.User) is not { } caller || !ItemPermissions.CanWrite(caller))
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var fields = ContractItemFields.Validate(request, errors);
        if (request.ProjectItemId is < 1)
        {
            errors.Add("projectItemId", "projectItemId is not valid.");
        }

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

        var contractStatus = await db.SubContracts.AsNoTracking()
            .Where(contract => contract.Id == contractId && contract.ProjectId == projectId)
            .Select(contract => (ContractStatus?)contract.Status)
            .SingleOrDefaultAsync(cancellationToken);
        if (contractStatus is null)
        {
            return SubcontractorsProblems.ContractNotFound();
        }

        if (contractStatus == ContractStatus.Completed)
        {
            return SubcontractorsProblems.CompletedContract();
        }

        if (request.ProjectItemId is { } projectItemId)
        {
            switch (await ProjectItemLinks.CheckAsync(projectItems, projectId, projectItemId, cancellationToken))
            {
                case ProjectItemCheck.NotFound:
                    errors.Add("projectItemId", "The project item was not found on this project.");
                    return SubcontractorsProblems.Validation(errors);
                case ProjectItemCheck.Inactive:
                    return SubcontractorsProblems.InactiveItem();
            }
        }

        var item = new ContractItem
        {
            ContractId = contractId,
            ProjectItemId = request.ProjectItemId,
            CreatedAt = clock.UtcNow,
            CreatedBy = caller.UserId,
        };
        fields!.ApplyTo(item);
        db.ContractItems.Add(item);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (item.ProjectItemId is not null && SqlViolations.IsReferenceOrCheck(exception))
        {
            // The project item was deleted by someone else after our check: a lost race is a 409, never a 500.
            return SubcontractorsProblems.ItemRemoved();
        }

        log.ContractItemCreated(caller.UserId, projectId, contractId, item.Id);

        var view = await details.ItemAsync(projectId, contractId, item.Id, caller.SeesMoney, cancellationToken);
        return TypedResults.Created($"/api/projects/{projectId}/subcontracts/{contractId}/items/{item.Id}", view!);
    }
}
