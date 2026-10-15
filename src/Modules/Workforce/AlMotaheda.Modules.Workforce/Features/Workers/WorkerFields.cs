using System.Text.RegularExpressions;
using AlMotaheda.Modules.Workforce.Common;
using AlMotaheda.Modules.Workforce.Data;
using AlMotaheda.Modules.Workforce.Data.Configurations;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Workforce.Features.Workers;

/// <summary>The fields of the worker form, shared by create and edit.</summary>
internal interface IWorkerFieldsRequest
{
    string? NameAr { get; }
    string? NameEn { get; }
    string? NationalId { get; }
    string? Phone { get; }
    int? TradeId { get; }
    decimal? DailyRate { get; }
    string? Notes { get; }
}

/// <summary>A worker form after validation.</summary>
internal sealed record WorkerFields(
    string NameAr,
    string NameEn,
    string? NationalId,
    string? Phone,
    int TradeId,
    decimal? DailyRate,
    string? Notes)
{
    public void ApplyTo(Worker worker)
    {
        worker.NameAr = NameAr;
        worker.NameEn = NameEn;
        worker.NationalId = NationalId;
        worker.Phone = Phone;
        worker.TradeId = TradeId;
        worker.DailyRate = DailyRate;
        worker.Notes = Notes;
    }
}

/// <summary>The input rules of the worker form. Every failing field is reported at once.</summary>
internal static partial class WorkerForm
{
    public static WorkerFields? Validate(IWorkerFieldsRequest request, ValidationErrors errors)
    {
        var nameAr = Input.RequiredText(request.NameAr, WorkerConfiguration.NameMaxLength, errors, "nameAr", "The Arabic name");
        var nameEn = Input.RequiredText(request.NameEn, WorkerConfiguration.NameMaxLength, errors, "nameEn", "The English name");

        var nationalId = Input.Clean(request.NationalId);
        if (nationalId is not null && !NationalIdPattern().IsMatch(nationalId))
        {
            errors.Add("nationalId", "The national id must be exactly 14 digits.");
        }

        var phone = Input.Clean(request.Phone);
        if (phone is not null && !PhonePattern().IsMatch(phone))
        {
            errors.Add("phone", $"The phone number may have digits, spaces, '+', '-' and brackets, at most {WorkerConfiguration.PhoneMaxLength} characters.");
        }

        if (request.TradeId is null or < 1)
        {
            errors.Add("tradeId", "tradeId is required.");
        }

        var rate = NumberInput.OptionalMoney(request.DailyRate, errors, "dailyRate", "The daily rate");
        var notes = Input.OptionalText(request.Notes, WorkerConfiguration.NotesMaxLength, errors, "notes", "Notes");

        return errors.HasErrors ? null : new WorkerFields(nameAr!, nameEn!, nationalId, phone, request.TradeId!.Value, rate, notes);
    }

    [GeneratedRegex("^[0-9]{14}$")]
    private static partial Regex NationalIdPattern();

    [GeneratedRegex(@"^[0-9+\-() ]{3,30}$")]
    private static partial Regex PhonePattern();
}

/// <summary>National ids are personal data: shown in full only to callers with workers Edit.</summary>
internal static class NationalIds
{
    /// <summary>All but the last 4 digits become <c>*</c>, like the prototype.</summary>
    public static string? Mask(string? nationalId) =>
        nationalId is null || nationalId.Length <= 4 ? nationalId : new string('*', nationalId.Length - 4) + nationalId[^4..];

    public static string? Show(string? nationalId, bool full) => full ? nationalId : Mask(nationalId);
}

/// <summary>A worker as the queries project it; the views are built from it in memory.</summary>
internal sealed class WorkerRow
{
    public int Id { get; init; }
    public string NameAr { get; init; } = "";
    public string NameEn { get; init; } = "";
    public string? NationalId { get; init; }
    public string? Phone { get; init; }
    public int TradeId { get; init; }
    public string TradeNameAr { get; init; } = "";
    public string TradeNameEn { get; init; } = "";
    public decimal TradeDefaultRate { get; init; }
    public decimal? DailyRate { get; init; }
    public WorkerStatus Status { get; init; }
    public string? Notes { get; init; }
    public DateTime CreatedAt { get; init; }
    public int CreatedBy { get; init; }
    public DateTime? UpdatedAt { get; init; }
    public int? UpdatedBy { get; init; }
    public byte[] RowVersion { get; init; } = [];
}

