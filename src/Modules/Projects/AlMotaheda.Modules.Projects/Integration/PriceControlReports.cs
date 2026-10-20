using System.Security.Claims;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Features.PriceControl;
using AlMotaheda.Modules.Reports.Contracts;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Integration;

/// <summary>
/// The Price Control reports: the cases (pending, approved once, approved with a price change, rejected), the catalog
/// changes (every approved range, with the case that caused it when there was one), and where the variances come
/// from (by project, category, supplier or submitter). They read the same rows as the Price Control screens.
/// </summary>
internal sealed class PriceControlReports(ProjectsDbContext db, IUserDirectory users, IVisibleProjects visibleProjects) : IReportSource
{
    public const string Exceptions = "PRICE-EXCEPTIONS";
    public const string Changes = "PRICE-CHANGES";
    public const string Variance = "PRICE-VARIANCE";

    private static readonly ReportPermission[] CatalogMoney = [new("catalog", ReportActions.SeeMoney)];
    private static readonly ReportFilter From = new("from", ReportFilterTypes.Date, false, "من تاريخ", "From");
    private static readonly ReportFilter To = new("to", ReportFilterTypes.Date, false, "إلى تاريخ", "To");
    private static readonly ReportFilter Project = new("projectId", ReportFilterTypes.Id, false, "المشروع", "Project");
    private static readonly ReportFilter Supplier = new("supplierId", ReportFilterTypes.Id, false, "المورد", "Supplier");
    private static readonly string[] GroupKeys = ["Project", "Category", "Supplier", "Submitter", "Item"];

    public IReadOnlyList<ReportDefinition> Reports { get; } =
    [
        new(Exceptions, "استثناءات الأسعار", "Price exceptions", ReportCategories.Projects, CatalogMoney,
            [From, To, Project, Supplier, new("status", ReportFilterTypes.Option, false, "الحالة", "Status", Enum.GetNames<PriceExceptionStatus>())],
            [
                new("date", "التاريخ", "Date", ReportColumnTypes.Date),
                new("project", "المشروع", "Project", ReportColumnTypes.Text),
                new("item", "الصنف", "Item", ReportColumnTypes.Text),
                new("category", "التصنيف", "Category", ReportColumnTypes.Text),
                new("supplier", "المورد", "Supplier", ReportColumnTypes.Text),
                new("unit", "الوحدة", "Unit", ReportColumnTypes.Text),
                new("quantity", "الكمية", "Quantity", ReportColumnTypes.Quantity),
                new("unitPrice", "السعر المسجل", "Entered price", ReportColumnTypes.Money),
                new("referenceMax", "الحد الأقصى", "Reference max", ReportColumnTypes.Money),
                new("variancePerUnit", "الفرق للوحدة", "Variance / unit", ReportColumnTypes.Money),
                new("varianceTotal", "إجمالي الفرق", "Variance total", ReportColumnTypes.Money, Total: true),
                new("variancePercent", "نسبة الفرق %", "Variance %", ReportColumnTypes.Quantity),
                new("status", "الحالة", "Status", ReportColumnTypes.Text),
                new("submittedBy", "سجّله", "Submitted by", ReportColumnTypes.Text),
                new("decidedBy", "قرار", "Decided by", ReportColumnTypes.Text),
                new("source", "المستند", "Record", ReportColumnTypes.Text),
            ]),
        new(Changes, "تغييرات الأسعار المرجعية", "Reference price changes", ReportCategories.Projects, CatalogMoney,
            [From, To, Supplier],
            [
                new("effectiveFrom", "ساري من", "Effective from", ReportColumnTypes.Date),
                new("item", "الصنف", "Item", ReportColumnTypes.Text),
                new("category", "التصنيف", "Category", ReportColumnTypes.Text),
                new("supplier", "المورد", "Supplier", ReportColumnTypes.Text),
                new("kind", "نوع القاعدة", "Rule", ReportColumnTypes.Text),
                new("minPrice", "الحد الأدنى", "Min", ReportColumnTypes.Money),
                new("maxPrice", "الحد الأقصى", "Max", ReportColumnTypes.Money),
                new("referencePrice", "السعر المرجعي", "Reference", ReportColumnTypes.Money),
                new("origin", "المصدر", "Origin", ReportColumnTypes.Text),
                new("requestedBy", "طلبه", "Requested by", ReportColumnTypes.Text),
                new("decidedBy", "اعتمده", "Approved by", ReportColumnTypes.Text),
                new("decidedAt", "تاريخ الاعتماد", "Approved on", ReportColumnTypes.Date),
                new("reason", "السبب", "Reason", ReportColumnTypes.Text),
            ]),
        new(Variance, "أكثر الفروق تكراراً", "Most frequent price variances", ReportCategories.Projects, CatalogMoney,
            [From, To, Project, new("groupBy", ReportFilterTypes.Option, false, "تجميع حسب", "Group by", GroupKeys)],
            [
                new("group", "المجموعة", "Group", ReportColumnTypes.Text),
                new("cases", "عدد الحالات", "Cases", ReportColumnTypes.Integer, Total: true),
                new("varianceTotal", "إجمالي الفرق", "Variance total", ReportColumnTypes.Money, Total: true),
                new("pending", "قيد القرار", "Pending", ReportColumnTypes.Integer, Total: true),
                new("approvedOnce", "استثناء لمرة", "One-off approvals", ReportColumnTypes.Integer, Total: true),
                new("approvedWithPriceChange", "تغيير السعر", "Price changes", ReportColumnTypes.Integer, Total: true),
                new("rejected", "مرفوض", "Rejected", ReportColumnTypes.Integer, Total: true),
            ]),
    ];

