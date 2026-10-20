using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.Modules.Procurement.Contracts;
using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Features.Suppliers;

/// <summary>What one supplier supplied for one work item of a project.</summary>
internal sealed record SupplierWorkItemView(
    int? ProjectItemId, string? ProjectItemNameAr, string? ProjectItemNameEn, int? ParentItemId, SupplierFigures Figures);

/// <summary>One material a supplier delivered to a project, in one unit.</summary>
/// <param name="Quantity">Approved quantity.</param>
/// <param name="PendingQuantity">Recorded, waiting for review.</param>
internal sealed record SupplierMaterialView(string? MaterialName, string? Unit, decimal Quantity, decimal PendingQuantity, decimal Recognized);

/// <summary>One supplier on one project: its figures, its agreements, its purchase orders, per work item and per material.</summary>
internal sealed record ProjectSupplierView(
    int SupplierId,
    string? NameAr,
    string? NameEn,
    string? Kind,
    string? Status,
    SupplierFigures Figures,
    IReadOnlyList<SupplyAgreementView> Agreements,
    IReadOnlyList<SupplierOrderLine> Orders,
    IReadOnlyList<SupplierWorkItemView> WorkItems,
    IReadOnlyList<SupplierMaterialView> Materials);

/// <summary>A supplier's figures on one project, for its company account.</summary>
internal sealed record SupplierProjectTotals(int ProjectId, string? ProjectCode, string? ProjectNameAr, string? ProjectNameEn, SupplierFigures Figures);

/// <summary>One line of a supplier's ledger across projects.</summary>
/// <param name="Debit">What the company became owed (an approved record, a voided payment taken back).</param>
/// <param name="Credit">What it paid (a payment, or the cash paid on site for a custody purchase).</param>
/// <param name="Balance">Running: approved credit − payments: what is owed now.</param>
/// <param name="Commitment">An order line's value: ordered, not owed yet. Never in the balance.</param>
internal sealed record SupplierLedgerEntry(
    DateOnly Date,
    string Type,
    int ProjectId,
    string? ProjectCode,
    string? ProjectNameAr,
    string? ProjectNameEn,
    int? ProjectItemId,
    string? ProjectItemNameAr,
    string? ProjectItemNameEn,
    string? Description,
    decimal? Quantity,
    string? Unit,
    decimal? Commitment,
    decimal Debit,
    decimal Credit,
    decimal Balance,
    string SourceType,
    int SourceId,
    int? RecordedBy);

/// <summary>
/// The supplier screens with money: a project's suppliers (the workspace's "شركات التوريد" section) and one supplier's
/// company account across the projects the caller may see. projects See money. Every figure comes from
/// <see cref="SupplierFigures"/>, so the project page, the company page and the work-item breakdown can't disagree.
/// </summary>
internal static class SupplierAccounts
{
    public static void Map(IEndpointRouteBuilder api, IEndpointRouteBuilder projects)
    {
        projects.MapGet("/{projectId:int}/suppliers", ProjectSuppliersAsync)
            .RequirePermission(ProjectPermissions.Module, PermissionAction.SeeMoney)
            .WithName("ListProjectSuppliers")
            .WithSummary("The suppliers of a project: agreements, purchase orders, delivered, invoiced, paid, outstanding and pending, per work item and material");

        api.MapGet("/suppliers/{supplierId:int}/account", AccountAsync)
            .RequirePermission(ProjectPermissions.Module, PermissionAction.SeeMoney)
            .WithName("GetSupplierAccount")
            .WithSummary("A supplier's account across every project you can see: totals per project and the chronological ledger (projectId, page, pageSize)");
    }

    internal sealed record ProjectResponse(IReadOnlyList<ProjectSupplierView> Items, SupplierFigures Totals);

