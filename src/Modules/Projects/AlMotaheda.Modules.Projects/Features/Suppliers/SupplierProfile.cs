using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Features.Expenses;
using AlMotaheda.Modules.Projects.Features.SupplierPayments;
using AlMotaheda.Modules.Projects.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Features.Suppliers;

/// <summary><c>GET /api/suppliers/{id}</c>: one supplier's profile (company-wide, like the list).</summary>
internal static class GetSupplier
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/suppliers/{supplierId:int}", HandleAsync)
            .RequireAnyPermission((ProjectPermissions.Module, PermissionAction.View), (ProjectPermissions.ProcurementModule, PermissionAction.View))
            .WithName("GetSupplier")
            .WithSummary("Get one supplier's profile");

    internal static async Task<Results<Ok<SupplierView>, ProblemHttpResult>> HandleAsync(int supplierId, ProjectsDbContext db, CancellationToken cancellationToken)
    {
        var supplier = await db.Suppliers.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == supplierId, cancellationToken);
        return supplier is null ? CostFlowProblems.SupplierNotFound() : TypedResults.Ok(SupplierView.From(supplier));
    }
}

/// <summary>One record of a supplier on any project, as the history needs it.</summary>
internal sealed record SupplierHistoryRow(
    int ProjectId,
    DateOnly ExpenseDate,
    string? ItemName,
    string? Unit,
    decimal? Quantity,
    ExpenseSettlementRow Settlement);

/// <summary>
/// <c>GET /api/suppliers/{id}/history</c>: the supplier's dealings with the company, project by project, for the
/// projects the caller may see. Every figure comes from the source records and the same settlement rule as the
/// project's supplier balances, so the history and each project's screens can't disagree; no balance is stored.
/// Amounts are <c>null</c> without projects See money; counts and delivered quantities are always shown.
/// </summary>
internal static class GetSupplierHistory
{
    internal sealed record Delivery(string? ItemName, string? Unit, decimal Quantity, decimal? Amount, int Count);

    internal sealed record Payment(int PaymentId, DateOnly Date, string Method, string? Reference, decimal? Amount);

    internal sealed record ProjectHistory(
        int ProjectId,
        string ProjectCode,
        string ProjectNameAr,
        string ProjectNameEn,
        decimal? CreditRecognized,
        decimal? CustodyRecognized,
        decimal? Recognized,
        decimal? Paid,
        decimal? Outstanding,
        int ApprovedRecords,
        int OpenInvoices,
        int PendingRecords,
        decimal? PendingAmount,
        int OpenPriceVariances,
        IReadOnlyList<Delivery> Deliveries,
        IReadOnlyList<Payment> Payments,
        int VoidedPayments);

    internal sealed record Response(
        SupplierView Supplier,
        IReadOnlyList<ProjectHistory> Projects,
        decimal? Recognized,
        decimal? Paid,
        decimal? Outstanding,
        int ProjectCount);

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/suppliers/{supplierId:int}/history", HandleAsync)
            .RequirePermission(ProjectPermissions.Module, PermissionAction.View)
            .WithName("GetSupplierHistory")
            .WithSummary("A supplier's history across the projects you can see: recognized, paid, outstanding, deliveries and payments per project");

