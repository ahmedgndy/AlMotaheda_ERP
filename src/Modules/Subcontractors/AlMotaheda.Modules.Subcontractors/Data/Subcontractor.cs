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
/// One company's contract on one project. The contract value is a commitment, never a cost: cost is only the
/// approved measured work and manpower recorded under it. One contract per company per project; the same company
/// can have contracts on other projects, each with its own value, scope and records.
/// </summary>
internal sealed class SubContract
{
    public int Id { get; set; }

    public int SubcontractorId { get; set; }
    public Subcontractor Subcontractor { get; set; } = null!;

    /// <summary><c>projects.Project</c>. A plain column here: the foreign key is written by hand in the migration.</summary>
    public int ProjectId { get; set; }

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

    public bool IsCompleted => Status == ContractStatus.Completed;
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

    public WorkStatus Status { get; set; }

    public string? Note { get; set; }

    public DateTime? ReviewedAt { get; set; }
    public int? ReviewedBy { get; set; }
    public string? ReviewNote { get; set; }

    public DateTime CreatedAt { get; set; }
    public int CreatedBy { get; set; }

    public byte[] RowVersion { get; set; } = [];
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

    public WorkStatus Status { get; set; }

    public DateTime? ReviewedAt { get; set; }
    public int? ReviewedBy { get; set; }
    public string? ReviewNote { get; set; }

    public DateTime CreatedAt { get; set; }
    public int CreatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public int? UpdatedBy { get; set; }

    public byte[] RowVersion { get; set; } = [];
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
