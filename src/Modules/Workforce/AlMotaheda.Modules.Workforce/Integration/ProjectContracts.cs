using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Workforce.Data;
using AlMotaheda.Modules.Workforce.Features.Ledger;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Workforce.Integration;

/// <summary>
/// The Workforce answer to <see cref="ILaborItemCosts"/>: approved internal labor by project item, for the Projects
/// items screens. Only approved, non-void lines count; a line's part on one item never shows on another item or in
/// "not linked".
/// </summary>
internal sealed class LaborItemCosts(LaborLedgerSource source, WorkforceDbContext db) : ILaborItemCosts
{
    public async Task<IReadOnlyList<ItemCostTotal>> TotalsByItemAsync(int projectId, CancellationToken cancellationToken) =>
        (await source.LoadAsync(projectId, null, null, withDeductionReasons: false, cancellationToken)).Totals;

    public async Task<IReadOnlyList<LaborItemLine>> LinesAsync(int projectId, int? itemId, CancellationToken cancellationToken) =>
        (await source.LoadAsync(projectId, null, null, withDeductionReasons: true, cancellationToken)).LinesOf(itemId);

    public Task<bool> HasLinksAsync(int projectId, int itemId, CancellationToken cancellationToken) =>
        LaborQueries.LinksToItem(db, projectId, itemId).AnyAsync(cancellationToken);
}

/// <summary>The Workforce answer to <see cref="IProjectCostSource"/> (group Labor), for the overview and the reports.</summary>
internal sealed class LaborCostSource(LaborLedgerSource source) : IProjectCostSource
{
    public string Group => CostGroups.Labor;

    public Task<ProjectCostFigures> SummarizeAsync(int projectId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken) =>
        source.SummarizeAsync(projectId, from, to, cancellationToken);
}