/// <summary>
/// A worker as the API returns it. The national id is masked without workers Edit; the rates are <c>null</c>
/// without workers See money.
/// </summary>
/// <param name="DailyRate">The worker's own rate; <c>null</c> when they use the trade's default.</param>
/// <param name="EffectiveDailyRate">The rate a normal day in their own trade gets, before any project rate.</param>
internal sealed record WorkerView(
    int Id,
    string NameAr,
    string NameEn,
    string? NationalId,
    string? Phone,
    int TradeId,
    string TradeNameAr,
    string TradeNameEn,
    decimal? DailyRate,
    decimal? EffectiveDailyRate,
    string Status,
    string? Notes,
    DateTime CreatedAt,
    int CreatedBy,
    DateTime? UpdatedAt,
    int? UpdatedBy,
    string RowVersion)
{
    public static WorkerView From(WorkerRow row, bool fullNationalId, bool seesMoney) => new(
        row.Id,
        row.NameAr,
        row.NameEn,
        NationalIds.Show(row.NationalId, fullNationalId),
        row.Phone,
        row.TradeId,
        row.TradeNameAr,
        row.TradeNameEn,
        seesMoney ? row.DailyRate : null,
        seesMoney ? row.DailyRate ?? row.TradeDefaultRate : null,
        row.Status.ToString(),
        row.Notes,
        row.CreatedAt,
        row.CreatedBy,
        row.UpdatedAt,
        row.UpdatedBy,
        RowVersions.Format(row.RowVersion));
}

/// <summary>The filters of <c>GET /api/workers</c>, after validation.</summary>
/// <param name="ProjectId">Only workers with an open assignment on this project.</param>
/// <param name="SearchNationalId">The search also matches national ids (only for callers who may see them).</param>
internal sealed record WorkerFilter(string? Search, int? TradeId, WorkerStatus? Status, int? ProjectId, bool SearchNationalId);

/// <summary>The worker queries, as <see cref="IQueryable{T}"/> so tests can prove they translate to SQL.</summary>
internal static class WorkerQueries
{
    /// <summary>The workers list: every filter runs in SQL, by English name.</summary>
    public static IQueryable<WorkerRow> Search(WorkforceDbContext db, WorkerFilter filter)
    {
        var query = db.Workers.AsNoTracking();

        if (filter.Search is { } text)
        {
            query = filter.SearchNationalId
                ? query.Where(worker => worker.NameEn.Contains(text) || worker.NameAr.Contains(text)
                    || (worker.Phone != null && worker.Phone.Contains(text))
                    || (worker.NationalId != null && worker.NationalId.Contains(text)))
                : query.Where(worker => worker.NameEn.Contains(text) || worker.NameAr.Contains(text)
                    || (worker.Phone != null && worker.Phone.Contains(text)));
        }

        if (filter.TradeId is { } tradeId)
        {
            query = query.Where(worker => worker.TradeId == tradeId);
        }

        if (filter.Status is { } status)
        {
            query = query.Where(worker => worker.Status == status);
        }

        if (filter.ProjectId is { } projectId)
        {
            query = query.Where(worker => db.WorkerAssignments.Any(assignment =>
                assignment.WorkerId == worker.Id && assignment.ProjectId == projectId && assignment.EndDate == null));
        }

        return Project(query.OrderBy(worker => worker.NameEn).ThenBy(worker => worker.Id));
    }

    public static IQueryable<WorkerRow> One(WorkforceDbContext db, int workerId) =>
        Project(db.Workers.AsNoTracking().Where(worker => worker.Id == workerId));

    private static IQueryable<WorkerRow> Project(IQueryable<Worker> query) =>
        query.Select(worker => new WorkerRow
        {
            Id = worker.Id,
            NameAr = worker.NameAr,
            NameEn = worker.NameEn,
            NationalId = worker.NationalId,
            Phone = worker.Phone,
            TradeId = worker.TradeId,
            TradeNameAr = worker.Trade.NameAr,
            TradeNameEn = worker.Trade.NameEn,
            TradeDefaultRate = worker.Trade.DefaultDailyRate,
            DailyRate = worker.DailyRate,
            Status = worker.Status,
            Notes = worker.Notes,
            CreatedAt = worker.CreatedAt,
            CreatedBy = worker.CreatedBy,
            UpdatedAt = worker.UpdatedAt,
            UpdatedBy = worker.UpdatedBy,
            RowVersion = worker.RowVersion,
        });
}