    internal static async Task<Results<Ok<ProjectResponse>, ProblemHttpResult>> ProjectSuppliersAsync(
        int projectId,
        HttpContext httpContext,
        ProjectsDbContext db,
        IProjectAccess access,
        ISupplierOrders orders,
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

        var records = await SupplierLedger.Records(db, projectId, null).ToListAsync(cancellationToken);
        var agreements = await db.SupplyAgreements.AsNoTracking().Where(agreement => agreement.ProjectId == projectId).ToListAsync(cancellationToken);
        var lines = await orders.LinesAsync(projectId, null, cancellationToken);
        var agreementViews = await SupplyAgreements.ViewsAsync(db, projectId, agreements, seesMoney: true, cancellationToken);

        var supplierIds = records.Select(record => record.SupplierId)
            .Concat(agreements.Select(agreement => agreement.SupplierId))
            .Concat(lines.Select(line => line.SupplierId))
            .Distinct()
            .ToList();
        var suppliers = await db.Suppliers.AsNoTracking().Where(supplier => supplierIds.Contains(supplier.Id)).ToDictionaryAsync(supplier => supplier.Id, cancellationToken);
        var itemIds = records.Select(record => record.ProjectItemId).Concat(lines.Select(line => line.ProjectItemId)).OfType<int>().Distinct().ToList();
        var items = await db.ProjectItems.AsNoTracking().Where(item => item.ProjectId == projectId && itemIds.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, cancellationToken);

        var views = supplierIds
            .Select(supplierId =>
            {
                var own = records.Where(record => record.SupplierId == supplierId).ToList();
                var ownLines = lines.Where(line => line.SupplierId == supplierId).ToList();
                var supplier = suppliers.GetValueOrDefault(supplierId);
                var workItems = own.Select(record => record.ProjectItemId).Concat(ownLines.Select(line => line.ProjectItemId)).Distinct()
                    .Select(itemId =>
                    {
                        var item = itemId is { } id ? items.GetValueOrDefault(id) : null;
                        return new SupplierWorkItemView(
                            itemId, item?.NameAr, item?.NameEn, item?.ParentItemId,
                            SupplierFigures.Of(own.Where(record => record.ProjectItemId == itemId).ToList(), ownLines.Where(line => line.ProjectItemId == itemId).ToList()));
                    })
                    .OrderBy(view => view.ProjectItemId is null)
                    .ThenBy(view => view.ProjectItemNameEn)
                    .ToList();
                var materials = own.Where(record => record.Kind == ExpenseKind.Supply)
                    .GroupBy(record => (record.ItemName, record.Unit))
                    .Select(group => new SupplierMaterialView(
                        group.Key.ItemName, group.Key.Unit,
                        group.Where(record => record.Status == ExpenseStatus.Approved).Sum(record => record.Quantity ?? 0),
                        group.Where(record => record.IsPending).Sum(record => record.Quantity ?? 0),
                        group.Where(record => record.Status == ExpenseStatus.Approved).Sum(record => record.Amount)))
                    .OrderBy(view => view.MaterialName)
                    .ToList();
                return new ProjectSupplierView(
                    supplierId, supplier?.NameAr, supplier?.NameEn, supplier?.Kind.ToString(), supplier?.Status.ToString(), SupplierFigures.Of(own, ownLines),
                    agreementViews.Where(view => view.SupplierId == supplierId).ToList(), ownLines, workItems, materials);
            })
            .OrderByDescending(view => view.Figures.Outstanding)
            .ThenBy(view => view.NameEn)
            .ToList();

        return TypedResults.Ok(new ProjectResponse(views, SupplierFigures.Of(records, lines)));
    }

    internal sealed record AccountResponse(
        int SupplierId,
        string NameAr,
        string NameEn,
        string Kind,
        string Status,
        SupplierFigures Totals,
        IReadOnlyList<SupplierProjectTotals> Projects,
        IReadOnlyList<SupplierLedgerEntry> Ledger,
        int Page,
        int PageSize,
        int TotalCount);

