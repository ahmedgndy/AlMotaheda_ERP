using Microsoft.EntityFrameworkCore;
using AlMotaheda.BuildingBlocks.Domain;
namespace AlMotaheda.Modules.Subcontractors.Data;

/// <summary>
/// A subcontractor company: one row for the whole company list, used by many projects (overview 3.6). What is
/// project-specific is the <see cref="SubContract"/>. A subcontractor is not a supplier: suppliers sell materials,
/// subcontractors do work under a contract, and the two are never merged.
/// </summary>
internal sealed class Subcontractor
{
    public int Id { get; set; }

    public string NameAr { get; set; } = "";

    /// <summary>Unique: the company list can't hold the same company twice.</summary>
    public string NameEn { get; set; } = "";

    public SubcontractorSpecialty Specialty { get; set; }

    public string? ContactName { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Address { get; set; }
    public string? Notes { get; set; }

    /// <summary>An inactive company keeps its contracts and history, but can't be given a new contract.</summary>
    public SubcontractorStatus Status { get; set; }

    public DateTime CreatedAt { get; set; }
    public int CreatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public int? UpdatedBy { get; set; }

    public byte[] RowVersion { get; set; } = [];
}

/// <summary>The kind of work a company does. Stored by name.</summary>
internal enum SubcontractorSpecialty
{
    Civil,
    Masonry,
    Plumbing,
    Electrical,
    Mep,
    Finishing,
    Earthworks,
    Steel,
    Facade,
    Landscape,
    Other,
}

internal enum SubcontractorStatus
{
    Active,
    Inactive,
}

/// <summary>
/// One company's engagement on one project: a contract for one main work item (or, for older contracts, for the
/// project as a whole). The contract value is a commitment, never a cost: cost is only the approved measured work
/// and manpower recorded under it. One contract per company, project and main work item, so the same company can
/// work on several main items and several projects, each engagement with its own value, scope and records, and one
/// company identity.
/// </summary>
/// <remarks>
/// The value that counts (<c>ContractValues.Effective</c>) is <see cref="Value"/> plus the approved
/// <see cref="ContractVariation"/>s: a variation changes the contract with an approval and an audit trail.
/// </remarks>
internal sealed class SubContract
{
    public int Id { get; set; }

    public int SubcontractorId { get; set; }
    public Subcontractor Subcontractor { get; set; } = null!;

    /// <summary><c>projects.Project</c>. A plain column here: the foreign key is written by hand in the migration.</summary>
    public int ProjectId { get; set; }

    /// <summary>
    /// The main work item ("بند رئيسي") this engagement is for, on the same project (checked through
    /// <c>IProjectItems</c>). Its contract items are linked to it or to one of its sub-items. Empty for contracts made
    /// before work items had a hierarchy: those cover the project as a whole.
    /// </summary>
    public int? ProjectItemId { get; set; }

    /// <summary>The company's contract number, when there is one. Unique when set.</summary>
    public string? ContractNo { get; set; }

    public ContractModel Model { get; set; }

    /// <summary>What was signed. For <see cref="ContractModel.DailyRate"/> it is the ceiling of the manpower sheets.</summary>
    public decimal Value { get; set; }

    /// <summary>Pay per worker per day. Set exactly when the model is <see cref="ContractModel.DailyRate"/>.</summary>
    public decimal? DailyRate { get; set; }

    /// <summary>0 to 100. Shown on the contract account only (decision S2); never deducted from an item.</summary>
    public decimal RetentionPercent { get; set; }

    public DateOnly StartDate { get; set; }
    public DateOnly? EndDate { get; set; }

    public ContractStatus Status { get; set; }

    public string? Scope { get; set; }

    public DateTime CreatedAt { get; set; }
    public int CreatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public int? UpdatedBy { get; set; }

    public byte[] RowVersion { get; set; } = [];

    public List<ContractItem> Items { get; } = [];

