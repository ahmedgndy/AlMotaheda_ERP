using System.Security.Claims;
using AlMotaheda.Modules.Fleet.Data;
using AlMotaheda.Modules.Fleet.Features.Costs;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Reports.Contracts;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Fleet.Integration;

/// <summary>
/// The equipment cost and fuel reports, from the same settlement rows (corrections included) and rules as the
/// equipment cost screens. Only approved costs count.
/// </summary>
internal sealed class FleetReports(FleetDbContext db, IVisibleProjects projects, IProjectAccess access) : IReportSource
{
    public const string EquipmentCost = "EQP-COST";
    public const string Fuel = "EQP-FUEL";

    private static readonly ReportPermission[] EquipmentMoney = [new("equipment", ReportActions.SeeMoney)];

    private static readonly ReportFilter[] Filters =
    [
        new("from", ReportFilterTypes.Date, false, "من تاريخ", "From"),
        new("to", ReportFilterTypes.Date, false, "إلى تاريخ", "To"),
        new("projectId", ReportFilterTypes.Id, false, "المشروع", "Project"),
        new("equipmentId", ReportFilterTypes.Id, false, "المعدة", "Equipment"),
    ];

    public IReadOnlyList<ReportDefinition> Reports { get; } =
    [
        new(EquipmentCost, "تكلفة المعدات (إيجار، وقود، صيانة)", "Equipment cost (rental, fuel, maintenance)", ReportCategories.Equipment, EquipmentMoney, Filters,
            [
                new("projectId", "المشروع", "Project", ReportColumnTypes.Integer),
                new("equipment", "المعدة", "Equipment", ReportColumnTypes.Text),
                new("rental", "إيجار", "Rental", ReportColumnTypes.Money, Total: true),
                new("fuel", "وقود", "Fuel", ReportColumnTypes.Money, Total: true),
                new("maintenance", "صيانة وإصلاح", "Maintenance and repair", ReportColumnTypes.Money, Total: true),
                new("other", "نقل وأخرى", "Transport and other", ReportColumnTypes.Money, Total: true),
                new("total", "الإجمالي", "Total", ReportColumnTypes.Money, Total: true),
                new("paid", "المدفوع", "Paid", ReportColumnTypes.Money, Total: true),
                new("outstanding", "المتبقي", "Outstanding", ReportColumnTypes.Money, Total: true),
            ]),
        new(Fuel, "الوقود حسب المعدة والمشروع", "Fuel by equipment and project", ReportCategories.Equipment, EquipmentMoney, Filters,
            [
                new("projectId", "المشروع", "Project", ReportColumnTypes.Integer),
                new("equipment", "المعدة", "Equipment", ReportColumnTypes.Text),
                new("fills", "مرات التزود", "Fills", ReportColumnTypes.Integer, Total: true),
                new("liters", "اللترات", "Litres", ReportColumnTypes.Quantity, Total: true),
                new("cost", "التكلفة", "Cost", ReportColumnTypes.Money, Total: true),
            ]),
    ];

    public async Task<ReportData?> RunAsync(string code, ReportQuery query, ClaimsPrincipal caller, CancellationToken cancellationToken)
    {
        IReadOnlyCollection<int> projectIds;
        if (query.Id("projectId") is { } projectId)
        {
            if (await access.FindVisibleAsync(caller, projectId, cancellationToken) is null)
            {
                return null;
            }

            projectIds = [projectId];
        }
        else
        {
            var visible = await projects.IdsAsync(caller, cancellationToken);
            projectIds = visible is not null
                ? visible.ToList()
                : await db.EquipmentCosts.AsNoTracking().Select(cost => cost.ProjectId).Distinct().ToListAsync(cancellationToken);
        }

        var equipmentId = query.Id("equipmentId");
        var rows = new List<(int ProjectId, EquipmentCostSettlementRow Row)>();
        foreach (var id in projectIds.Order())
        {
            var settled = await EquipmentCostQueries.SettlementRows(db, id, query.Date("from"), query.Date("to"))
                .Where(row => row.Status == EquipmentCostStatus.Approved && (equipmentId == null || row.EquipmentId == equipmentId))
                .ToListAsync(cancellationToken);
            rows.AddRange(settled.Select(row => (id, row)));
        }

        var equipmentIds = rows.Select(entry => entry.Row.EquipmentId).Distinct().ToList();
        var names = await db.Equipment.AsNoTracking().Where(equipment => equipmentIds.Contains(equipment.Id))
            .ToDictionaryAsync(equipment => equipment.Id, equipment => $"{equipment.AssetNo} {equipment.NameEn}", cancellationToken);

        var groups = rows.GroupBy(entry => (entry.ProjectId, entry.Row.EquipmentId)).OrderBy(group => group.Key.ProjectId).ThenBy(group => names[group.Key.EquipmentId]);
        return code switch
        {
            EquipmentCost => new ReportData(groups.Select(group =>
            {
                decimal Of(params EquipmentCostType[] types) => group.Where(entry => types.Contains(entry.Row.Type)).Sum(entry => entry.Row.Amount);
                var total = group.Sum(entry => EquipmentCostSettlement.Recognized(entry.Row));
                var paid = group.Sum(entry => EquipmentCostSettlement.Paid(entry.Row));
                return new ReportRow(new Dictionary<string, object?>
                {
                    ["projectId"] = group.Key.ProjectId,
                    ["equipment"] = names[group.Key.EquipmentId],
                    ["rental"] = Of(EquipmentCostType.Rental),
                    ["fuel"] = Of(EquipmentCostType.Fuel),
                    ["maintenance"] = Of(EquipmentCostType.Maintenance, EquipmentCostType.Repair),
                    ["other"] = Of(EquipmentCostType.Transport, EquipmentCostType.Other),
                    ["total"] = total,
                    ["paid"] = paid,
                    ["outstanding"] = total - paid,
                }, new ReportLink("equipment", group.Key.EquipmentId, group.Key.ProjectId));
            }).ToList()),
            Fuel => new ReportData(groups
                .Where(group => group.Any(entry => entry.Row.Type == EquipmentCostType.Fuel))
                .Select(group =>
                {
                    var fuel = group.Where(entry => entry.Row.Type == EquipmentCostType.Fuel).ToList();
                    return new ReportRow(new Dictionary<string, object?>
                    {
                        ["projectId"] = group.Key.ProjectId,
                        ["equipment"] = names[group.Key.EquipmentId],
                        ["fills"] = fuel.Count,
                        ["liters"] = fuel.Sum(entry => entry.Row.Liters ?? 0),
                        ["cost"] = fuel.Sum(entry => entry.Row.Amount),
                    }, new ReportLink("equipment", group.Key.EquipmentId, group.Key.ProjectId));
                }).ToList(),
                "Fuel by driver is not available: fuel records don't name a driver yet."),
            _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Not a Fleet report."),
        };
    }
}