    /// <summary>
    /// One supplier across the projects the caller may see (an Executive or an Accountant sees all). A project the
    /// caller can't see is left out of every figure. Ledger newest first; the running balance runs oldest first.
    /// </summary>
    internal static async Task<Results<Ok<AccountResponse>, ValidationProblem, ProblemHttpResult>> AccountAsync(
        int supplierId,
        HttpContext httpContext,
        ProjectsDbContext db,
        IVisibleProjects visibleProjects,
        ISupplierOrders orders,
        CancellationToken cancellationToken,
        int? projectId = null,
        int page = 1,
        int pageSize = 50)
    {
        var errors = new ValidationErrors();
        Paging.Validate(page, pageSize, errors);
        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        var supplier = await db.Suppliers.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == supplierId, cancellationToken);
        if (supplier is null)
        {
            return CoreProblems.SupplierNotFound();
        }

        var visible = await visibleProjects.IdsAsync(httpContext.User, cancellationToken);
        bool Shown(int id) => (visible is null || visible.Contains(id)) && (projectId is null || id == projectId);

        var records = (await SupplierLedger.Records(db, null, supplierId).ToListAsync(cancellationToken)).Where(record => Shown(record.ProjectId)).ToList();
        var payments = (await SupplierLedger.Payments(db, null, supplierId).ToListAsync(cancellationToken)).Where(payment => Shown(payment.ProjectId)).ToList();
        var lines = (await orders.LinesAsync(null, supplierId, cancellationToken)).Where(line => Shown(line.ProjectId)).ToList();

        var projectIds = records.Select(record => record.ProjectId).Concat(payments.Select(payment => payment.ProjectId))
            .Concat(lines.Select(line => line.ProjectId)).Distinct().ToList();
        var projects = await db.Projects.AsNoTracking().Where(project => projectIds.Contains(project.Id))
            .Select(project => new { project.Id, project.Code, project.NameAr, project.NameEn })
            .ToDictionaryAsync(project => project.Id, cancellationToken);
        var itemIds = records.Select(record => record.ProjectItemId).Concat(lines.Select(line => line.ProjectItemId)).OfType<int>().Distinct().ToList();
        var items = await db.ProjectItems.AsNoTracking().Where(item => itemIds.Contains(item.Id))
            .Select(item => new { item.Id, item.NameAr, item.NameEn })
            .ToDictionaryAsync(item => item.Id, cancellationToken);

        var perProject = projectIds
            .Select(id =>
            {
                var project = projects.GetValueOrDefault(id);
                return new SupplierProjectTotals(
                    id, project?.Code, project?.NameAr, project?.NameEn,
                    SupplierFigures.Of(records.Where(record => record.ProjectId == id).ToList(), lines.Where(line => line.ProjectId == id).ToList()));
            })
            .OrderBy(totals => totals.ProjectCode)
            .ToList();

        var ledger = SupplierLedger.Run(SupplierLedger.Facts(records, payments, lines))
            .Select(line =>
            {
                var project = projects.GetValueOrDefault(line.Fact.ProjectId);
                var item = line.Fact.ProjectItemId is { } itemId ? items.GetValueOrDefault(itemId) : null;
                return new SupplierLedgerEntry(
                    line.Fact.Date, line.Fact.Type, line.Fact.ProjectId, project?.Code, project?.NameAr, project?.NameEn, line.Fact.ProjectItemId,
                    item?.NameAr, item?.NameEn, line.Fact.Description, line.Fact.Quantity, line.Fact.Unit, line.Fact.Commitment, line.Fact.Debit,
                    line.Fact.Credit, line.Balance, line.Fact.SourceType, line.Fact.SourceId, line.Fact.RecordedBy);
            })
            .Reverse()
            .ToList();

        return TypedResults.Ok(new AccountResponse(
            supplier.Id, supplier.NameAr, supplier.NameEn, supplier.Kind.ToString(), supplier.Status.ToString(), SupplierFigures.Of(records, lines), perProject,
            ledger.Skip((page - 1) * pageSize).Take(pageSize).ToList(), page, pageSize, ledger.Count));
    }
}
