using AlMotaheda.Modules.Projects.Contracts;

namespace AlMotaheda.Modules.Fleet.Integration;

/// <summary>Placeholder until the equipment features exist: no equipment cost.</summary>
internal sealed class EquipmentCostSource : IProjectCostSource
{
    public string Group => CostGroups.Equipment;

    public Task<ProjectCostFigures> SummarizeAsync(int projectId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken) =>
        Task.FromResult(ProjectCostFigures.Empty);
}
