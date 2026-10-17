using AlMotaheda.Modules.Projects.Data;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Features.DailyReports;

/// <summary>One report as the list shows it: the header and counts, not the lines.</summary>
internal sealed record DailyReportSummaryRow(
    int Id,
    DateOnly ReportDate,
    int AuthorUserId,
    DailyReportStatus Status,
    int? WorkersPresent,
    int LineCount,
    int SubcontractorLineCount,
    int MaterialCount,
    int EquipmentCount,
    bool HasIssues,
    DateTime SubmittedAt,
    DateTime? ReviewedAt,
    int? ReviewedBy);

internal sealed record DailyReportLineRow(
    int Id,
    int LineNo,
    string WorkType,
    string Description,
    string Unit,
    decimal Quantity,
    WorkPerformer PerformedBy,
    int? ProjectItemId,
    string? ProjectItemNameAr,
    string? ProjectItemNameEn,
    string? SubcontractorName,
    int? SubContractId,
    int? WorkersCount);

internal sealed record DailyReportMaterialRow(int Id, string MaterialName, string Unit, decimal Quantity);

internal sealed record DailyReportEquipmentRow(int Id, int? EquipmentId, string EquipmentName, decimal? Hours);

/// <summary>One report with everything the detail shows, read in one query.</summary>
internal sealed class DailyReportRow
{
    public int Id { get; init; }
    public int ProjectId { get; init; }
    public DateOnly ReportDate { get; init; }
    public int AuthorUserId { get; init; }
    public DailyReportStatus Status { get; init; }
    public int? WorkersPresent { get; init; }
    public string? Weather { get; init; }
    public string? Issues { get; init; }
    public string? Notes { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? WrittenAt { get; init; }
    public DateTime SubmittedAt { get; init; }
    public DateTime? ReviewedAt { get; init; }
    public int? ReviewedBy { get; init; }
    public string? ReviewNote { get; init; }
    public int? ReportRequestId { get; init; }
    public string? ReportRequestTitle { get; init; }
    public byte[] RowVersion { get; init; } = [];
    public List<DailyReportLineRow> Lines { get; init; } = [];
    public List<DailyReportMaterialRow> Materials { get; init; } = [];
    public List<DailyReportEquipmentRow> Equipment { get; init; } = [];
}

/// <summary>A supply of the project dated on the report's day: materials received. No prices: a report holds no money.</summary>
internal sealed record ReceivedMaterialRow(
    int ExpenseId,
    string? ItemName,
    decimal? Quantity,
    string? Unit,
    int? SupplierId,
    string? SupplierNameAr,
    string? SupplierNameEn,
    ExpenseStatus Status);

/// <summary>The filters of <c>GET /api/projects/{pid}/daily-reports</c>, after validation.</summary>
internal sealed record DailyReportFilter(DailyReportStatus? Status, DateOnly? From, DateOnly? To, int? AuthorUserId);

/// <summary>The daily report queries, as <see cref="IQueryable{T}"/> so tests can prove they translate to SQL.</summary>
internal static class DailyReportQueries
{
    /// <summary>The list screen, newest day first. Every filter runs in SQL.</summary>
    public static IQueryable<DailyReportSummaryRow> Filtered(ProjectsDbContext db, int projectId, DailyReportFilter filter)
    {
        var query = db.DailyReports.AsNoTracking().Where(report => report.ProjectId == projectId);

        if (filter.Status is { } status)
        {
            query = query.Where(report => report.Status == status);
        }

        if (filter.From is { } from)
        {
            query = query.Where(report => report.ReportDate >= from);
        }

        if (filter.To is { } to)
        {
            query = query.Where(report => report.ReportDate <= to);
        }

        if (filter.AuthorUserId is { } author)
        {
            query = query.Where(report => report.AuthorUserId == author);
        }

        return query
            .OrderByDescending(report => report.ReportDate)
            .ThenByDescending(report => report.Id)
            .Select(report => new DailyReportSummaryRow(
                report.Id,
                report.ReportDate,
                report.AuthorUserId,
                report.Status,
                report.WorkersPresent,
                report.Lines.Count,
                report.Lines.Count(line => line.PerformedBy == WorkPerformer.Subcontractor),
                report.Materials.Count,
                report.Equipment.Count,
                report.Issues != null,
                report.SubmittedAt,
                report.ReviewedAt,
                report.ReviewedBy));
    }

    /// <summary>One report of the project with its lines, materials and equipment, or none when it is not on this project.</summary>
    public static IQueryable<DailyReportRow> One(ProjectsDbContext db, int projectId, int reportId) =>
        db.DailyReports.AsNoTracking()
            .Where(report => report.ProjectId == projectId && report.Id == reportId)
            .Select(report => new DailyReportRow
            {
                Id = report.Id,
                ProjectId = report.ProjectId,
                ReportDate = report.ReportDate,
                AuthorUserId = report.AuthorUserId,
                Status = report.Status,
                WorkersPresent = report.WorkersPresent,
                Weather = report.Weather,
                Issues = report.Issues,
                Notes = report.Notes,
                CreatedAt = report.CreatedAt,
                WrittenAt = report.WrittenAt,
                SubmittedAt = report.SubmittedAt,
                ReviewedAt = report.ReviewedAt,
                ReviewedBy = report.ReviewedBy,
                ReviewNote = report.ReviewNote,
                ReportRequestId = report.ReportRequestId,
                ReportRequestTitle = report.ReportRequest != null ? report.ReportRequest.Title : null,
                RowVersion = report.RowVersion,
                Lines = report.Lines
                    .OrderBy(line => line.LineNo)
                    .Select(line => new DailyReportLineRow(
                        line.Id,
                        line.LineNo,
                        line.WorkType,
                        line.Description,
                        line.Unit,
                        line.Quantity,
                        line.PerformedBy,
                        line.ProjectItemId,
                        line.ProjectItem != null ? line.ProjectItem.NameAr : null,
                        line.ProjectItem != null ? line.ProjectItem.NameEn : null,
                        line.SubcontractorName,
                        line.SubContractId,
                        line.WorkersCount))
                    .ToList(),
                Materials = report.Materials
                    .OrderBy(material => material.Id)
                    .Select(material => new DailyReportMaterialRow(material.Id, material.MaterialName, material.Unit, material.Quantity))
                    .ToList(),
                Equipment = report.Equipment
                    .OrderBy(equipment => equipment.Id)
                    .Select(equipment => new DailyReportEquipmentRow(equipment.Id, equipment.EquipmentId, equipment.EquipmentName, equipment.Hours))
                    .ToList(),
            });

    /// <summary>
    /// The project's supplies dated on <paramref name="date"/> (materials received that day), not stored again on
    /// the report. Rejected ones were never received.
    /// </summary>
    public static IQueryable<ReceivedMaterialRow> ReceivedOn(ProjectsDbContext db, int projectId, DateOnly date) =>
        db.ProjectExpenses.AsNoTracking()
            .Where(expense => expense.ProjectId == projectId
                && expense.Kind == ExpenseKind.Supply
                && expense.ExpenseDate == date
                && expense.Status != ExpenseStatus.Rejected)
            .OrderBy(expense => expense.Id)
            .Select(expense => new ReceivedMaterialRow(
                expense.Id,
                expense.ItemName,
                expense.Quantity,
                expense.Unit,
                expense.SupplierId,
                expense.Supplier != null ? expense.Supplier.NameAr : null,
                expense.Supplier != null ? expense.Supplier.NameEn : null,
                expense.Status));
}
