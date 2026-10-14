using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Features.Expenses;
using AlMotaheda.Modules.Projects.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Features.SupplierPayments;

/// <summary>
/// <c>GET /api/projects/{pid}/supplier-balances</c>: what each supplier has delivered to the project and what it
/// is still owed, from the approved records and the payments that are not voided.
/// </summary>
internal static class GetSupplierBalances
{
    internal sealed record Row(
        int SupplierId,
        string SupplierNameAr,
        string SupplierNameEn,
        string SupplierKind,
        string SupplierStatus,
        decimal CreditRecognized,
        decimal CustodyRecognized,
        decimal Paid,
        decimal Outstanding,
        int OpenInvoices);

    internal sealed record Response(IReadOnlyList<Row> Items, decimal CreditRecognized, decimal CustodyRecognized, decimal Paid, decimal Outstanding);

    private sealed record SupplierName(int Id, string NameAr, string NameEn, SupplierKind Kind, SupplierStatus Status);

    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapGet("/{projectId:int}/supplier-balances", HandleAsync)
            .RequirePermission(ProjectPermissions.Module, PermissionAction.SeeMoney)
            .WithName("GetSupplierBalances")
            .WithSummary("Per supplier: recognized on credit and from custody, paid, outstanding and open invoices");

    internal static async Task<Results<Ok<Response>, ProblemHttpResult>> HandleAsync(
        int projectId,
        HttpContext httpContext,
        ProjectsDbContext db,
        IProjectAccess access,
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

        var rows = await ExpenseQueries.SettlementRows(db, projectId).ToListAsync(cancellationToken);
        var balances = SupplierBalances.Compute(rows);

        // One query for every supplier's name, never one per row.
        var supplierIds = balances.Select(balance => balance.SupplierId).ToArray();
        var suppliers = supplierIds.Length == 0
            ? []
            : await db.Suppliers.AsNoTracking()
                .Where(supplier => supplierIds.Contains(supplier.Id))
                .Select(supplier => new SupplierName(supplier.Id, supplier.NameAr, supplier.NameEn, supplier.Kind, supplier.Status))
                .ToListAsync(cancellationToken);

        var items = balances
            .Select(balance =>
            {
                var supplier = suppliers.First(candidate => candidate.Id == balance.SupplierId);
                return new Row(
                    balance.SupplierId,
                    supplier.NameAr,
                    supplier.NameEn,
                    supplier.Kind.ToString(),
                    supplier.Status.ToString(),
                    balance.CreditRecognized,
                    balance.CustodyRecognized,
                    balance.Paid,
                    balance.Outstanding,
                    balance.OpenInvoices);
            })
            .OrderBy(row => row.SupplierNameEn)
            .ToList();

        return TypedResults.Ok(new Response(
            items,
            items.Sum(row => row.CreditRecognized),
            items.Sum(row => row.CustodyRecognized),
            items.Sum(row => row.Paid),
            items.Sum(row => row.Outstanding)));
    }
}