    public List<ContractVariation> Variations { get; } = [];

    public bool IsCompleted => Status == ContractStatus.Completed;
}

/// <summary>
/// A change of an agreed contract value ("أمر تغيير"): more work (positive) or less (negative), with its reason. It
/// counts only once someone other than its requester approves it (subcontractors Approve); a decided row never
/// changes, so the contract's history stays readable.
/// </summary>
internal sealed class ContractVariation
{
    public int Id { get; set; }

    public int ContractId { get; set; }
    public SubContract Contract { get; set; } = null!;

    /// <summary>The contract's project, copied so the audit trail and the live updates know the project.</summary>
    public int ProjectId { get; set; }

    /// <summary>Signed: added (positive) or removed (negative) contract value. Never zero.</summary>
    public decimal Amount { get; set; }

    public required string Reason { get; set; }

    /// <summary>The variation's own reference (the signed change order), when there is one.</summary>
    public string? Reference { get; set; }

    private VariationStatus _status = VariationStatus.Pending;

    public VariationStatus Status { get => _status; init => _status = value; }

    public DateTime RequestedAt { get; set; }
    public int RequestedBy { get; set; }

    public DateTime? DecidedAt { get; set; }
    public int? DecidedBy { get; set; }
    public string? DecisionNote { get; set; }

    private static readonly AlMotaheda.BuildingBlocks.Domain.StatusFlow<VariationStatus> Flow = new(
        (VariationStatus.Pending, VariationStatus.Approved),
        (VariationStatus.Pending, VariationStatus.Rejected));

    public void Decide(VariationStatus outcome, int deciderId, DateTime now, string? note)
    {
        _status = Flow.Move(_status, outcome);
        DecidedAt = now;
        DecidedBy = deciderId;
        DecisionNote = note;
    }
}

internal enum VariationStatus
{
    /// <summary>Waiting for a decision. Not in the contract value.</summary>
    Pending,
    Approved,
    Rejected,
}

/// <summary>The one rule for "what is this contract worth now": the signed value plus its approved variations.</summary>
internal static class ContractValues
{
    /// <summary>The approved variations of a contract, as a subquery any projection can use.</summary>
    public static IQueryable<decimal> ApprovedVariations(SubcontractorsDbContext db, int contractId) =>
        db.ContractVariations.Where(variation => variation.ContractId == contractId && variation.Status == VariationStatus.Approved)
            .Select(variation => variation.Amount);

    /// <summary>Signed value + approved variations, read in one query.</summary>
    public static Task<decimal> EffectiveAsync(SubcontractorsDbContext db, int contractId, CancellationToken cancellationToken) =>
        db.SubContracts.AsNoTracking()
            .Where(contract => contract.Id == contractId)
            .Select(contract => contract.Value + (db.ContractVariations
                .Where(variation => variation.ContractId == contract.Id && variation.Status == VariationStatus.Approved)
                .Sum(variation => (decimal?)variation.Amount) ?? 0))
            .SingleAsync(cancellationToken);
}

/// <summary>How the work of a contract is priced. Stored by name.</summary>
internal enum ContractModel
{
    /// <summary>Contract items priced per unit; measured work is paid by quantity.</summary>
    UnitRate,

    /// <summary>A lump sum split into items (usually one Job or Lot); measured as a part of each line.</summary>
    LumpSum,

    /// <summary>A crew paid per worker per day; recorded as manpower sheets up to the contract value.</summary>
    DailyRate,
}

internal enum ContractStatus
{
    /// <summary>Not signed yet.</summary>
    Pending,

    Active,

    /// <summary>Finished: no new work is recorded, and its remaining value is no longer a commitment.</summary>
    Completed,
}

/// <summary>
/// One line of contracted work: a name, a unit, a quantity and a unit price. Its approved measurements are the
/// cost. When it points to a project item, all of its approved measurements count on that item; the link is the
/// only thing that moves, never an amount.
/// </summary>
internal sealed class ContractItem
{
    public int Id { get; set; }

