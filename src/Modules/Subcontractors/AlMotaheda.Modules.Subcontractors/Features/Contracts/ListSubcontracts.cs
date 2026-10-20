using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Subcontractors.Common;
using AlMotaheda.Modules.Subcontractors.Data;
using AlMotaheda.Modules.Subcontractors.Features.Accounts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Subcontractors.Features.Contracts;

/// <summary>
/// <c>GET /api/projects/{pid}/subcontracts</c>: the subcontractors screen of a project. Every contract with its
/// account, and the totals over the project. Money is <c>null</c> without subcontractors See money.
/// </summary>
/// <remarks>
/// A project has a handful of contracts, so the list is not paged: the totals need every contract anyway. The
/// accounts come from <see cref="ContractAccount"/> over flat rows loaded for the whole project (decision G5).
/// </remarks>
internal static class ListSubcontracts
{
    /// <param name="Totals">The sums over every contract of the project, whatever the filter. <c>null</c> without See money.</param>
    internal sealed record Response(IReadOnlyList<SubcontractView> Items, ContractTotals? Totals);

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/projects/{projectId:int}/subcontracts", HandleAsync)
            .RequirePermission(SubPermissions.Module, PermissionAction.View)
            .WithName("ListSubcontracts")
            .WithSummary("List the subcontract contracts of a project, each with its account (status: Pending, Active or Completed)");

    internal static async Task<Results<Ok<Response>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId,
        HttpContext httpContext,
        SubcontractorsDbContext db,
        IProjectAccess access,
        IProjectItems projectItems,
        CancellationToken cancellationToken,
        string? status = null)
    {
        if (SubCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        ContractStatus? statusFilter = null;
        if (Input.Clean(status) is { } statusText)
        {
            if (Input.TryParseName<ContractStatus>(statusText, out var parsed))
            {
                statusFilter = parsed;
            }
            else
            {
                errors.Add("status", $"status must be one of: {string.Join(", ", Enum.GetNames<ContractStatus>())}.");
            }
        }

        if (errors.HasErrors)
        {
            return SubcontractorsProblems.Validation(errors);
        }

        if (await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken) is null)
        {
            return SubcontractorsProblems.ProjectNotFound();
        }

        var contracts = await ContractQueries.Rows(db, projectId).ToListAsync(cancellationToken);
        var work = await WorkQueries.LoadWorkAsync(db, projectId, contractId: null, cancellationToken);
        var payments = await WorkQueries.Payments(db, projectId).ToListAsync(cancellationToken);

        var workByContract = work.ToLookup(row => row.ContractId);
        var paymentsByContract = payments.ToLookup(row => row.ContractId);
        var accounts = contracts.ToDictionary(
            contract => contract.Id,
            contract => ContractAccount.Compute(contract.Terms, workByContract[contract.Id], paymentsByContract[contract.Id]));

        var seesMoney = caller.SeesMoney;
        var itemNames = await projectItems.FindAsync(
            projectId, contracts.Select(contract => contract.ProjectItemId).OfType<int>().Distinct().ToList(), cancellationToken);
        var items = contracts
            .Where(contract => statusFilter is null || contract.Status == statusFilter)
            .Select(contract => SubcontractView.From(contract, accounts[contract.Id], seesMoney, itemNames))
            .ToList();

        return TypedResults.Ok(new Response(items, seesMoney ? ContractTotals.Sum(accounts.Values) : null));
    }
}

/// <summary>
/// <c>GET /api/projects/{pid}/subcontracts/{cid}</c>: one contract's account page: the company, the contract, its
/// items with measured and remaining quantity, its measurements, manpower sheets and payments, and its account.
/// </summary>
internal static class GetSubcontract
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/projects/{projectId:int}/subcontracts/{contractId:int}", HandleAsync)
            .RequirePermission(SubPermissions.Module, PermissionAction.View)
            .WithName("GetSubcontract")
            .WithSummary("Get one subcontract with its items, measured work, manpower sheets, payments and account");

    internal static async Task<Results<Ok<SubcontractDetailView>, ProblemHttpResult>> HandleAsync(
        int projectId,
        int contractId,
        HttpContext httpContext,
        IProjectAccess access,
        ContractDetails details,
        CancellationToken cancellationToken)
    {
        if (SubCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        if (await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken) is null)
        {
            return SubcontractorsProblems.ProjectNotFound();
        }

        var view = await details.LoadAsync(projectId, contractId, caller.SeesMoney, cancellationToken);
        return view is null ? SubcontractorsProblems.ContractNotFound() : TypedResults.Ok(view);
    }
}
