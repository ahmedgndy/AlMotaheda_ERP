using AlMotaheda.Modules.Subcontractors.Data;
using AlMotaheda.Modules.Subcontractors.Features.Accounts;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Subcontractors.Features.Contracts;

/// <summary>One contract with its company, as the list and the detail read it.</summary>
internal sealed class ContractListRow
{
    public int Id { get; init; }
    public int ProjectId { get; init; }
    public int SubcontractorId { get; init; }
    public string SubcontractorNameAr { get; init; } = "";
    public string SubcontractorNameEn { get; init; } = "";
    public SubcontractorSpecialty Specialty { get; init; }
    public SubcontractorStatus SubcontractorStatus { get; init; }
    public string? ContractNo { get; init; }
    public ContractModel Model { get; init; }
    public decimal Value { get; init; }
    public decimal? DailyRate { get; init; }
    public decimal RetentionPercent { get; init; }
    public DateOnly StartDate { get; init; }
    public DateOnly? EndDate { get; init; }
    public ContractStatus Status { get; init; }
    public string? Scope { get; init; }
    public int ItemCount { get; init; }
    public int? ProjectItemId { get; init; }

    /// <summary>The approved variations: the contract is worth <see cref="Value"/> + this.</summary>
    public decimal ApprovedVariations { get; init; }

    public DateTime CreatedAt { get; init; }
    public int CreatedBy { get; init; }
    public DateTime? UpdatedAt { get; init; }
    public int? UpdatedBy { get; init; }
    public byte[] RowVersion { get; init; } = [];

    /// <summary>The terms every calculation uses: the effective value (signed + approved variations).</summary>
    public ContractRow Terms => new(Id, SubcontractorId, Value + ApprovedVariations, RetentionPercent, Status);
}

/// <summary>One measurement with its contract item, for the detail screen.</summary>
internal sealed class MeasurementRow
{
    public int Id { get; init; }
    public int ContractId { get; init; }
    public int SubcontractorId { get; init; }
    public int ContractItemId { get; init; }
    public string ContractItemName { get; init; } = "";
    public string Unit { get; init; } = "";
    public decimal ItemQuantity { get; init; }
    public int? ProjectItemId { get; init; }
    public DateOnly MeasuredOn { get; init; }
    public decimal Quantity { get; init; }
    public decimal UnitPrice { get; init; }
    public decimal Amount { get; init; }
    public WorkStatus Status { get; init; }
    public string? Note { get; init; }
    public DateTime? ReviewedAt { get; init; }
    public int? ReviewedBy { get; init; }
    public string? ReviewNote { get; init; }
    public DateTime CreatedAt { get; init; }
    public int CreatedBy { get; init; }
    public byte[] RowVersion { get; init; } = [];

    /// <summary>The allocations of payments that are not voided.</summary>
    public decimal Allocated { get; init; }

    public WorkRow Work => new(WorkKind.Measurement, Id, ContractId, SubcontractorId, ContractItemId, ProjectItemId, MeasuredOn, Amount, Status, Allocated);
}

/// <summary>One manpower sheet, for the detail screen.</summary>
internal sealed class ManpowerRow
{
    public int Id { get; init; }
    public int ContractId { get; init; }
    public int SubcontractorId { get; init; }
    public int? ProjectItemId { get; init; }
    public DateOnly FromDate { get; init; }
    public DateOnly ToDate { get; init; }
    public int Workers { get; init; }
    public decimal Days { get; init; }
    public decimal DailyRate { get; init; }
    public decimal Amount { get; init; }
    public string WorkType { get; init; } = "";
    public string? Note { get; init; }
    public WorkStatus Status { get; init; }
    public DateTime? ReviewedAt { get; init; }
    public int? ReviewedBy { get; init; }
    public string? ReviewNote { get; init; }
    public DateTime CreatedAt { get; init; }
    public int CreatedBy { get; init; }
    public DateTime? UpdatedAt { get; init; }
    public int? UpdatedBy { get; init; }
    public byte[] RowVersion { get; init; } = [];

    /// <summary>The allocations of payments that are not voided.</summary>
    public decimal Allocated { get; init; }

    public WorkRow Work => new(WorkKind.Manpower, Id, ContractId, SubcontractorId, null, ProjectItemId, ToDate, Amount, Status, Allocated);
}

/// <summary>One allocation of a payment, as shown on the payment.</summary>
internal sealed record AllocationRef(int? MeasurementId, int? ManpowerSheetId, decimal Amount);

/// <summary>One payment with its allocations, for the detail screen.</summary>
internal sealed class PaymentDetailRow
{
    public int Id { get; init; }
    public int ContractId { get; init; }
    public DateOnly PaymentDate { get; init; }
    public decimal Amount { get; init; }
    public SubPaymentMethod Method { get; init; }
    public SubPaymentPurpose Purpose { get; init; }
    public string? Reference { get; init; }
    public string? Notes { get; init; }
    public DateTime? VoidedAt { get; init; }
    public int? VoidedBy { get; init; }
    public string? VoidReason { get; init; }
    public DateTime CreatedAt { get; init; }
    public int CreatedBy { get; init; }
    public byte[] RowVersion { get; init; } = [];
    public List<AllocationRef> Allocations { get; init; } = [];