    internal static async Task<Results<Ok<Response>, ProblemHttpResult>> HandleAsync(
        int supplierId,
        HttpContext httpContext,
        ProjectsDbContext db,
        IVisibleProjects visible,
        CancellationToken cancellationToken)
    {
        if (ProjectCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var supplier = await db.Suppliers.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == supplierId, cancellationToken);
        if (supplier is null)
        {
            return CostFlowProblems.SupplierNotFound();
        }

        var scope = await visible.IdsAsync(httpContext.User, cancellationToken);
        var seesMoney = caller.Can(PermissionAction.SeeMoney);

        var expenses = db.ProjectExpenses.AsNoTracking().Where(expense => expense.SupplierId == supplierId);
        var payments = db.SupplierPayments.AsNoTracking().Where(payment => payment.SupplierId == supplierId);
        var variances = db.PriceVariances.AsNoTracking()
            .Where(variance => variance.Expense.SupplierId == supplierId && variance.Status == PriceVarianceStatus.Open);
        if (scope is not null)
        {
            expenses = expenses.Where(expense => scope.Contains(expense.ProjectId));
            payments = payments.Where(payment => scope.Contains(payment.ProjectId));
            variances = variances.Where(variance => scope.Contains(variance.ProjectId));
        }

        var rows = await expenses
            .Select(expense => new SupplierHistoryRow(
                expense.ProjectId,
                expense.ExpenseDate,
                expense.ItemName,
                expense.Unit,
                expense.Quantity,
                new ExpenseSettlementRow(
                    expense.Id,
                    expense.Kind,
                    expense.Status,
                    expense.Channel,
                    expense.Category,
                    expense.SupplierId,
                    expense.ProjectItemId,
                    expense.Amount + (expense.Adjustments.Where(adjustment => adjustment.Status == AdjustmentStatus.Approved)
                        .Sum(adjustment => (decimal?)adjustment.Amount) ?? 0),
                    db.SupplierPaymentAllocations
                        .Where(allocation => allocation.ExpenseId == expense.Id && allocation.Payment.VoidedAt == null)
                        .Sum(allocation => allocation.Amount))))
            .ToListAsync(cancellationToken);

        var paymentRows = await payments
            .OrderBy(payment => payment.PaymentDate)
            .ThenBy(payment => payment.Id)
            .Select(payment => new { payment.Id, payment.ProjectId, payment.PaymentDate, payment.Method, payment.Reference, payment.Amount, payment.VoidedAt })
            .ToListAsync(cancellationToken);

        var openVariances = await variances.GroupBy(variance => variance.ProjectId)
            .Select(group => new { ProjectId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(group => group.ProjectId, group => group.Count, cancellationToken);

        var projectIds = rows.Select(row => row.ProjectId).Concat(paymentRows.Select(payment => payment.ProjectId)).Distinct().ToList();
        var projects = await db.Projects.AsNoTracking()
            .Where(project => projectIds.Contains(project.Id))
            .Select(project => new { project.Id, project.Code, project.NameAr, project.NameEn })
            .ToListAsync(cancellationToken);

        decimal? Money(decimal value) => seesMoney ? value : null;

        var history = projects
            .OrderBy(project => project.Code)
            .Select(project =>
            {
                var projectRows = rows.Where(row => row.ProjectId == project.Id).ToList();
                var balance = SupplierBalances.Compute(projectRows.Select(row => row.Settlement).ToList()).SingleOrDefault()
                    ?? new SupplierBalance(supplierId, 0, 0, 0, 0, 0);
                var approved = projectRows.Where(row => row.Settlement.Status == ExpenseStatus.Approved).ToList();
                var pending = projectRows.Where(row => row.Settlement.Status == ExpenseStatus.Recorded).ToList();
                var projectPayments = paymentRows.Where(payment => payment.ProjectId == project.Id).ToList();

                var deliveries = approved
                    .Where(row => row.Settlement.Kind == ExpenseKind.Supply)
                    .GroupBy(row => (row.ItemName, row.Unit))
                    .OrderBy(group => group.Key.ItemName)
                    .Select(group => new Delivery(
                        group.Key.ItemName, group.Key.Unit, group.Sum(row => row.Quantity ?? 0), Money(group.Sum(row => row.Settlement.Amount)), group.Count()))
                    .ToList();

                return new ProjectHistory(
                    project.Id,
                    project.Code,
                    project.NameAr,
                    project.NameEn,
                    Money(balance.CreditRecognized),
                    Money(balance.CustodyRecognized),
                    Money(balance.CreditRecognized + balance.CustodyRecognized),
                    Money(balance.Paid),
                    Money(balance.Outstanding),
                    approved.Count,
                    balance.OpenInvoices,
                    pending.Count,
                    Money(pending.Sum(row => row.Settlement.Amount)),
                    openVariances.GetValueOrDefault(project.Id),
                    deliveries,
                    seesMoney
                        ? projectPayments.Where(payment => payment.VoidedAt == null)
                            .Select(payment => new Payment(payment.Id, payment.PaymentDate, payment.Method.ToString(), payment.Reference, payment.Amount))
                            .ToList()
                        : [],
                    projectPayments.Count(payment => payment.VoidedAt != null));
            })
            .ToList();

        return TypedResults.Ok(new Response(
            SupplierView.From(supplier),
            history,
            seesMoney ? history.Sum(project => project.Recognized ?? 0) : null,
            seesMoney ? history.Sum(project => project.Paid ?? 0) : null,
            seesMoney ? history.Sum(project => project.Outstanding ?? 0) : null,
            history.Count));
    }
}
