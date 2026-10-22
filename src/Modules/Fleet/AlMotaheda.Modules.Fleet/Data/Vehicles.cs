using AlMotaheda.BuildingBlocks.Domain;

namespace AlMotaheda.Modules.Fleet.Data;

/// <summary>
/// A vehicle: an <see cref="Equipment"/> row of category <see cref="EquipmentCategory.Vehicle"/> (the one register,
/// with its asset number, names, ownership, condition and status) extended with what only a vehicle has: the plate,
/// the odometer, the driver (through <see cref="VehicleDriverAssignment"/> history), its custody and its GPS status.
/// The key is the equipment's id.
/// </summary>
internal sealed class Vehicle
{
    /// <summary>The same id as the equipment row.</summary>
    public int Id { get; set; }
    public Equipment Equipment { get; set; } = null!;

    /// <summary>The plate number, upper-case, unique.</summary>
    public string Plate { get; set; } = "";

    public VehicleType Type { get; set; }

    public int? ModelYear { get; set; }

    public VehicleFuelType FuelType { get; set; }

    /// <summary>The last trusted odometer reading (an approved claim, a hand-over). Never decreases.</summary>
    public decimal OdometerKm { get; set; }
    public DateTime? OdometerUpdatedAt { get; set; }
    public int? OdometerUpdatedBy { get; set; }

    /// <summary>The GPS provider the vehicle's tracker reports to, when one is configured, and its device id.</summary>
    public string? GpsProvider { get; set; }
    public string? GpsDeviceId { get; set; }

    public VehicleGpsStatus GpsStatus { get; set; } = VehicleGpsStatus.NotConfigured;

    /// <summary>
    /// Whether the phone of the vehicle's assigned driver records the vehicle's positions (after the driver accepts
    /// the tracking notice). On by default; a fleet manager turns it off and the phone stops at its next check.
    /// </summary>
    public bool TrackingEnabled { get; set; } = true;

    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; }
    public int CreatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public int? UpdatedBy { get; set; }

    public byte[] RowVersion { get; set; } = [];
}

/// <summary>Stored by name.</summary>
internal enum VehicleType
{
    Car,
    Pickup,
    Van,
    Truck,
    Bus,
    Motorcycle,
    Other,
}

/// <summary>Stored by name.</summary>
internal enum VehicleFuelType
{
    Petrol,
    Diesel,
    Gas,
    Electric,
}

/// <summary>Stored by name.</summary>
internal enum VehicleGpsStatus
{
    /// <summary>No tracker, or no provider configured: the location endpoints say so.</summary>
    NotConfigured,

    /// <summary>A tracker is registered with a configured provider.</summary>
    Configured,

    /// <summary>A tracker is registered but the provider doesn't answer for it.</summary>
    Unavailable,
}

/// <summary>
/// One stay of a driver (an <c>hr.Employee</c>) at the wheel of a vehicle, from a start date to an end date. The
/// open row says who answers for the vehicle today; a filtered unique index allows one open row per vehicle, and
/// stays never overlap. Ended rows are kept: the driver responsible for a claim or a violation on a day is read here.
/// </summary>
internal sealed class VehicleDriverAssignment
{
    public int Id { get; set; }

    public int VehicleId { get; set; }
    public Vehicle Vehicle { get; set; } = null!;

    /// <summary>The driver (<c>hr.Employee</c>, a foreign key added by hand in the migration).</summary>
    public int DriverEmployeeId { get; set; }

    public DateOnly StartDate { get; set; }
    public DateOnly? EndDate { get; set; }

    public decimal? StartOdometerKm { get; set; }
    public decimal? EndOdometerKm { get; set; }

    public string? Notes { get; set; }

    /// <summary>What was handed over when the stay ended: the vehicle's custody balance and open claims at that moment.</summary>
    public string? HandoverNote { get; set; }
    public decimal? HandoverCustodyBalance { get; set; }
    public decimal? HandoverPendingClaims { get; set; }

