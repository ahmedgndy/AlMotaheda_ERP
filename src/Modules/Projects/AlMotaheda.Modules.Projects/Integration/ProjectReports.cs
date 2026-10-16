using System.Security.Claims;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Features.Expenses;
using AlMotaheda.Modules.Projects.Features.Items;
using AlMotaheda.Modules.Projects.Features.Reports;
using AlMotaheda.Modules.Projects.Features.SupplierPayments;
using AlMotaheda.Modules.Reports.Contracts;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Integration;

/// <summary>
/// The project cost and supplier reports, computed by the same code as the project screens: the cost loader of the
/// overview and cost summary, the items ledger of the items screen, and the supplier balances.
/// </summary>
internal sealed class ProjectReports(
    ProjectsDbContext db,
    IProjectAccess access,
    IVisibleProjects visibleProjects,
    ProjectCostLoader costs,
    ItemsLedgerSource items) : IReportSource
{
    public const string CostSummary = "PRJ-COST";
    public const string ItemBreakdown = "PRJ-ITEMS";
    public const string SupplierAccount = "SUP-ACCOUNT";

    private static readonly ReportPermission[] ProjectMoney = [new("projects", ReportActions.SeeMoney)];
    private static readonly ReportFilter RequiredProject = new("projectId", ReportFilterTypes.Id, true, "المشروع", "Project");

    public IReadOnlyList<ReportDefinition> Reports { get; } =
    [
        new(CostSummary, "ملخص تكلفة المشروع", "Project cost summary", ReportCategories.Projects, ProjectMoney,
            [RequiredProject, new("from", ReportFilterTypes.Date, false, "من تاريخ", "From"), new("to", ReportFilterTypes.Date, false, "إلى تاريخ", "To")],
            [
                new("costGroup", "مجموعة التكلفة", "Cost group", ReportColumnTypes.Text),
                new("recognized", "التكلفة المعتمدة", "Recognized", ReportColumnTypes.Money, Total: true),
                new("paid", "المدفوع", "Paid", ReportColumnTypes.Money, Total: true),
                new("outstanding", "المتبقي", "Outstanding", ReportColumnTypes.Money, Total: true),
                new("pendingReview", "قيد المراجعة", "Pending review", ReportColumnTypes.Money, Total: true),
                new("committed", "الالتزامات", "Committed", ReportColumnTypes.Money, Total: true),
                new("records", "عدد السجلات", "Records", ReportColumnTypes.Integer, Total: true),
            ]),
        new(ItemBreakdown, "تفصيل تكلفة المشروع حسب البند", "Project cost breakdown by item", ReportCategories.Projects, ProjectMoney,
            [RequiredProject],
            [
                new("item", "البند", "Item", ReportColumnTypes.Text),
                new("category", "التصنيف", "Category", ReportColumnTypes.Text),
                new("supplierCost", "الموردون", "Suppliers", ReportColumnTypes.Money, Total: true),
                new("laborCost", "العمالة الداخلية", "Internal labor", ReportColumnTypes.Money, Total: true),
                new("subcontractCost", "مقاولو الباطن", "Subcontractors", ReportColumnTypes.Money, Total: true),
                new("totalCost", "الإجمالي", "Total", ReportColumnTypes.Money, Total: true),
                new("paid", "المدفوع", "Paid", ReportColumnTypes.Money, Total: true),
                new("outstanding", "المتبقي", "Outstanding", ReportColumnTypes.Money, Total: true),
            ]),
        new(SupplierAccount, "حساب الموردين ومدفوعاتهم", "Supplier account and payments", ReportCategories.Parties, ProjectMoney,
            [new("projectId", ReportFilterTypes.Id, false, "المشروع", "Project"), new("supplierId", ReportFilterTypes.Id, false, "المورد", "Supplier")],
            [
                new("project", "المشروع", "Project", ReportColumnTypes.Text),
                new("supplier", "المورد", "Supplier", ReportColumnTypes.Text),
                new("creditRecognized", "مشتريات آجلة", "On credit", ReportColumnTypes.Money, Total: true),
                new("custodyRecognized", "من العهدة", "From custody", ReportColumnTypes.Money, Total: true),
                new("paid", "المدفوع", "Paid", ReportColumnTypes.Money, Total: true),
                new("outstanding", "المتبقي", "Outstanding", ReportColumnTypes.Money, Total: true),
                new("openInvoices", "فواتير مفتوحة", "Open invoices", ReportColumnTypes.Integer, Total: true),
            ]),
    ];

    public async Task<ReportData?> RunAsync(string code, ReportQuery query, ClaimsPrincipal caller, CancellationToken cancellationToken)
    {
        if (query.Id("projectId") is { } projectId && await access.FindVisibleAsync(caller, projectId, cancellationToken) is null)
        {
            return null;
        }

        return code switch
        {
            CostSummary => await CostSummaryAsync(query, cancellationToken),
            ItemBreakdown => await ItemsAsync(query, cancellationToken),
            SupplierAccount => await SuppliersAsync(query, caller, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Not a Projects report."),
        };
    }

    private async Task<ReportData> CostSummaryAsync(ReportQuery query, CancellationToken cancellationToken)
    {
        var projectId = query.Id("projectId")!.Value;
        var data = await costs.LoadAsync(projectId, query.Date("from"), query.Date("to"), cancellationToken);
        return new ReportData(data.Groups.Select(group => new ReportRow(new Dictionary<string, object?>
        {
            ["costGroup"] = group.CostGroup,
            ["recognized"] = group.Recognized,
            ["paid"] = group.Paid,
            ["outstanding"] = group.Outstanding,
            ["pendingReview"] = group.PendingReview,
            ["committed"] = group.Committed,
            ["records"] = group.RecognizedRecords,
        }, new ReportLink("project", projectId, projectId))).ToList());
    }

    private async Task<ReportData> ItemsAsync(ReportQuery query, CancellationToken cancellationToken)
    {
        var projectId = query.Id("projectId")!.Value;
        var data = await items.LoadAsync(projectId, cancellationToken);
        var rows = data.Items
            .Select(item => (Name: item.NameEn, item.Category, Id: (int?)item.Id, Figures: data.Ledger.Of(item.Id)))
            .Append(("Not linked to an item", "-", null, data.Ledger.Unallocated))
            .Where(row => row.Figures.RecordCount > 0)
            .Select(row => new ReportRow(new Dictionary<string, object?>
            {
                ["item"] = row.Name,
                ["category"] = row.Category,
                ["supplierCost"] = row.Figures.SupplierCost,
                ["laborCost"] = row.Figures.LaborCost,
                ["subcontractCost"] = row.Figures.SubcontractorCost,
                ["totalCost"] = row.Figures.TotalCost,
                ["paid"] = row.Figures.Paid,
                ["outstanding"] = row.Figures.Outstanding,
            }, new ReportLink("projectItem", row.Id, projectId)))
            .ToList();
        return new ReportData(rows, "Equipment and site expenses without an item are not split over items; see the project cost summary.");
    }

    private async Task<ReportData> SuppliersAsync(ReportQuery query, ClaimsPrincipal caller, CancellationToken cancellationToken)
    {
        IReadOnlyCollection<int> projectIds;
        if (query.Id("projectId") is { } projectId)
        {
            projectIds = [projectId];
        }
        else
        {
            var visible = await visibleProjects.IdsAsync(caller, cancellationToken);
            projectIds = visible is not null ? visible.ToList() : await db.Projects.AsNoTracking().Select(project => project.Id).ToListAsync(cancellationToken);
        }

        var wantedSupplier = query.Id("supplierId");
        var balances = new List<(int ProjectId, SupplierBalance Balance)>();
        foreach (var id in projectIds)
        {
            var rows = await ExpenseQueries.SettlementRows(db, id).ToListAsync(cancellationToken);
            balances.AddRange(SupplierBalances.Compute(rows)
                .Where(balance => wantedSupplier is null || balance.SupplierId == wantedSupplier)
                .Select(balance => (id, balance)));
        }

        var supplierIds = balances.Select(entry => entry.Balance.SupplierId).Distinct().ToList();
        var projectIdsUsed = balances.Select(entry => entry.ProjectId).Distinct().ToList();
        var suppliers = await db.Suppliers.AsNoTracking().Where(supplier => supplierIds.Contains(supplier.Id)).ToDictionaryAsync(supplier => supplier.Id, supplier => supplier.NameEn, cancellationToken);
        var projects = await db.Projects.AsNoTracking().Where(project => projectIdsUsed.Contains(project.Id)).ToDictionaryAsync(project => project.Id, project => project.Code, cancellationToken);

        return new ReportData(balances
            .OrderBy(entry => suppliers[entry.Balance.SupplierId]).ThenBy(entry => projects[entry.ProjectId])
            .Select(entry => new ReportRow(new Dictionary<string, object?>
            {
                ["project"] = projects[entry.ProjectId],
                ["supplier"] = suppliers[entry.Balance.SupplierId],
                ["creditRecognized"] = entry.Balance.CreditRecognized,
                ["custodyRecognized"] = entry.Balance.CustodyRecognized,
                ["paid"] = entry.Balance.Paid,
                ["outstanding"] = entry.Balance.Outstanding,
                ["openInvoices"] = entry.Balance.OpenInvoices,
            }, new ReportLink("supplierBalances", entry.Balance.SupplierId, entry.ProjectId))).ToList());
    }
}
