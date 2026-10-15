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

namespace AlMotaheda.Modules.Projects.Features.Reports;

/// <summary>
/// <c>GET /api/projects/{pid}/reports/obligations</c>: what the project still owes. Supplier balances from this
/// module's records and payments; labor, subcontractors and equipment from the modules that own them.
/// </summary>
internal static class GetObligations
{
    internal sealed record SupplierRow(
        int SupplierId,
        string SupplierNameAr,
        string SupplierNameEn,
        string SupplierStatus,
        decimal CreditRecognized,
        decimal CustodyRecognized,
        decimal Paid,
        decimal Outstanding,
        int OpenInvoices);

    internal sealed record Response(
        IReadOnlyList<SupplierRow> Suppliers,
        OwedFigures SupplierTotals,
        OwedFigures WithoutSupplier,
        IReadOnlyList<OwedGroup> Groups,
        decimal TotalOutstanding);

    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapGet("/{projectId:int}/reports/obligations", HandleAsync)
            .RequirePermission(ProjectPermissions.Module, PermissionAction.SeeMoney)
            .WithName("GetProjectObligations")
            .WithSummary("What the project still owes: supplier balances, and labor, subcontract and equipment recognized, paid and outstanding");

    internal static async Task<Results<Ok<Response>, ProblemHttpResult>> HandleAsync(
        int projectId,
        HttpContext httpContext,
        ProjectsDbContext db,
        IProjectAccess access,
        ProjectCostLoader costs,
        CancellationToken cancellationToken)
    {
        if (ProjectCaller.From(httpContext.User) is null)
        {
            return ApiProblem.ForbiddenResult();
        }

        if (await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken) is null)
        {
            return ProjectProblems.ProjectNotFound();
        }

        var data = await costs.LoadAsync(projectId, from: null, to: null, cancellationToken);
        var result = Obligations.Compute(data.Expenses, data.Groups);

        var supplierIds = result.Suppliers.Select(balance => balance.SupplierId).ToArray();
        var names = supplierIds.Length == 0
            ? new Dictionary<int, SupplierName>()
            : await ReportQueries.SupplierNames(db, supplierIds).ToDictionaryAsync(name => name.Id, cancellationToken);

        var suppliers = result.Suppliers
            .Select(balance =>
            {
                var name = names.GetValueOrDefault(balance.SupplierId);
                return new SupplierRow(
                    balance.SupplierId,
                    name?.NameAr ?? "",
                    name?.NameEn ?? "",
                    name?.Status.ToString() ?? "",
                    balance.CreditRecognized,
                    balance.CustodyRecognized,
                    balance.Paid,
                    balance.Outstanding,
                    balance.OpenInvoices);
            })
            .OrderByDescending(row => row.Outstanding)
            .ThenBy(row => row.SupplierNameEn, StringComparer.Ordinal)
            .ToList();

        return TypedResults.Ok(new Response(suppliers, result.SupplierTotals, result.WithoutSupplier, result.Groups, result.TotalOutstanding));
    }
}