    public DateTime AssignedAt { get; set; }
    public int AssignedBy { get; set; }
    public DateTime? EndedAt { get; set; }
    public int? EndedBy { get; set; }

    public byte[] RowVersion { get; set; } = [];

    public bool Covers(DateOnly day) => StartDate <= day && (EndDate is null || EndDate >= day);
}

/// <summary>
/// One movement of a vehicle's operating custody: the cash its driver holds to fuel and maintain it. Append-only,
/// the same pattern as the project custody (<c>projects.CustodyEntry</c>): a funding (a received custody transfer),
/// a return, an expense (a claim: Pending while the claim waits, Posted once approved, Released when rejected or
/// reversed), or a reviewed adjustment. Every entry names the driver accountable at its date.
/// </summary>
/// <remarks>Balance = Σ posted fundings − Σ returns − Σ expenses not released ± adjustments. Pending exposure = Σ pending expenses.</remarks>
internal sealed class VehicleCustodyEntry
{
    public int Id { get; set; }

    public int VehicleId { get; set; }
    public Vehicle Vehicle { get; set; } = null!;

    /// <summary>The driver accountable for the vehicle on the entry's date (from the assignment history), if any.</summary>
    public int? DriverEmployeeId { get; set; }

    public VehicleCustodyKind Kind { get; set; }

    /// <summary>Always positive; <see cref="Kind"/> says which way it moves.</summary>
    public decimal Amount { get; set; }

    public DateOnly EntryDate { get; set; }

    public VehicleCustodyStatus Status { get; set; } = VehicleCustodyStatus.Posted;

    /// <summary>The record behind the movement (<c>CustodyTransfer</c>, <c>VehicleExpense</c>, <c>CustodyAdjustment</c>…), unique with <see cref="SourceId"/>.</summary>
    public string SourceType { get; set; } = "";
    public int SourceId { get; set; }

    public string? Reference { get; set; }
    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; }
    public int CreatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public int? UpdatedBy { get; set; }

    public byte[] RowVersion { get; set; } = [];
}

/// <summary>Stored by name.</summary>
internal enum VehicleCustodyKind
{
    Funding,
    Return,
    Expense,
    AdjustmentIn,
    AdjustmentOut,
}

/// <summary>Stored by name.</summary>
internal enum VehicleCustodyStatus
{
    /// <summary>A claim waiting for review: already shown as exposure, not yet in the posted balance.</summary>
    Pending,
    Posted,

    /// <summary>The claim was rejected or its posting reversed: counts nowhere, kept for the audit.</summary>
    Released,
}

/// <summary>The records a vehicle custody entry comes from.</summary>
internal static class VehicleCustodySources
{
    public const string Transfer = "CustodyTransfer";
    public const string TransferReversal = "CustodyTransferReversal";
    public const string Expense = "VehicleExpense";
    public const string Adjustment = "CustodyAdjustment";

    public static IReadOnlyList<string> All { get; } = [Transfer, TransferReversal, Expense, Adjustment];
}

/// <summary>
/// A vehicle expense claim: fuel (or maintenance, a toll…) the driver paid out of the vehicle's custody, with the
/// odometer photo and the invoice photo as evidence. Submitted → Approved (posted to the custody exactly once, by a
/// reviewer who is never the submitter) | Rejected (with a reason; may be referred to HR) | EvidenceRequested (the
/// submitter resubmits with more evidence) | Escalated (an Executive decides). Fuel claims that name a price item
/// go through Price Control like every other line.
/// </summary>
internal sealed class VehicleExpense
{
    public int Id { get; set; }

    /// <summary><c>VX-000123</c>, computed by the database from the id.</summary>
    public string Number { get; private set; } = "";

    public int VehicleId { get; set; }
    public Vehicle Vehicle { get; set; } = null!;

    /// <summary>The driver who paid: the vehicle's driver on the expense date. Fixed at submission; a later driver change never moves the claim.</summary>
    public int DriverEmployeeId { get; set; }
    public int? DriverAssignmentId { get; set; }

