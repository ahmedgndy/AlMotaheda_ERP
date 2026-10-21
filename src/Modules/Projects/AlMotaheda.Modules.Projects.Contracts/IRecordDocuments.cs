namespace AlMotaheda.Modules.Projects.Contracts;

/// <summary>
/// Implemented by the Archive module: how many live documents are linked to each record of a kind, so a module can
/// tell a supported expense from an unsupported one without reading the Archive's tables. The kinds are the
/// Archive's link types (<c>Expense</c>, <c>LaborSheet</c>, <c>PayrollSettlement</c>, <c>CustodyEntry</c>, ...).
/// </summary>
public interface IRecordDocuments
{
    /// <summary>The number of documents (not deleted) linked to each id in <paramref name="ids"/>; ids with none are left out.</summary>
    Task<IReadOnlyDictionary<int, int>> CountAsync(string entityType, IReadOnlyCollection<int> ids, CancellationToken cancellationToken);
}
