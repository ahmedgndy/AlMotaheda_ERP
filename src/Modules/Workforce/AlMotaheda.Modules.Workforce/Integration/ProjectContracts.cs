using AlMotaheda.Modules.Projects.Contracts;

namespace AlMotaheda.Modules.Workforce.Integration;

/// <summary>Placeholder until the labor features exist: no labor on any item.</summary>
internal sealed class LaborItemCosts : ILaborItemCosts
{
    public Task<IReadOnlyList<ItemCostTotal>> TotalsByItemAsync(int projectId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ItemCostTotal>>([]);

    public Task<IReadOnlyList<LaborItemLine>> LinesAsync(int projectId, int? itemId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<LaborItemLine>>([]);

    public Task<bool> HasLinksAsync(int projectId, int itemId, CancellationToken cancellationToken) => Task.FromResult(false);
}

/// <summary>Placeholder until the labor features exist: no labor cost.</summary>
internal sealed class LaborCostSource : IProjectCostSource
{
    public string Group => CostGroups.Labor;

    public Task<ProjectCostFigures> SummarizeAsync(int projectId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken) =>
        Task.FromResult(ProjectCostFigures.Empty);
}
