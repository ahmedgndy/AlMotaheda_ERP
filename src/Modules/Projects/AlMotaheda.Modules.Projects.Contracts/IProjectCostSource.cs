namespace AlMotaheda.Modules.Projects.Contracts;

/// <summary>
/// A module that owns one cost group of a project and reports it to the overview, the budget comparison and the
/// reports. Workforce, Subcontractors and Fleet each implement it once. Projects never reads their tables.
/// </summary>
public interface IProjectCostSource
{
    /// <summary>One of <see cref="CostGroups"/>. Unique among the registered sources.</summary>
    string Group { get; }

    /// <summary>
    /// The group's figures for one project. <paramref name="from"/> and <paramref name="to"/> limit the business
    /// date of the records (inclusive); <c>null</c> means no limit.
    /// </summary>
    Task<ProjectCostFigures> SummarizeAsync(int projectId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken);
}

/// <param name="Recognized">Approved cost. Never requests, never returned or rejected records.</param>
/// <param name="Paid">The part of <paramref name="Recognized"/> settled by payments.</param>
/// <param name="RecognizedRecords">How many approved records.</param>
/// <param name="PendingReview">Recorded or submitted, not approved yet. Shown apart; never added to cost.</param>
/// <param name="PendingRecords">How many records are waiting.</param>
/// <param name="Committed">Signed but not executed yet (subcontract value minus executed work). 0 where it has no meaning.</param>
public sealed record ProjectCostFigures(
    decimal Recognized,
    decimal Paid,
    int RecognizedRecords,
    decimal PendingReview,
    int PendingRecords,
    decimal Committed)
{
    public static ProjectCostFigures Empty { get; } = new(0, 0, 0, 0, 0, 0);

    public decimal Outstanding => Recognized - Paid;
}
