using AlMotaheda.BuildingBlocks.Domain;

namespace AlMotaheda.Modules.Fleet.Data;

/// <summary>
/// A correction of an approved equipment cost: a signed amount and the reason. An approved record is never edited
/// (its money is frozen); a mistake found later is corrected by an adjustment, which goes through its own approval.
/// Once approved, every figure uses the adjusted amount: the original amount plus its approved adjustments.
/// </summary>
/// <remarks>
/// Refactor plan M6. Who may request one: whoever may record equipment costs; who decides: an Executive, never the
/// requester (owner-accepted proposal). A credit cost is paid once for its full amount (decision F2), so it is corrected
/// only before its payment is requested; the corrected amount never goes below zero.
/// </remarks>
internal sealed class EquipmentCostAdjustment
{
    public int Id { get; set; }

    public int CostId { get; set; }

    /// <summary>Signed, never zero: negative lowers the amount (an over-charge), positive raises it.</summary>
    public decimal Amount { get; set; }

    public string Reason { get; set; } = "";

    private AdjustmentStatus _status = AdjustmentStatus.Requested;

    /// <summary>Set when the adjustment is created; afterwards only <see cref="Decide"/> changes it.</summary>
    public AdjustmentStatus Status { get => _status; init => _status = value; }

    public DateTime RequestedAt { get; set; }
    public int RequestedBy { get; set; }

    public DateTime? DecidedAt { get; set; }
    public int? DecidedBy { get; set; }
    public string? DecisionNote { get; set; }

    public byte[] RowVersion { get; set; } = [];

    /// <summary>Decided once: approved or rejected.</summary>
    private static readonly StatusFlow<AdjustmentStatus> Flow = new(
        (AdjustmentStatus.Requested, AdjustmentStatus.Approved),
        (AdjustmentStatus.Requested, AdjustmentStatus.Rejected));

    public void Decide(AdjustmentStatus outcome, int deciderId, DateTime now, string? note)
    {
        _status = Flow.Move(_status, outcome);
        DecidedAt = now;
        DecidedBy = deciderId;
        DecisionNote = note;
    }
}

/// <summary>Stored by name.</summary>
internal enum AdjustmentStatus
{
    Requested,
    Approved,
    Rejected,
}