    public bool IsVoided => VoidedAt is not null;

    public PaymentRow Figures => new(Id, ContractId, PaymentDate, Amount, IsVoided, Allocations.Sum(allocation => allocation.Amount));
}

/// <summary>The contract screens' queries, as <see cref="IQueryable{T}"/> so tests can prove they translate.</summary>
internal static class ContractQueries
{
    /// <summary>The contracts of a project with their company, ordered by company name.</summary>
    public static IQueryable<ContractListRow> Rows(SubcontractorsDbContext db, int projectId, ContractStatus? status = null)
    {
        var query = db.SubContracts.AsNoTracking().Where(contract => contract.ProjectId == projectId);
        if (status is { } wanted)
        {
            query = query.Where(contract => contract.Status == wanted);
        }

        return Project(query.OrderBy(contract => contract.Subcontractor.NameEn).ThenBy(contract => contract.Id));
    }

    /// <summary>One contract of a project, or none when the id belongs to another project.</summary>
    public static IQueryable<ContractListRow> One(SubcontractorsDbContext db, int projectId, int contractId) =>
        Project(db.SubContracts.AsNoTracking().Where(contract => contract.ProjectId == projectId && contract.Id == contractId));

    /// <summary>The items of one contract of a project, in the order they were added.</summary>
    public static IQueryable<ContractItem> Items(SubcontractorsDbContext db, int projectId, int contractId) =>
        db.ContractItems.AsNoTracking()
            .Where(item => item.ContractId == contractId && item.Contract.ProjectId == projectId)
            .OrderBy(item => item.Id);

    /// <summary>The measurements of one contract of a project, newest first.</summary>
    public static IQueryable<MeasurementRow> Measurements(SubcontractorsDbContext db, int projectId, int contractId) =>
        ProjectMeasurements(db, db.ProgressMeasurements.AsNoTracking()
            .Where(measurement => measurement.ContractItem.ContractId == contractId && measurement.ContractItem.Contract.ProjectId == projectId)
            .OrderByDescending(measurement => measurement.MeasuredOn)
            .ThenByDescending(measurement => measurement.Id));

    /// <summary>One measurement of a project, or none when it belongs to another project.</summary>
    public static IQueryable<MeasurementRow> Measurement(SubcontractorsDbContext db, int projectId, int measurementId) =>
        ProjectMeasurements(db, db.ProgressMeasurements.AsNoTracking()
            .Where(measurement => measurement.Id == measurementId && measurement.ContractItem.Contract.ProjectId == projectId));

    /// <summary>The manpower sheets of one contract of a project, newest first.</summary>
    public static IQueryable<ManpowerRow> ManpowerSheets(SubcontractorsDbContext db, int projectId, int contractId) =>
        ProjectSheets(db, db.ManpowerSheets.AsNoTracking()
            .Where(sheet => sheet.ContractId == contractId && sheet.Contract.ProjectId == projectId)
            .OrderByDescending(sheet => sheet.ToDate)
            .ThenByDescending(sheet => sheet.Id));

    /// <summary>One manpower sheet of a project, or none when it belongs to another project.</summary>
    public static IQueryable<ManpowerRow> ManpowerSheet(SubcontractorsDbContext db, int projectId, int sheetId) =>
        ProjectSheets(db, db.ManpowerSheets.AsNoTracking().Where(sheet => sheet.Id == sheetId && sheet.Contract.ProjectId == projectId));

    /// <summary>The payments of one contract of a project, voided ones included, newest first.</summary>
    public static IQueryable<PaymentDetailRow> Payments(SubcontractorsDbContext db, int projectId, int contractId) =>
        ProjectPayments(db.SubPayments.AsNoTracking()
            .Where(payment => payment.ContractId == contractId && payment.Contract.ProjectId == projectId)
            .OrderByDescending(payment => payment.PaymentDate)
            .ThenByDescending(payment => payment.Id));

    /// <summary>One payment of a project, or none when it belongs to another project.</summary>
    public static IQueryable<PaymentDetailRow> Payment(SubcontractorsDbContext db, int projectId, int paymentId) =>
        ProjectPayments(db.SubPayments.AsNoTracking().Where(payment => payment.Id == paymentId && payment.Contract.ProjectId == projectId));

    /// <summary>The payment made with this client key, on any contract.</summary>
    public static IQueryable<SubPayment> ByKey(SubcontractorsDbContext db, Guid idempotencyKey) =>
        db.SubPayments.AsNoTracking().Where(payment => payment.IdempotencyKey == idempotencyKey);