    /// <summary>The project the vehicle was on at the expense date (<c>projects.Project</c>), when it was on one: the cost reaches that project's equipment group.</summary>
    public int? ProjectId { get; set; }

    public VehicleExpenseType Type { get; set; }

    public DateOnly ExpenseDate { get; set; }

    /// <summary>The odometer reading the driver gave, when given: the odometer photo is the evidence either way.</summary>
    public decimal? OdometerKm { get; set; }

    /// <summary>Fuel only: litres and the price per litre; the amount is their product, set by the server.</summary>
    public decimal? FuelQuantity { get; set; }
    public decimal? UnitPrice { get; set; }

    public decimal Amount { get; set; }

    public string? VendorName { get; set; }

    /// <summary>A supplier of the Projects module (<c>projects.Supplier</c>), when the vendor is a known supplier.</summary>
    public int? SupplierId { get; set; }

    public string? InvoiceNo { get; set; }
    public string? Notes { get; set; }

    /// <summary>The Price Control item the fuel was compared with, the comparison as made, and the case it opened.</summary>
    public int? PriceItemId { get; set; }
    public string? PriceCheck { get; set; }
    public decimal? ReferenceMaxPrice { get; set; }
    public string? PriceJustification { get; set; }
    public int? PriceExceptionId { get; set; }

    /// <summary>Where the phone was when the claim was made, when the app had a real fix. Never a vehicle tracker's data.</summary>
    public decimal? GpsLatitude { get; set; }
    public decimal? GpsLongitude { get; set; }
    public DateTime? GpsCapturedAt { get; set; }

    private VehicleExpenseStatus _status = VehicleExpenseStatus.Submitted;

    public VehicleExpenseStatus Status { get => _status; init => _status = value; }

    /// <summary>Increases on every resubmission after an evidence request.</summary>
    public int Round { get; set; } = 1;

    public DateTime SubmittedAt { get; set; }
    public int CreatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public int? UpdatedBy { get; set; }

    public DateTime? ReviewedAt { get; set; }
    public int? ReviewedBy { get; set; }
    public string? ReviewNote { get; set; }

    /// <summary>Posted once, with the custody entry it posted; Reversed keeps both and releases the entry.</summary>
    public VehiclePostingStatus PostingStatus { get; set; } = VehiclePostingStatus.NotPosted;
    public int? CustodyEntryId { get; set; }
    public DateTime? PostedAt { get; set; }
    public DateTime? ReversedAt { get; set; }
    public int? ReversedBy { get; set; }
    public string? ReversalReason { get; set; }

    /// <summary>The HR referral of a rejected claim, when one was made.</summary>
    public int? HrReferralId { get; set; }

    public Guid IdempotencyKey { get; set; }

    public byte[] RowVersion { get; set; } = [];

    public List<VehicleExpenseFlag> Flags { get; } = [];

    private static readonly StatusFlow<VehicleExpenseStatus> Flow = new(
        (VehicleExpenseStatus.Submitted, VehicleExpenseStatus.Approved),
        (VehicleExpenseStatus.Submitted, VehicleExpenseStatus.Rejected),
        (VehicleExpenseStatus.Submitted, VehicleExpenseStatus.EvidenceRequested),
        (VehicleExpenseStatus.Submitted, VehicleExpenseStatus.Escalated),
        (VehicleExpenseStatus.EvidenceRequested, VehicleExpenseStatus.Submitted),
        (VehicleExpenseStatus.EvidenceRequested, VehicleExpenseStatus.Rejected),
        (VehicleExpenseStatus.Escalated, VehicleExpenseStatus.Approved),
        (VehicleExpenseStatus.Escalated, VehicleExpenseStatus.Rejected),
        (VehicleExpenseStatus.Escalated, VehicleExpenseStatus.EvidenceRequested));

