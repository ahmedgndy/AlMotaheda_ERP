using AlMotaheda.Modules.Fleet.Data;
using AlMotaheda.Modules.Fleet.Features.Costs;
using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Fleet.Integration;

/// <summary>
/// The equipment cost group of a project, for the overview, the budget comparison, the reports and the "other
/// project costs" of the items screen. Recognized = approved equipment costs in the period; paid = custody at
/// approval and credit once its payment is recorded; pending review = recorded costs; committed = 0.
/// </summary>
/// <remarks>
/// Only this project's rows are read, flat, and the arithmetic is <see cref="EquipmentCostSettlement.Figures"/>, the
/// same rule as the equipment screens (decision G5). Equipment costs are never split over project items (F1).
/// </remarks>
internal sealed class EquipmentCostSource(FleetDbContext db) : IProjectCostSource
{
    public string Group => CostGroups.Equipment;

    public async Task<ProjectCostFigures> SummarizeAsync(int projectId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken)
    {
        var rows = await EquipmentCostQueries.SettlementRows(db, projectId, from, to).ToListAsync(cancellationToken);
        return EquipmentCostSettlement.Figures(rows);
    }
}