    private static IQueryable<ContractListRow> Project(IQueryable<SubContract> query) =>
        query.Select(contract => new ContractListRow
        {
            Id = contract.Id,
            ProjectId = contract.ProjectId,
            SubcontractorId = contract.SubcontractorId,
            SubcontractorNameAr = contract.Subcontractor.NameAr,
            SubcontractorNameEn = contract.Subcontractor.NameEn,
            Specialty = contract.Subcontractor.Specialty,
            SubcontractorStatus = contract.Subcontractor.Status,
            ContractNo = contract.ContractNo,
            Model = contract.Model,
            Value = contract.Value,
            DailyRate = contract.DailyRate,
            RetentionPercent = contract.RetentionPercent,
            StartDate = contract.StartDate,
            EndDate = contract.EndDate,
            Status = contract.Status,
            Scope = contract.Scope,
            ItemCount = contract.Items.Count,
            ProjectItemId = contract.ProjectItemId,
            ApprovedVariations = contract.Variations.Where(variation => variation.Status == VariationStatus.Approved)
                .Sum(variation => (decimal?)variation.Amount) ?? 0,
            CreatedAt = contract.CreatedAt,
            CreatedBy = contract.CreatedBy,
            UpdatedAt = contract.UpdatedAt,
            UpdatedBy = contract.UpdatedBy,
            RowVersion = contract.RowVersion,
        });

    private static IQueryable<MeasurementRow> ProjectMeasurements(SubcontractorsDbContext db, IQueryable<ProgressMeasurement> query) =>
        query.Select(measurement => new MeasurementRow
        {
            Id = measurement.Id,
            ContractId = measurement.ContractItem.ContractId,
            SubcontractorId = measurement.ContractItem.Contract.SubcontractorId,
            ContractItemId = measurement.ContractItemId,
            ContractItemName = measurement.ContractItem.Name,
            Unit = measurement.ContractItem.Unit,
            ItemQuantity = measurement.ContractItem.Quantity,
            ProjectItemId = measurement.ContractItem.ProjectItemId,
            MeasuredOn = measurement.MeasuredOn,
            Quantity = measurement.Quantity,
            UnitPrice = measurement.UnitPrice,
            Amount = measurement.Amount,
            Status = measurement.Status,
            Note = measurement.Note,
            ReviewedAt = measurement.ReviewedAt,
            ReviewedBy = measurement.ReviewedBy,
            ReviewNote = measurement.ReviewNote,
            CreatedAt = measurement.CreatedAt,
            CreatedBy = measurement.CreatedBy,
            RowVersion = measurement.RowVersion,
            Allocated = db.SubPaymentAllocations
                .Where(allocation => allocation.MeasurementId == measurement.Id && allocation.Payment.VoidedAt == null)
                .Sum(allocation => allocation.Amount),
        });

    private static IQueryable<ManpowerRow> ProjectSheets(SubcontractorsDbContext db, IQueryable<ManpowerSheet> query) =>
        query.Select(sheet => new ManpowerRow
        {
            Id = sheet.Id,
            ContractId = sheet.ContractId,
            SubcontractorId = sheet.Contract.SubcontractorId,
            ProjectItemId = sheet.ProjectItemId,
            FromDate = sheet.FromDate,
            ToDate = sheet.ToDate,
            Workers = sheet.Workers,
            Days = sheet.Days,
            DailyRate = sheet.DailyRate,
            Amount = sheet.Amount,
            WorkType = sheet.WorkType,
            Note = sheet.Note,
            Status = sheet.Status,
            ReviewedAt = sheet.ReviewedAt,
            ReviewedBy = sheet.ReviewedBy,
            ReviewNote = sheet.ReviewNote,
            CreatedAt = sheet.CreatedAt,
            CreatedBy = sheet.CreatedBy,
            UpdatedAt = sheet.UpdatedAt,
            UpdatedBy = sheet.UpdatedBy,
            RowVersion = sheet.RowVersion,
            Allocated = db.SubPaymentAllocations
                .Where(allocation => allocation.ManpowerSheetId == sheet.Id && allocation.Payment.VoidedAt == null)
                .Sum(allocation => allocation.Amount),
        });

    private static IQueryable<PaymentDetailRow> ProjectPayments(IQueryable<SubPayment> query) =>
        query.Select(payment => new PaymentDetailRow
        {
            Id = payment.Id,
            ContractId = payment.ContractId,
            PaymentDate = payment.PaymentDate,
            Amount = payment.Amount,
            Method = payment.Method,
            Purpose = payment.Purpose,
            Reference = payment.Reference,
            Notes = payment.Notes,
            VoidedAt = payment.VoidedAt,
            VoidedBy = payment.VoidedBy,
            VoidReason = payment.VoidReason,
            CreatedAt = payment.CreatedAt,
            CreatedBy = payment.CreatedBy,
            RowVersion = payment.RowVersion,
            Allocations = payment.Allocations
                .OrderBy(allocation => allocation.Id)
                .Select(allocation => new AllocationRef(allocation.MeasurementId, allocation.ManpowerSheetId, allocation.Amount))
                .ToList(),
        });
}
