using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Features.SupplierPayments;

/// <summary><c>GET /api/projects/{pid}/supplier-payments</c>: one page of a project's supplier payments, newest first.</summary>
internal static class ListSupplierPayments
{
    internal sealed record Response(IReadOnlyList<SupplierPaymentView> Items, int Page, int PageSize, int TotalCount);

    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapGet("/{projectId:int}/supplier-payments", HandleAsync)
            .RequirePermission(ProjectPermissions.Module, PermissionAction.SeeMoney)
            .WithName("ListSupplierPayments")
            .WithSummary("List a project's supplier payments with their allocations (supplierId; includeVoided)");

    internal static async Task<Results<Ok<Response>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId,
        HttpContext httpContext,
        ProjectsDbContext db,
        IProjectAccess access,
        CancellationToken cancellationToken,
        int? supplierId = null,
        bool includeVoided = false,
        int page = 1,
        int pageSize = Paging.DefaultPageSize)
    {
        if (ProjectCaller.From(httpContext.User) is null)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        Paging.Validate(page, pageSize, errors);
        if (supplierId is < 1)
        {
            errors.Add("supplierId", "supplierId is not valid.");
        }

        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        if (await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken) is null)
        {
            return ProjectProblems.ProjectNotFound();
        }

        var query = PaymentQueries.List(db, projectId, supplierId, includeVoided);
        var totalCount = await query.CountAsync(cancellationToken);
        var rows = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);

        return TypedResults.Ok(new Response(rows.Select(SupplierPaymentView.From).ToList(), page, pageSize, totalCount));
    }
}