    public bool CanMoveTo(VehicleExpenseStatus target) => Flow.Allows(_status, target);

    /// <summary>Waiting for a reviewer.</summary>
    public bool InReview => _status is VehicleExpenseStatus.Submitted or VehicleExpenseStatus.Escalated;

    public void Review(VehicleExpenseStatus outcome, int reviewerId, DateTime now, string? note)
    {
        _status = Flow.Move(_status, outcome);
        ReviewedAt = now;
        ReviewedBy = reviewerId;
        ReviewNote = note;
        UpdatedAt = now;
        UpdatedBy = reviewerId;
    }

    public void Resubmit(int userId, DateTime now)
    {
        _status = Flow.Move(_status, VehicleExpenseStatus.Submitted);
        Round++;
        ReviewedAt = null;
        ReviewedBy = null;
        ReviewNote = null;
        UpdatedAt = now;
        UpdatedBy = userId;
    }
}

/// <summary>Stored by name.</summary>
internal enum VehicleExpenseType
{
    Fuel,
    Maintenance,
    Repair,
    Toll,
    Parking,

    /// <summary>Cleaning.</summary>
    Wash,
    Tires,
    Oil,
    SpareParts,
    Insurance,
    Licensing,
    Other,
}

/// <summary>Stored by name.</summary>
internal enum VehicleExpenseStatus
{
    Submitted,

    /// <summary>Recognised and posted to the vehicle's custody, once.</summary>
    Approved,

    /// <summary>Final; the reason is kept. May be referred to HR.</summary>
    Rejected,

    /// <summary>The reviewer asked for more evidence; the submitter resubmits.</summary>
    EvidenceRequested,

    /// <summary>Only an Executive decides it now.</summary>
    Escalated,
}

/// <summary>Stored by name.</summary>
internal enum VehiclePostingStatus
{
    NotPosted,
    Posted,
    Reversed,
}

/// <summary>
/// A review flag on a claim: something a reviewer should look at. Never a block, never an accusation: the claim
/// goes through review as every other one, with the flag shown.
/// </summary>
internal sealed class VehicleExpenseFlag
{
    public int Id { get; set; }

    public int ExpenseId { get; set; }

    public VehicleExpenseFlagKind Kind { get; set; }

    public string Detail { get; set; } = "";

