using AlMotaheda.Modules.Fleet.Data;

namespace AlMotaheda.Modules.Fleet.Features.Costs;

/// <summary>Recognized cost split into the four columns of the equipment screens.</summary>
internal sealed record EquipmentCostBreakdown(decimal Rental, decimal Fuel, decimal Maintenance, decimal Other)
{
    public static EquipmentCostBreakdown Zero { get; } = new(0, 0, 0, 0);

    public decimal Total => Rental + Fuel + Maintenance + Other;

    public EquipmentCostBreakdown Add(CostGroup group, decimal amount) => group switch
    {
        CostGroup.Rental => this with { Rental = Rental + amount },
        CostGroup.Fuel => this with { Fuel = Fuel + amount },
        CostGroup.Maintenance => this with { Maintenance = Maintenance + amount },
        _ => this with { Other = Other + amount },
    };
}

/// <summary>One machine's approved costs on the project.</summary>
/// <param name="Liters">Approved fuel litres.</param>
internal sealed record MachineCostFigures(
    int EquipmentId,
    EquipmentCostBreakdown ToDate,
    decimal ThisMonth,
    decimal Liters,
    decimal Paid,
    decimal Outstanding);

/// <summary>The project's equipment costs: per machine and in total. Only approved costs are in the money columns.</summary>
internal sealed record ProjectEquipmentFigures(
    IReadOnlyList<MachineCostFigures> Machines,
    EquipmentCostBreakdown ToDate,
    EquipmentCostBreakdown ThisMonth,
    decimal Liters,
    decimal Paid,
    decimal Outstanding,
    decimal PendingReviewAmount,
    int PendingReviewCount,
    int ReturnedCount);

/// <summary>
/// The arithmetic of <c>GET /api/projects/{pid}/equipment-summary</c>, with no database access. Recognized =
/// approved only (decision G2); pending review = recorded costs, shown apart and never added to cost.
/// </summary>
internal static class EquipmentSummary
{
    /// <param name="rows">Every cost of one project.</param>
    /// <param name="monthStart">The first day of the current month at the company: "this month" is from it on.</param>
    public static ProjectEquipmentFigures Compute(IReadOnlyList<EquipmentCostSettlementRow> rows, DateOnly monthStart)
    {
        var machines = new Dictionary<int, (EquipmentCostBreakdown ToDate, decimal Month, decimal Liters, decimal Paid)>();
        var toDate = EquipmentCostBreakdown.Zero;
        var month = EquipmentCostBreakdown.Zero;
        decimal liters = 0, paid = 0, pendingAmount = 0;
        int pendingCount = 0, returnedCount = 0;

        foreach (var row in rows)
        {
            switch (row.Status)
            {
                case EquipmentCostStatus.Recorded:
                    pendingCount++;
                    pendingAmount += row.Amount;
                    continue;
                case EquipmentCostStatus.Returned:
                    returnedCount++;
                    continue;
                case EquipmentCostStatus.Rejected:
                    continue;
            }

            var group = EquipmentCostGroups.Of(row.Type);
            var rowPaid = EquipmentCostSettlement.Paid(row);
            var rowLiters = row.Type == EquipmentCostType.Fuel ? row.Liters ?? 0 : 0;
            var inMonth = row.CostDate >= monthStart;

            toDate = toDate.Add(group, row.Amount);
            if (inMonth)
            {
                month = month.Add(group, row.Amount);
            }

            liters += rowLiters;
            paid += rowPaid;

            var machine = machines.TryGetValue(row.EquipmentId, out var found)
                ? found
                : (ToDate: EquipmentCostBreakdown.Zero, Month: 0m, Liters: 0m, Paid: 0m);
            machines[row.EquipmentId] = (
                machine.ToDate.Add(group, row.Amount),
                machine.Month + (inMonth ? row.Amount : 0),
                machine.Liters + rowLiters,
                machine.Paid + rowPaid);
        }

        var perMachine = machines
            .Select(entry => new MachineCostFigures(
                entry.Key, entry.Value.ToDate, entry.Value.Month, entry.Value.Liters, entry.Value.Paid, entry.Value.ToDate.Total - entry.Value.Paid))
            .OrderByDescending(machine => machine.ToDate.Total)
            .ThenBy(machine => machine.EquipmentId)
            .ToList();

        return new ProjectEquipmentFigures(
            perMachine, toDate, month, liters, paid, toDate.Total - paid, pendingAmount, pendingCount, returnedCount);
    }

    public static DateOnly MonthStart(DateOnly today) => new(today.Year, today.Month, 1);
}
