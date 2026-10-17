using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Features.Projects;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Features.DailyReports;

/// <summary>One report in the list.</summary>
internal sealed record DailyReportListItem(
    int Id,
    DateOnly ReportDate,
    PersonRef Author,
    string Status,
    int? WorkersPresent,
    int LineCount,
    int SubcontractorLineCount,
    int MaterialCount,
    int EquipmentCount,
    bool HasIssues,
    DateTime SubmittedAt,
    DateTime? ReviewedAt,
    PersonRef? Reviewer)
{
    public static DailyReportListItem From(DailyReportSummaryRow row, IReadOnlyDictionary<int, UserSummary> people) => new(
        row.Id,
        row.ReportDate,
        PersonRef.From(row.AuthorUserId, people),
        row.Status.ToString(),
        row.WorkersPresent,
        row.LineCount,
        row.SubcontractorLineCount,
        row.MaterialCount,
        row.EquipmentCount,
        row.HasIssues,
        row.SubmittedAt,
        row.ReviewedAt,
        row.ReviewedBy is { } reviewer ? PersonRef.From(reviewer, people) : null);
}

internal sealed record DailyReportLineView(
    int Id,
    int LineNo,
    string WorkType,
    string Description,
    string Unit,
    decimal Quantity,
    string PerformedBy,
    int? ProjectItemId,
    string? ProjectItemNameAr,
    string? ProjectItemNameEn,
    string? SubcontractorName,
    int? SubContractId,
    int? WorkersCount);

/// <summary>A supply received that day: what, how much, from whom. No prices.</summary>
internal sealed record ReceivedMaterialView(
    int ExpenseId,
    string? ItemName,
    decimal? Quantity,
    string? Unit,
    int? SupplierId,
    string? SupplierNameAr,
    string? SupplierNameEn,
    string Status);

/// <summary>What the caller may do with this report now, so the frontend shows only the buttons that will work.</summary>
internal sealed record DailyReportAbilities(bool Edit, bool Review);

/// <summary>
/// One daily report with its work, materials used, equipment used, and the supplies received that day. It holds
/// no money. Photos are Archive documents linked to the report.
/// </summary>
internal sealed record DailyReportView(
    int Id,
    int ProjectId,
    DateOnly ReportDate,
    PersonRef Author,
    string Status,
    int? WorkersPresent,
    string? Weather,
    string? Issues,
    string? Notes,
    IReadOnlyList<DailyReportLineView> Lines,
    IReadOnlyList<DailyReportMaterialRow> Materials,
    IReadOnlyList<DailyReportEquipmentRow> Equipment,
    IReadOnlyList<ReceivedMaterialView> MaterialsReceived,
    int? ReportRequestId,
    string? ReportRequestTitle,
    DateTime CreatedAt,
    DateTime? WrittenAt,
    DateTime SubmittedAt,
    DateTime? ReviewedAt,
    PersonRef? Reviewer,
    string? ReviewNote,
    string RowVersion,
    DailyReportAbilities Can)
{
    /// <summary>Reads one report of a project, or <c>null</c> when it is not on this project.</summary>
    /// <param name="callerCanReview">site Approve, and an Executive or the project's manager.</param>
    public static async Task<DailyReportView?> LoadAsync(
        ProjectsDbContext db,
        IUserDirectory users,
        int projectId,
        int reportId,
        int callerUserId,
        bool callerCanReview,
        CancellationToken cancellationToken)
    {
        var row = await DailyReportQueries.One(db, projectId, reportId).SingleOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return null;
        }

        var received = await DailyReportQueries.ReceivedOn(db, projectId, row.ReportDate).ToListAsync(cancellationToken);
        var people = await PlanningPeople.FindAsync(
            users, row.ReviewedBy is { } reviewer ? [row.AuthorUserId, reviewer] : [row.AuthorUserId], cancellationToken);

        var abilities = new DailyReportAbilities(
            DailyReportRules.Edit(row.Status, row.AuthorUserId, callerUserId) == ReportDecision.Allowed,
            callerCanReview && DailyReportRules.Review(row.Status, row.AuthorUserId, callerUserId) == ReportDecision.Allowed);

        return new DailyReportView(
            row.Id,
            row.ProjectId,
            row.ReportDate,
            PersonRef.From(row.AuthorUserId, people),
            row.Status.ToString(),
            row.WorkersPresent,
            row.Weather,
            row.Issues,
            row.Notes,
            row.Lines.Select(line => new DailyReportLineView(
                line.Id,
                line.LineNo,
                line.WorkType,
                line.Description,
                line.Unit,
                line.Quantity,
                line.PerformedBy.ToString(),
                line.ProjectItemId,
                line.ProjectItemNameAr,
                line.ProjectItemNameEn,
                line.SubcontractorName,
                line.SubContractId,
                line.WorkersCount)).ToList(),
            row.Materials,
            row.Equipment,
            received.Select(supply => new ReceivedMaterialView(
                supply.ExpenseId,
                supply.ItemName,
                supply.Quantity,
                supply.Unit,
                supply.SupplierId,
                supply.SupplierNameAr,
                supply.SupplierNameEn,
                supply.Status.ToString())).ToList(),
            row.ReportRequestId,
            row.ReportRequestTitle,
            row.CreatedAt,
            row.WrittenAt,
            row.SubmittedAt,
            row.ReviewedAt,
            row.ReviewedBy is { } reviewedBy ? PersonRef.From(reviewedBy, people) : null,
            row.ReviewNote,
            RowVersions.Format(row.RowVersion),
            abilities);
    }
}
