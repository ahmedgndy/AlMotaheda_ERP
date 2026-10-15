using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Workforce.Common;
using AlMotaheda.Modules.Workforce.Data;
using AlMotaheda.Modules.Workforce.Security;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Workforce.Features.Payments;

/// <summary>One allocation of a worker payment: which day of which project it settles.</summary>
internal sealed record WorkerPaymentAllocationView(int LineId, int SheetId, int ProjectId, DateOnly WorkDate, string Shift, decimal Amount);

/// <summary>One worker payment, as the list and the writes return it.</summary>
internal sealed record WorkerPaymentView(
    int Id,
    int WorkerId,
    string WorkerNameAr,
    string WorkerNameEn,
    DateOnly PaymentDate,
    decimal Amount,
    string Method,
    string? Reference,
    string? Notes,
    Guid IdempotencyKey,
    bool IsVoided,
    DateTime? VoidedAt,
    int? VoidedBy,
    string? VoidReason,
    IReadOnlyList<WorkerPaymentAllocationView> Allocations,
    DateTime CreatedAt,
    int CreatedBy,
    string RowVersion)
{
    public static WorkerPaymentView From(WorkerPaymentRow row) => From(row, null);

    /// <summary>
    /// The payment as a caller who sees only <paramref name="visibleProjectIds"/> may see it: the allocations on
    /// other projects are left out, and the amount is then the part paid on the shown projects (a payment is
    /// exactly the sum of its allocations). <c>null</c> shows everything.
    /// </summary>
    public static WorkerPaymentView From(WorkerPaymentRow row, IReadOnlySet<int>? visibleProjectIds)
    {
        var shown = visibleProjectIds is null
            ? row.Allocations
            : row.Allocations.Where(allocation => visibleProjectIds.Contains(allocation.ProjectId)).ToList();
        var amount = shown.Count == row.Allocations.Count ? row.Amount : shown.Sum(allocation => allocation.Amount);

        return Build(row, amount, shown);
    }

    private static WorkerPaymentView Build(WorkerPaymentRow row, decimal amount, IReadOnlyList<WorkerPaymentAllocationRow> allocations) => new(
        row.Id,
        row.WorkerId,
        row.WorkerNameAr,
        row.WorkerNameEn,
        row.PaymentDate,
        amount,
        row.Method.ToString(),
        row.Reference,
        row.Notes,
        row.IdempotencyKey,
        row.VoidedAt is not null,
        row.VoidedAt,
        row.VoidedBy,
        row.VoidReason,
        allocations
            .Select(allocation => new WorkerPaymentAllocationView(
                allocation.LineId, allocation.SheetId, allocation.ProjectId, allocation.WorkDate, allocation.Shift.ToString(), allocation.Amount))
            .ToList(),
        row.CreatedAt,
        row.CreatedBy,
        RowVersions.Format(row.RowVersion));
}

/// <summary>A payment as the queries project it; the view is built from it in memory.</summary>
internal sealed class WorkerPaymentRow
{
    public int Id { get; init; }
    public int WorkerId { get; init; }
    public string WorkerNameAr { get; init; } = "";
    public string WorkerNameEn { get; init; } = "";
    public DateOnly PaymentDate { get; init; }
    public decimal Amount { get; init; }
    public WorkerPaymentMethod Method { get; init; }
    public string? Reference { get; init; }
    public string? Notes { get; init; }
    public Guid IdempotencyKey { get; init; }
    public DateTime? VoidedAt { get; init; }
    public int? VoidedBy { get; init; }
    public string? VoidReason { get; init; }
    public DateTime CreatedAt { get; init; }
    public int CreatedBy { get; init; }
    public byte[] RowVersion { get; init; } = [];
    public List<WorkerPaymentAllocationRow> Allocations { get; init; } = [];
}

internal sealed record PendingLineCount(int ProjectId, int Lines);

internal sealed record WorkerPaymentAllocationRow(int LineId, int SheetId, int ProjectId, DateOnly WorkDate, Shift Shift, decimal Amount);

/// <summary>The worker payment queries, as <see cref="IQueryable{T}"/> so tests can prove they translate to SQL.</summary>
internal static class WorkerPaymentQueries
{
    /// <summary>
    /// A worker's payments, newest first. Voided ones only when asked for. With <paramref name="projectIds"/>, only
    /// the payments that settled at least one day on one of those projects (the projects the caller may see).
    /// </summary>
    public static IQueryable<WorkerPaymentRow> OfWorker(WorkforceDbContext db, int workerId, bool includeVoided, IReadOnlyCollection<int>? projectIds = null)
    {
        var query = db.WorkerPayments.AsNoTracking().Where(payment => payment.WorkerId == workerId);
        if (!includeVoided)
        {
            query = query.Where(payment => payment.VoidedAt == null);
        }

        if (projectIds is not null)
        {
            query = query.Where(payment => payment.Allocations.Any(allocation => projectIds.Contains(allocation.Line.Sheet.ProjectId)));
        }

        return Project(query.OrderByDescending(payment => payment.PaymentDate).ThenByDescending(payment => payment.Id));
    }

