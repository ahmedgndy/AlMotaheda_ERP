using AlMotaheda.Modules.Projects.Contracts;

namespace AlMotaheda.Modules.Subcontractors.Integration;

/// <summary>Placeholder until the subcontract features exist: no subcontract work on any item.</summary>
internal sealed class SubcontractItemCosts : ISubcontractItemCosts
{
    public Task<IReadOnlyList<ItemCostTotal>> TotalsByItemAsync(int projectId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ItemCostTotal>>([]);

    public Task<IReadOnlyList<SubcontractItemLine>> LinesAsync(int projectId, int? itemId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SubcontractItemLine>>([]);

    public Task<bool> HasLinksAsync(int projectId, int itemId, CancellationToken cancellationToken) => Task.FromResult(false);
}

/// <summary>Placeholder until the subcontract features exist: no subcontract cost.</summary>
internal sealed class SubcontractCostSource : IProjectCostSource
{
    public string Group => CostGroups.Subcontractors;

    public Task<ProjectCostFigures> SummarizeAsync(int projectId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken) =>
        Task.FromResult(ProjectCostFigures.Empty);
}
