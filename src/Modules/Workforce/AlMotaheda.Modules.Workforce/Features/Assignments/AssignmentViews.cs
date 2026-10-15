using AlMotaheda.Modules.Workforce.Common;
using AlMotaheda.Modules.Workforce.Data;
using AlMotaheda.Modules.Workforce.Features.Workers;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Workforce.Features.Assignments;

/// <summary>An assignment with its worker, as the queries project it.</summary>
internal sealed class AssignmentRow
{
    public int Id { get; init; }
    public int WorkerId { get; init; }
    public string WorkerNameAr { get; init; } = "";
    public string WorkerNameEn { get; init; } = "";
    public string? NationalId { get; init; }
    public string? Phone { get; init; }
    public WorkerStatus WorkerStatus { get; init; }
    public int TradeId { get; init; }
    public string TradeNameAr { get; init; } = "";
    public string TradeNameEn { get; init; } = "";
    public decimal TradeDefaultRate { get; init; }
    public decimal? WorkerRate { get; init; }
    public int ProjectId { get; init; }
    public decimal? DailyRate { get; init; }
    public DateOnly StartDate { get; init; }
    public DateOnly? EndDate { get; init; }
    public DateTime AssignedAt { get; init; }
    public int AssignedBy { get; init; }
    public DateTime? EndedAt { get; init; }
    public int? EndedBy { get; init; }
    public byte[] RowVersion { get; init; } = [];
}

/// <summary>
/// A worker's assignment to a project, as the API returns it. Rates are <c>null</c> without workers See money;
/// the national id is masked without workers Edit.
/// </summary>
/// <param name="DailyRate">The rate on this project only; <c>null</c> when the worker's own rate applies.</param>
/// <param name="EffectiveDailyRate">The rate a normal day in the worker's own trade gets on this project.</param>
internal sealed record WorkerAssignmentView(
    int Id,
    int WorkerId,
    string WorkerNameAr,
    string WorkerNameEn,
    string? NationalId,
    string? Phone,
    string WorkerStatus,
    int TradeId,
    string TradeNameAr,
    string TradeNameEn,
    int ProjectId,
    decimal? DailyRate,
    decimal? EffectiveDailyRate,
    DateOnly StartDate,
    DateOnly? EndDate,
    bool IsOpen,
    DateTime AssignedAt,
    int AssignedBy,
    DateTime? EndedAt,
    int? EndedBy,
    string RowVersion)
{
    public static WorkerAssignmentView From(AssignmentRow row, bool fullNationalId, bool seesMoney) => new(
        row.Id,
        row.WorkerId,
        row.WorkerNameAr,
        row.WorkerNameEn,
        NationalIds.Show(row.NationalId, fullNationalId),
        row.Phone,
        row.WorkerStatus.ToString(),
        row.TradeId,
        row.TradeNameAr,
        row.TradeNameEn,
        row.ProjectId,
        seesMoney ? row.DailyRate : null,
        seesMoney ? row.DailyRate ?? row.WorkerRate ?? row.TradeDefaultRate : null,
        row.StartDate,
        row.EndDate,
        row.EndDate is null,
        row.AssignedAt,
        row.AssignedBy,
        row.EndedAt,
        row.EndedBy,
        RowVersions.Format(row.RowVersion));
}

/// <summary>The assignment queries, as <see cref="IQueryable{T}"/> so tests can prove they translate to SQL.</summary>
internal static class AssignmentQueries
{
    /// <summary>The workers of a project: open assignments only unless <paramref name="includeEnded"/>; by worker name.</summary>
    public static IQueryable<AssignmentRow> OfProject(WorkforceDbContext db, int projectId, bool includeEnded)
    {
        var query = db.WorkerAssignments.AsNoTracking().Where(assignment => assignment.ProjectId == projectId);
        if (!includeEnded)
        {
            query = query.Where(assignment => assignment.EndDate == null);
        }

        return Project(query
            .OrderBy(assignment => assignment.EndDate != null)
            .ThenBy(assignment => assignment.Worker.NameEn)
            .ThenByDescending(assignment => assignment.StartDate)
            .ThenBy(assignment => assignment.Id));
    }

    /// <summary>Every assignment of one worker, newest first: the worker's project history.</summary>
    public static IQueryable<AssignmentRow> OfWorker(WorkforceDbContext db, int workerId) =>
        Project(db.WorkerAssignments.AsNoTracking()
            .Where(assignment => assignment.WorkerId == workerId)
            .OrderByDescending(assignment => assignment.StartDate)
            .ThenByDescending(assignment => assignment.Id));

    /// <summary>One assignment of a project, or none when the id belongs to another project.</summary>
    public static IQueryable<AssignmentRow> One(WorkforceDbContext db, int projectId, int assignmentId) =>
        Project(db.WorkerAssignments.AsNoTracking().Where(assignment => assignment.ProjectId == projectId && assignment.Id == assignmentId));

    private static IQueryable<AssignmentRow> Project(IQueryable<WorkerAssignment> query) =>
        query.Select(assignment => new AssignmentRow
        {
            Id = assignment.Id,
            WorkerId = assignment.WorkerId,
            WorkerNameAr = assignment.Worker.NameAr,
            WorkerNameEn = assignment.Worker.NameEn,
            NationalId = assignment.Worker.NationalId,
            Phone = assignment.Worker.Phone,
            WorkerStatus = assignment.Worker.Status,
            TradeId = assignment.Worker.TradeId,
            TradeNameAr = assignment.Worker.Trade.NameAr,
            TradeNameEn = assignment.Worker.Trade.NameEn,
            TradeDefaultRate = assignment.Worker.Trade.DefaultDailyRate,
            WorkerRate = assignment.Worker.DailyRate,
            ProjectId = assignment.ProjectId,
            DailyRate = assignment.DailyRate,
            StartDate = assignment.StartDate,
            EndDate = assignment.EndDate,
            AssignedAt = assignment.AssignedAt,
            AssignedBy = assignment.AssignedBy,
            EndedAt = assignment.EndedAt,
            EndedBy = assignment.EndedBy,
            RowVersion = assignment.RowVersion,
        });
}