    public async Task<ReportData?> RunAsync(string code, ReportQuery query, ClaimsPrincipal caller, CancellationToken cancellationToken)
    {
        // The caller's projects only (the Executive and the accountants see them all).
        var visible = await visibleProjects.IdsAsync(caller, cancellationToken);
        if (query.Id("projectId") is { } projectId && visible is not null && !visible.Contains(projectId))
        {
            return null;
        }

        return code switch
        {
            Exceptions => await ExceptionsAsync(query, visible, cancellationToken),
            Changes => await ChangesAsync(query, cancellationToken),
            Variance => await VarianceAsync(query, visible, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Not a Price Control report."),
        };
    }

    private IQueryable<PriceException> Scope(ReportQuery query, IReadOnlySet<int>? visible)
    {
        var exceptions = db.PriceExceptions.AsNoTracking();
        if (visible is not null)
        {
            exceptions = exceptions.Where(exception => visible.Contains(exception.ProjectId));
        }

        if (query.Id("projectId") is { } projectId)
        {
            exceptions = exceptions.Where(exception => exception.ProjectId == projectId);
        }

        if (query.Id("supplierId") is { } supplierId)
        {
            exceptions = exceptions.Where(exception => exception.SupplierId == supplierId);
        }

        if (query.Date("from") is { } from)
        {
            exceptions = exceptions.Where(exception => exception.TransactionDate >= from);
        }

        if (query.Date("to") is { } to)
        {
            exceptions = exceptions.Where(exception => exception.TransactionDate <= to);
        }

        return exceptions;
    }

    private async Task<ReportData> ExceptionsAsync(ReportQuery query, IReadOnlySet<int>? visible, CancellationToken cancellationToken)
    {
        var exceptions = Scope(query, visible);
        if (query.Text("status") is { } status && Enum.TryParse<PriceExceptionStatus>(status, out var wanted))
        {
            exceptions = exceptions.Where(exception => exception.Status == wanted);
        }

        var views = await PriceExceptionQueries.ViewsAsync(db, users, exceptions.OrderBy(exception => exception.TransactionDate).ThenBy(exception => exception.Id), cancellationToken);
        return new ReportData(views.Select(view => new ReportRow(new Dictionary<string, object?>
        {
            ["date"] = view.TransactionDate,
            ["project"] = view.ProjectNameEn,
            ["item"] = view.PriceItemNameEn,
            ["category"] = view.CategoryCode,
            ["supplier"] = view.SupplierNameEn,
            ["unit"] = view.Unit,
            ["quantity"] = view.Quantity,
            ["unitPrice"] = view.UnitPrice,
            ["referenceMax"] = view.ReferenceMaxPrice,
            ["variancePerUnit"] = view.VariancePerUnit,
            ["varianceTotal"] = view.VarianceTotal,
            ["variancePercent"] = view.VariancePercent,
            ["status"] = view.Status,
            ["submittedBy"] = view.SubmittedByNameEn,
            ["decidedBy"] = view.DecidedByNameEn,
            ["source"] = view.Summary,
        }, new ReportLink("priceException", view.Id, view.ProjectId))).ToList());
    }

    private async Task<ReportData> ChangesAsync(ReportQuery query, CancellationToken cancellationToken)
    {
        var ranges = db.PriceRanges.AsNoTracking().Where(range => range.Status == PriceRangeStatus.Approved);
        if (query.Id("supplierId") is { } supplierId)
        {
            ranges = ranges.Where(range => range.SupplierId == supplierId);
        }

        if (query.Date("from") is { } from)
        {
            ranges = ranges.Where(range => range.EffectiveFrom >= from);
        }

        if (query.Date("to") is { } to)
        {
            ranges = ranges.Where(range => range.EffectiveFrom <= to);
        }

        var rows = await ranges
            .OrderByDescending(range => range.EffectiveFrom).ThenByDescending(range => range.Id)
            .Select(range => new
            {
                Range = range,
                ItemName = range.PriceItem.NameEn,
                Category = range.PriceItem.Category.Code,
                SupplierName = range.Supplier == null ? null : range.Supplier.NameEn,
                // A range created by the Executive's decision B is a permanent change born from one transaction.
                FromException = db.PriceExceptions.Where(exception => exception.NewRangeId == range.Id).Select(exception => (int?)exception.Id).FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);
        var people = await users.FindAsync(rows.SelectMany(row => new[] { row.Range.RequestedBy, row.Range.DecidedBy ?? row.Range.RequestedBy }).Distinct().ToList(), cancellationToken);

        return new ReportData(rows.Select(row => new ReportRow(new Dictionary<string, object?>
        {
            ["effectiveFrom"] = row.Range.EffectiveFrom,
            ["item"] = row.ItemName,
            ["category"] = row.Category,
            ["supplier"] = row.SupplierName ?? "All suppliers",
            ["kind"] = row.Range.Kind.ToString(),
            ["minPrice"] = row.Range.MinPrice,
            ["maxPrice"] = row.Range.MaxPrice,
            ["referencePrice"] = row.Range.ReferencePrice,
            ["origin"] = row.FromException is { } exceptionId ? $"Price exception #{exceptionId}" : "Price list request",
            ["requestedBy"] = people.GetValueOrDefault(row.Range.RequestedBy)?.NameEn,
            ["decidedBy"] = row.Range.DecidedBy is { } decidedBy ? people.GetValueOrDefault(decidedBy)?.NameEn : null,
            ["decidedAt"] = row.Range.DecidedAt is { } decidedAt ? DateOnly.FromDateTime(decidedAt) : null,
            ["reason"] = row.Range.Reason ?? row.Range.DecisionNote,
        }, new ReportLink("priceItem", row.Range.PriceItemId))).ToList());
    }

    private async Task<ReportData> VarianceAsync(ReportQuery query, IReadOnlySet<int>? visible, CancellationToken cancellationToken)
    {
        var groupBy = query.Text("groupBy") ?? "Item";
        var rows = await PriceExceptionQueries.Rows(db, Scope(query, visible)).ToListAsync(cancellationToken);
        var people = groupBy == "Submitter"
            ? await users.FindAsync(rows.Select(row => row.Exception.SubmittedBy).Distinct().ToList(), cancellationToken)
            : new Dictionary<int, UserSummary>();

        var groups = rows
            .GroupBy(row => groupBy switch
            {
                "Project" => (Key: row.Exception.ProjectId, Label: row.ProjectNameEn ?? row.ProjectCode, Link: new ReportLink("project", row.Exception.ProjectId, row.Exception.ProjectId)),
                "Category" => (Key: 0, Label: row.CategoryCode, Link: (ReportLink?)null),
                "Supplier" => (Key: row.Exception.SupplierId ?? 0, Label: row.SupplierNameEn ?? "No supplier", Link: null),
                "Submitter" => (Key: row.Exception.SubmittedBy, Label: people.GetValueOrDefault(row.Exception.SubmittedBy)?.NameEn ?? $"User {row.Exception.SubmittedBy}", Link: null),
                _ => (Key: row.Exception.PriceItemId, Label: row.PriceItemNameEn, Link: new ReportLink("priceItem", row.Exception.PriceItemId)),
            })
            .Select(group => new ReportRow(new Dictionary<string, object?>
            {
                ["group"] = group.Key.Label,
                ["cases"] = group.Count(),
                ["varianceTotal"] = group.Sum(row => row.Exception.VarianceTotal),
                ["pending"] = group.Count(row => row.Exception.Status == PriceExceptionStatus.Pending),
                ["approvedOnce"] = group.Count(row => row.Exception.Status == PriceExceptionStatus.ApprovedOnce),
                ["approvedWithPriceChange"] = group.Count(row => row.Exception.Status == PriceExceptionStatus.ApprovedWithPriceChange),
                ["rejected"] = group.Count(row => row.Exception.Status == PriceExceptionStatus.Rejected),
            }, group.Key.Link))
            .OrderByDescending(row => (int)row.Cells["cases"]!).ThenByDescending(row => (decimal)row.Cells["varianceTotal"]!)
            .ToList();
        return new ReportData(groups, "A withdrawn case (its record was changed before a decision) is counted in none of the decision columns.");
    }
}
