namespace AlMotaheda.Modules.Projects.Contracts;

/// <summary>
/// The kinds of project records another module may point to (an archive document's links, a daily report's
/// subcontract and equipment rows). The names match the Archive module's link types.
/// </summary>
public static class ProjectRecordKinds
{
    public const string Expense = "Expense";
    public const string SupplierPayment = "SupplierPayment";
    public const string DailyReport = "DailyReport";
    public const string ReportRequest = "ReportRequest";
    public const string Budget = "Budget";
    public const string LaborSheet = "LaborSheet";
    public const string SubContract = "SubContract";
    public const string SubPayment = "SubPayment";
    public const string Equipment = "Equipment";
    public const string EquipmentCost = "EquipmentCost";
    public const string RepairRequest = "RepairRequest";

    /// <summary>A supplier (company-wide): a project document may be one of its documents (docs/cost-flow §2.5).</summary>
    public const string Supplier = "Supplier";
}

/// <summary>
/// Answers "which of these records belong to this project?" for the record kinds one module owns. Every module that
/// owns records others may point to registers one. A caller must never store a pointer to a record of another
/// project, so it asks the owner instead of trusting the id the client sent.
/// </summary>
public interface IProjectRecordOwnership
{
    /// <summary>The <see cref="ProjectRecordKinds"/> this module owns.</summary>
    IReadOnlyCollection<string> Kinds { get; }

    /// <summary>
    /// The ids among <paramref name="ids"/> that are records of <paramref name="kind"/> on <paramref name="projectId"/>,
    /// in one query. Ids that don't exist or belong to another project are left out.
    /// </summary>
    Task<IReadOnlySet<int>> FindOnProjectAsync(int projectId, string kind, IReadOnlyCollection<int> ids, CancellationToken cancellationToken);
}
