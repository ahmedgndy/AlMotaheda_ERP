using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.Modules.Subcontractors.Common;
using AlMotaheda.Modules.Subcontractors.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Subcontractors.Features.Companies;

/// <summary>
/// <c>GET /api/subcontractors</c>: one page of the company's subcontractors, for the company list and the company
/// picker on the contract form. Companies are company-wide (overview 3.6), so there is no project scope here, and
/// the list shows no contract or money.
/// </summary>
internal static class ListSubcontractors
{
    internal sealed record Response(IReadOnlyList<SubcontractorView> Items, int Page, int PageSize, int TotalCount);

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/subcontractors", HandleAsync)
            .RequirePermission(SubPermissions.Module, PermissionAction.View)
            .WithName("ListSubcontractors")
            .WithSummary("List the subcontractor companies (search: names, contact or phone; specialty; status)");

    internal static async Task<Results<Ok<Response>, ValidationProblem>> HandleAsync(
        SubcontractorsDbContext db,
        CancellationToken cancellationToken,
        string? search = null,
        string? specialty = null,
        string? status = null,
        int page = 1,
        int pageSize = Paging.DefaultPageSize)
    {
        var errors = new ValidationErrors();
        Paging.Validate(page, pageSize, errors);

        SubcontractorSpecialty? specialtyFilter = null;
        if (Input.Clean(specialty) is { } specialtyText)
        {
            if (Input.TryParseName<SubcontractorSpecialty>(specialtyText, out var parsed))
            {
                specialtyFilter = parsed;
            }
            else
            {
                errors.Add("specialty", $"specialty must be one of: {string.Join(", ", Enum.GetNames<SubcontractorSpecialty>())}.");
            }
        }

        SubcontractorStatus? statusFilter = null;
        if (Input.Clean(status) is { } statusText)
        {
            if (Input.TryParseName<SubcontractorStatus>(statusText, out var parsed))
            {
                statusFilter = parsed;
            }
            else
            {
                errors.Add("status", "status must be Active or Inactive.");
            }
        }

        if (errors.HasErrors)
        {
            return SubcontractorsProblems.Validation(errors);
        }

        var query = SubcontractorQueries.Search(db, Input.Clean(search), specialtyFilter, statusFilter);
        var totalCount = await query.CountAsync(cancellationToken);
        var companies = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);

        return TypedResults.Ok(new Response(companies.Select(SubcontractorView.From).ToList(), page, pageSize, totalCount));
    }
}

/// <summary><c>GET /api/subcontractors/{id}</c>: one company.</summary>
/// <remarks>
/// The company's contracts are not listed here: they belong to projects the caller may not see. Each project's
/// subcontractors screen shows its own contracts.
/// </remarks>
internal static class GetSubcontractor
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/subcontractors/{subcontractorId:int}", HandleAsync)
            .RequirePermission(SubPermissions.Module, PermissionAction.View)
            .WithName("GetSubcontractor")
            .WithSummary("Get one subcontractor company");

    internal static async Task<Results<Ok<SubcontractorView>, ProblemHttpResult>> HandleAsync(
        int subcontractorId, SubcontractorsDbContext db, CancellationToken cancellationToken)
    {
        var company = await db.Subcontractors.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == subcontractorId, cancellationToken);

        return company is null ? SubcontractorsProblems.SubcontractorNotFound() : TypedResults.Ok(SubcontractorView.From(company));
    }
}