    public static IQueryable<WorkerPaymentRow> One(WorkforceDbContext db, int paymentId) =>
        Project(db.WorkerPayments.AsNoTracking().Where(payment => payment.Id == paymentId));

    /// <summary>The payment recorded with this client key, whatever the worker, or none.</summary>
    public static IQueryable<WorkerPayment> ByKey(WorkforceDbContext db, Guid idempotencyKey) =>
        db.WorkerPayments.AsNoTracking().Where(payment => payment.IdempotencyKey == idempotencyKey);

    /// <summary>
    /// The worker's approved, non-void lines on every project, flat: what can be paid. The amounts already paid
    /// come from <see cref="PaidParts"/>, so the void rule lives in one pure place (<see cref="PaymentFifo.Settle"/>).
    /// </summary>
    public static IQueryable<PayableLine> PayableLines(WorkforceDbContext db, int workerId) =>
        db.AttendanceLines.AsNoTracking()
            .Where(line => line.WorkerId == workerId && !line.IsVoid && line.Sheet.Status == SheetStatus.Approved && line.NetAmount != null)
            .Select(line => new PayableLine(line.Id, line.Sheet.ProjectId, line.WorkDate, line.Days, line.NetAmount!.Value));

    /// <summary>The projects the worker's payments settled days on, voided payments included.</summary>
    public static IQueryable<int> PaidProjectIds(WorkforceDbContext db, int workerId) =>
        db.WorkerPaymentAllocations.AsNoTracking()
            .Where(allocation => allocation.Payment.WorkerId == workerId)
            .Select(allocation => allocation.Line.Sheet.ProjectId)
            .Distinct();

    /// <summary>How many of the worker's lines are on submitted or returned sheets, by project: not pay yet, shown apart.</summary>
    public static IQueryable<PendingLineCount> PendingLinesByProject(WorkforceDbContext db, int workerId) =>
        db.AttendanceLines.AsNoTracking()
            .Where(line => line.WorkerId == workerId && !line.IsVoid
                && (line.Sheet.Status == SheetStatus.Submitted || line.Sheet.Status == SheetStatus.Returned))
            .GroupBy(line => line.Sheet.ProjectId)
            .Select(group => new PendingLineCount(group.Key, group.Count()));

    /// <summary>Every payment allocation on the worker's lines, with whether its payment is voided.</summary>
    public static IQueryable<PaidPart> PaidParts(WorkforceDbContext db, int workerId) =>
        db.WorkerPaymentAllocations.AsNoTracking()
            .Where(allocation => allocation.Payment.WorkerId == workerId)
            .Select(allocation => new PaidPart(allocation.LineId, allocation.Amount, allocation.Payment.VoidedAt != null));

    private static IQueryable<WorkerPaymentRow> Project(IQueryable<WorkerPayment> query) =>
        query.Select(payment => new WorkerPaymentRow
        {
            Id = payment.Id,
            WorkerId = payment.WorkerId,
            WorkerNameAr = payment.Worker.NameAr,
            WorkerNameEn = payment.Worker.NameEn,
            PaymentDate = payment.PaymentDate,
            Amount = payment.Amount,
            Method = payment.Method,
            Reference = payment.Reference,
            Notes = payment.Notes,
            IdempotencyKey = payment.IdempotencyKey,
            VoidedAt = payment.VoidedAt,
            VoidedBy = payment.VoidedBy,
            VoidReason = payment.VoidReason,
            CreatedAt = payment.CreatedAt,
            CreatedBy = payment.CreatedBy,
            RowVersion = payment.RowVersion,
            Allocations = payment.Allocations
                .OrderBy(allocation => allocation.Line.WorkDate)
                .ThenBy(allocation => allocation.LineId)
                .Select(allocation => new WorkerPaymentAllocationRow(
                    allocation.LineId,
                    allocation.Line.SheetId,
                    allocation.Line.Sheet.ProjectId,
                    allocation.Line.WorkDate,
                    allocation.Line.Shift,
                    allocation.Amount))
                .ToList(),
        });
}

/// <summary>Builds a payment view for the caller, leaving out the parts on projects the caller can't see.</summary>
internal static class WorkerPaymentVisibility
{
    public static async Task<WorkerPaymentView> ViewForAsync(
        WorkforceDbContext db, IProjectAccess access, System.Security.Claims.ClaimsPrincipal caller, int paymentId, CancellationToken cancellationToken)
    {
        var row = await WorkerPaymentQueries.One(db, paymentId).SingleAsync(cancellationToken);
        var projectIds = row.Allocations.Select(allocation => allocation.ProjectId).Distinct().ToList();
        var visible = await ProjectVisibility.AmongAsync(access, caller, projectIds, cancellationToken);
        return WorkerPaymentView.From(row, visible.Count == projectIds.Count ? null : visible);
    }
}
