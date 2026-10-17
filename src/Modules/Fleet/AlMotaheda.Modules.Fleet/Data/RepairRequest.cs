using AlMotaheda.BuildingBlocks.Domain;

namespace AlMotaheda.Modules.Fleet.Data;

/// <summary>
/// A request to repair a machine on a project (docs/cost-flow §2.7). It is never a cost and never a completed
/// maintenance job: the repair's cost is its own equipment cost, with its own approval and payment, and can be
/// linked here when the repair is done.
/// </summary>
internal sealed class RepairRequest
{
    public int Id { get; set; }

    public int EquipmentId { get; set; }
    public Equipment Equipment { get; set; } = null!;

    public int ProjectId { get; set; }

    public required string Problem { get; set; }

    public RepairPriority Priority { get; set; } = RepairPriority.Normal;

    private RepairRequestStatus _status = RepairRequestStatus.Open;

    /// <summary>Set when the record is created; afterwards only this class's methods change it.</summary>
    public RepairRequestStatus Status { get => _status; init => _status = value; }

    public DateTime RequestedAt { get; set; }

    /// <summary>The requester. They can never decide their own request.</summary>
    public int RequestedBy { get; set; }

    public DateTime? ReviewedAt { get; set; }
    public int? ReviewedBy { get; set; }
    public string? ReviewNote { get; set; }

    public DateTime? CompletedAt { get; set; }
    public int? CompletedBy { get; set; }
    public string? Result { get; set; }

    /// <summary>The equipment cost of the repair (repair, maintenance or spare parts), when there is one.</summary>
    public int? EquipmentCostId { get; set; }

    public DateTime? CancelledAt { get; set; }
    public int? CancelledBy { get; set; }
    public string? CancelReason { get; set; }

    public byte[] RowVersion { get; set; } = [];

    private static readonly StatusFlow<RepairRequestStatus> Flow = new(
        (RepairRequestStatus.Open, RepairRequestStatus.Approved),
        (RepairRequestStatus.Open, RepairRequestStatus.Rejected),
        (RepairRequestStatus.Open, RepairRequestStatus.Cancelled),
        (RepairRequestStatus.Approved, RepairRequestStatus.Completed),
        (RepairRequestStatus.Approved, RepairRequestStatus.Cancelled));

    public void Review(RepairRequestStatus outcome, int reviewerId, DateTime now, string? note)
    {
        if (outcome is not (RepairRequestStatus.Approved or RepairRequestStatus.Rejected))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "A review approves or rejects.");
        }

        _status = Flow.Move(_status, outcome);
        ReviewedAt = now;
        ReviewedBy = reviewerId;
        ReviewNote = note;
    }

    public void Complete(int userId, DateTime now, string result, int? equipmentCostId)
    {
        _status = Flow.Move(_status, RepairRequestStatus.Completed);
        CompletedAt = now;
        CompletedBy = userId;
        Result = result;
        EquipmentCostId = equipmentCostId;
    }

    public void Cancel(int userId, DateTime now, string reason)
    {
        _status = Flow.Move(_status, RepairRequestStatus.Cancelled);
        CancelledAt = now;
        CancelledBy = userId;
        CancelReason = reason;
    }
}

/// <summary>Stored by name.</summary>
internal enum RepairPriority
{
    Low,
    Normal,
    Urgent,
}

/// <summary>Stored by name.</summary>
internal enum RepairRequestStatus
{
    /// <summary>Waiting for someone with equipment Approve.</summary>
    Open,
    Approved,
    Rejected,

    /// <summary>The repair is done; the result is recorded.</summary>
    Completed,
    Cancelled,
}
