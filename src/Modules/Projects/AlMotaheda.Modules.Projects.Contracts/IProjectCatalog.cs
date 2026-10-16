namespace AlMotaheda.Modules.Projects.Contracts;

/// <summary>
/// Looks up a project's price catalog ("the project's current item price") for modules that estimate or buy
/// materials. Implemented by Projects.
/// </summary>
public interface IProjectCatalog
{
    /// <summary>
    /// The catalog items of <paramref name="projectId"/> among <paramref name="catalogItemIds"/>, with the approved
    /// price that applies on <paramref name="date"/>. Ids that don't exist or belong to another project are left out.
    /// </summary>
    Task<IReadOnlyDictionary<int, CatalogItemRef>> FindAsync(
        int projectId, IReadOnlyCollection<int> catalogItemIds, DateOnly date, CancellationToken cancellationToken);
}

/// <param name="Unit">One of <see cref="MeasurementUnits"/>.</param>
/// <param name="CurrentPrice">The approved price on the asked date, or <c>null</c> when none applies yet.</param>
public sealed record CatalogItemRef(int Id, int ProjectId, string NameAr, string NameEn, string Unit, bool IsActive, decimal? CurrentPrice);