    public int ContractId { get; set; }
    public SubContract Contract { get; set; } = null!;

    /// <summary><c>projects.ProjectItem</c> of the same project, or <c>null</c>. Foreign key written by hand in the migration.</summary>
    public int? ProjectItemId { get; set; }

    public string Name { get; set; } = "";

    /// <summary>One of <c>WorkTypes</c>.</summary>
    public string WorkType { get; set; } = "";

    /// <summary>One of <c>MeasurementUnits</c>.</summary>
    public string Unit { get; set; } = "";

    public decimal Quantity { get; set; }

    /// <summary>The current price. A measurement copies it when it is recorded, so a change only affects new measurements.</summary>
    public decimal UnitPrice { get; set; }

    public string? Description { get; set; }
    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; }
    public int CreatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public int? UpdatedBy { get; set; }

    public byte[] RowVersion { get; set; } = [];
}

/// <summary>
/// Completed work measured on site for one contract item. Recorded by one person, approved or rejected by
/// another (decision S1). Only an approved measurement is cost.
/// </summary>
internal sealed class ProgressMeasurement
{
    public int Id { get; set; }

    public int ContractItemId { get; set; }
    public ContractItem ContractItem { get; set; } = null!;

    public DateOnly MeasuredOn { get; set; }

    public decimal Quantity { get; set; }

    /// <summary>Copied from the contract item when recorded.</summary>
    public decimal UnitPrice { get; set; }

    /// <summary>round(Quantity × UnitPrice), computed by the server.</summary>
    public decimal Amount { get; set; }

    private WorkStatus _status;

    /// <summary>Set when the record is created; afterwards only this class's methods change it, through <see cref="Flow"/>.</summary>
    public WorkStatus Status { get => _status; init => _status = value; }

    public string? Note { get; set; }

    public DateTime? ReviewedAt { get; set; }
    public int? ReviewedBy { get; set; }
    public string? ReviewNote { get; set; }

    public DateTime CreatedAt { get; set; }
    public int CreatedBy { get; set; }

    public byte[] RowVersion { get; set; } = [];

    /// <summary>Recorded work is approved or rejected once (decision S1).</summary>
    private static readonly StatusFlow<WorkStatus> Flow = new(
        (WorkStatus.Recorded, WorkStatus.Approved),
        (WorkStatus.Recorded, WorkStatus.Rejected));

    public void Review(WorkStatus outcome, int reviewerId, DateTime now, string? note)
    {
        _status = Flow.Move(_status, outcome);
        ReviewedAt = now;
        ReviewedBy = reviewerId;
        ReviewNote = note;
    }
}

/// <summary>
/// A daily-rate crew over a period: workers × days × daily rate. Only on <see cref="ContractModel.DailyRate"/>
/// contracts. Recorded, then approved or rejected by someone else; only an approved sheet is cost.
/// </summary>
internal sealed class ManpowerSheet
{
    public int Id { get; set; }

    public int ContractId { get; set; }
    public SubContract Contract { get; set; } = null!;

    /// <summary><c>projects.ProjectItem</c> of the same project, or <c>null</c>. Foreign key written by hand in the migration.</summary>
    public int? ProjectItemId { get; set; }

    public DateOnly FromDate { get; set; }

    /// <summary>The business date of the sheet: work is cost on the last day of its period.</summary>
    public DateOnly ToDate { get; set; }

    public int Workers { get; set; }
    public decimal Days { get; set; }
    public decimal DailyRate { get; set; }

    /// <summary>round(Workers × Days × DailyRate), computed by the server.</summary>
    public decimal Amount { get; set; }

    /// <summary>One of <c>WorkTypes</c>.</summary>
    public string WorkType { get; set; } = "";

    public string? Note { get; set; }

    private WorkStatus _status;