    /// <summary>The other claim the flag points to, when it is about another claim.</summary>
    public int? RelatedExpenseId { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>Stored by name.</summary>
internal enum VehicleExpenseFlagKind
{
    DuplicateInvoiceNo,
    DuplicateReceipt,
    SameAmountVehicleDay,
    OdometerNotIncreasing,
    ImplausibleDistance,
    PriceAboveRange,
    NoReferencePrice,
}

/// <summary>One alert to one person (the accountants, the Executives) that a claim was submitted. Saved with the claim; the outbox turns it into a notification.</summary>
internal sealed class VehicleExpenseAlert
{
    public int Id { get; set; }

    public int ExpenseId { get; set; }
    public VehicleExpense Expense { get; set; } = null!;

    public int RecipientUserId { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// A photo or PDF attached to a vehicle record: the odometer, the invoice, a violation ticket. Uploaded first
/// (unbound), bound to exactly one record when it is saved, and never re-bound, replaced or deleted: a claim's
/// evidence is what the reviewer saw. The type comes from the file's first bytes, never from the client.
/// </summary>
internal sealed class FleetAttachment
{
    public int Id { get; set; }

    public FleetAttachmentPurpose Purpose { get; set; }

    /// <summary>One of <see cref="FleetAttachmentOwners"/>; <c>null</c> while unbound.</summary>
    public string? OwnerType { get; set; }
    public int? OwnerId { get; set; }

    public string StorageKey { get; set; } = "";
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long SizeBytes { get; set; }

    /// <summary>SHA-256 of the stored bytes (lower-case hex), computed by the server; used to spot a receipt used twice.</summary>
    public string Sha256 { get; set; } = "";

    public DateTime? CapturedAt { get; set; }

    public Guid IdempotencyKey { get; set; }

    public int UploadedBy { get; set; }
    public DateTime UploadedAt { get; set; }
    public DateTime? BoundAt { get; set; }
}

/// <summary>Stored by name.</summary>
internal enum FleetAttachmentPurpose
{
    Odometer,
    Invoice,
    Receipt,
    Violation,
    Supporting,
}

internal static class FleetAttachmentOwners
{
    public const string VehicleExpense = "VehicleExpense";
    public const string VehicleViolation = "VehicleViolation";

    public static IReadOnlyList<string> All { get; } = [VehicleExpense, VehicleViolation];
}

/// <summary>
/// A violation with a vehicle (a ticket, an accident, damage, misuse): the vehicle, the driver responsible on that
/// day (from the assignment history, never typed), the facts and the evidence. Reviewed (resolved or dismissed) by
/// someone other than its recorder; may be referred to HR, which decides on its own whether anything is deducted.
/// </summary>
internal sealed class VehicleViolation
{
    public int Id { get; set; }

    /// <summary><c>VV-000123</c>, computed by the database from the id.</summary>
    public string Number { get; private set; } = "";

    public int VehicleId { get; set; }
    public Vehicle Vehicle { get; set; } = null!;

    /// <summary>The driver on the violation date, from the assignment history; empty when nobody was assigned that day.</summary>
    public int? DriverEmployeeId { get; set; }
    public int? DriverAssignmentId { get; set; }

    public DateOnly ViolationDate { get; set; }

    public VehicleViolationCategory Category { get; set; }

    public string Description { get; set; } = "";

    /// <summary>The fine or damage when established.</summary>
    public decimal? Amount { get; set; }

    public string? TicketNo { get; set; }

    /// <summary>A trip or tracker reference, only when a real GPS source gave one.</summary>
    public string? TripReference { get; set; }
    public decimal? GpsLatitude { get; set; }
    public decimal? GpsLongitude { get; set; }

    private VehicleViolationStatus _status = VehicleViolationStatus.Recorded;

    public VehicleViolationStatus Status { get => _status; init => _status = value; }

    public DateTime? ReviewedAt { get; set; }
    public int? ReviewedBy { get; set; }
    public string? ReviewNote { get; set; }
    public string? Resolution { get; set; }

    public int? HrReferralId { get; set; }

    public DateTime CreatedAt { get; set; }
    public int CreatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public int? UpdatedBy { get; set; }

    public byte[] RowVersion { get; set; } = [];

    private static readonly StatusFlow<VehicleViolationStatus> Flow = new(
        (VehicleViolationStatus.Recorded, VehicleViolationStatus.UnderReview),
        (VehicleViolationStatus.Recorded, VehicleViolationStatus.Resolved),
        (VehicleViolationStatus.Recorded, VehicleViolationStatus.Dismissed),
        (VehicleViolationStatus.UnderReview, VehicleViolationStatus.Resolved),
        (VehicleViolationStatus.UnderReview, VehicleViolationStatus.Dismissed));

    public bool CanMoveTo(VehicleViolationStatus target) => Flow.Allows(_status, target);

    public void Review(VehicleViolationStatus outcome, int reviewerId, DateTime now, string note, string? resolution)
    {
        _status = Flow.Move(_status, outcome);
        ReviewedAt = now;
        ReviewedBy = reviewerId;
        ReviewNote = note;
        Resolution = resolution;
        UpdatedAt = now;
        UpdatedBy = reviewerId;
    }
}

/// <summary>Stored by name.</summary>
internal enum VehicleViolationCategory
{
    Speeding,
    Parking,
    Traffic,
    Accident,
    Damage,
    Misuse,
    Other,
}

/// <summary>Stored by name.</summary>
internal enum VehicleViolationStatus
{
    Recorded,
    UnderReview,
    Resolved,
    Dismissed,
}
