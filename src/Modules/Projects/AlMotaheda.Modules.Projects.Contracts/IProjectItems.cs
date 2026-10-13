namespace AlMotaheda.Modules.Projects.Contracts;

/// <summary>
/// Looks up project items ("بنود") for modules that link their records to them. Implemented by Projects.
/// </summary>
public interface IProjectItems
{
    /// <summary>
    /// The items of <paramref name="projectId"/> among <paramref name="itemIds"/>, in one query. Ids that don't
    /// exist or belong to another project are left out, so a record can never be linked across projects.
    /// </summary>
    Task<IReadOnlyDictionary<int, ProjectItemRef>> FindAsync(
        int projectId, IReadOnlyCollection<int> itemIds, CancellationToken cancellationToken);
}

/// <param name="IsActive">New records can only be linked to an active item.</param>
public sealed record ProjectItemRef(int Id, int ProjectId, string NameAr, string NameEn, bool IsActive);

/// <summary>Looks up suppliers (a company-wide list owned by Projects) for modules that record supplier costs.</summary>
public interface ISupplierDirectory
{
    /// <summary>The suppliers with these ids, in one query. Unknown ids are left out.</summary>
    Task<IReadOnlyDictionary<int, SupplierRef>> FindAsync(IReadOnlyCollection<int> supplierIds, CancellationToken cancellationToken);
}

public sealed record SupplierRef(int Id, string NameAr, string NameEn, bool IsActive);