    /// <summary>Set when the record is created; afterwards only this class's methods change it, through <see cref="Flow"/>.</summary>
    public WorkStatus Status { get => _status; init => _status = value; }

    public DateTime? ReviewedAt { get; set; }
    public int? ReviewedBy { get; set; }
    public string? ReviewNote { get; set; }

    public DateTime CreatedAt { get; set; }
    public int CreatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public int? UpdatedBy { get; set; }

    public byte[] RowVersion { get; set; } = [];

    /// <summary>Recorded work is approved or rejected once (decision S1).</summary>
    private static readonly StatusFlow<WorkStatus> Flow = new(
        (WorkStatus.Recorded, WorkStatus.Approved),
        (WorkStatus.Recorded, WorkStatus.Rejected));

    public void Review(WorkStatus outcome, int reviewerId, DateTime now, string? note)
    {
        _status = Flow.Move(_status, outcome);
        ReviewedAt = now;
        ReviewedBy = reviewerId;
        ReviewNote = note;
    }
}

/// <summary>The review state of a measurement or a manpower sheet. Stored by name.</summary>
internal enum WorkStatus
{
    Recorded,
    Approved,
    Rejected,
}

/// <summary>
/// Money paid to a subcontractor under one contract. A fact: never edited or deleted; a wrong payment is voided.
/// It settles the contract's oldest approved, unpaid work first; what is left over is an unapplied advance,
/// applied to work approved later (see <c>SubSettlement</c>).
/// </summary>
internal sealed class SubPayment
{
    public int Id { get; set; }

    public int ContractId { get; set; }
    public SubContract Contract { get; set; } = null!;

    public DateOnly PaymentDate { get; set; }

    public decimal Amount { get; set; }

    public SubPaymentMethod Method { get; set; }

    public SubPaymentPurpose Purpose { get; set; }

    public string? Reference { get; set; }
    public string? Notes { get; set; }

    /// <summary>Made by the client for each payment form. The same key twice returns the first payment.</summary>
    public Guid IdempotencyKey { get; set; }

    /// <summary>Set together with <see cref="VoidedBy"/> and <see cref="VoidReason"/>. A voided payment pays nothing.</summary>
    public DateTime? VoidedAt { get; set; }
    public int? VoidedBy { get; set; }
    public string? VoidReason { get; set; }

    public DateTime CreatedAt { get; set; }
    public int CreatedBy { get; set; }

    public byte[] RowVersion { get; set; } = [];

    public List<SubPaymentAllocation> Allocations { get; } = [];

    public bool IsVoided => VoidedAt is not null;
}

internal enum SubPaymentMethod
{
    BankTransfer,
    Cheque,
    Cash,
}

/// <summary>Why the money was paid. Shown only; the settlement rule is the same for every purpose.</summary>
internal enum SubPaymentPurpose
{
    Advance,
    Progress,
    Final,
    Retention,
}

/// <summary>
/// The part of one payment that settles one piece of approved work: a measurement or a manpower sheet, never both.
/// The allocations on a piece of work (payments not voided) never exceed its amount; the server checks this under
/// the project lock.
/// </summary>
/// <remarks>
/// One row per payment and piece of work. When unapplied money of a payment is applied again to the same work
/// (after another payment was voided), the existing row grows instead of a second row being added.
/// </remarks>
internal sealed class SubPaymentAllocation
{
    public int Id { get; set; }

    public int PaymentId { get; set; }
    public SubPayment Payment { get; set; } = null!;

    public int? MeasurementId { get; set; }
    public ProgressMeasurement? Measurement { get; set; }

    public int? ManpowerSheetId { get; set; }
    public ManpowerSheet? ManpowerSheet { get; set; }

    public decimal Amount { get; set; }

    /// <summary>When the allocation was first made: at the payment, or later when work was approved.</summary>
    public DateTime CreatedAt { get; set; }
}
