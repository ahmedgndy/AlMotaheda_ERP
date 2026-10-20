using AlMotaheda.BuildingBlocks.Domain;

namespace AlMotaheda.Modules.Procurement.Data;

/// <summary>
/// A purchase requisition (PR): what a project needs, on which cost items, estimated at the project's current
/// catalog price. Approved by the policy or by people, then worked by Purchasing.
/// </summary>
internal sealed class Requisition
{
    private static readonly StatusFlow<RequisitionStatus> Flow = new(
        (RequisitionStatus.Submitted, RequisitionStatus.ManagerApproved),
        (RequisitionStatus.Submitted, RequisitionStatus.Approved),
        (RequisitionStatus.ManagerApproved, RequisitionStatus.Approved),
        (RequisitionStatus.Submitted, RequisitionStatus.Rejected),
        (RequisitionStatus.ManagerApproved, RequisitionStatus.Rejected),
        (RequisitionStatus.Submitted, RequisitionStatus.Cancelled),
        (RequisitionStatus.ManagerApproved, RequisitionStatus.Cancelled),
        (RequisitionStatus.Approved, RequisitionStatus.Cancelled),
        (RequisitionStatus.Approved, RequisitionStatus.Ordered),
        (RequisitionStatus.Ordered, RequisitionStatus.Approved));

    private RequisitionStatus _status = RequisitionStatus.Submitted;

    public int Id { get; set; }

    /// <summary><c>PR-000123</c>, computed by the database from the id.</summary>
    public string Number { get; private set; } = "";

    public int ProjectId { get; set; }

    public RequisitionStatus Status { get => _status; init => _status = value; }

    public DateOnly? NeededBy { get; set; }

    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; }
    public int CreatedBy { get; set; }

    public DateTime? ApprovedAt { get; private set; }

    public byte[] RowVersion { get; set; } = [];

    public List<RequisitionLine> Lines { get; } = [];

    public List<RequisitionEvent> Events { get; } = [];

    public bool IsOpenForOrders => Status is RequisitionStatus.Approved;

    public void Record(string action, int? userId, string? actorLabel, string? policy, DateTime now, string? note) =>
        Events.Add(new RequisitionEvent
        {
            Action = action,
            Status = Status,
            ActorUserId = userId,
            ActorLabel = actorLabel,
            PolicyName = policy,
            At = now,
            Note = note,
        });

    /// <summary>The approval step under the auto-approval policy: recorded, by the system, with the policy's name.</summary>
    public void ApproveByPolicy(DateTime now)
    {
        _status = Flow.Move(Status, RequisitionStatus.Approved);
        ApprovedAt = now;
        Record("Approve", null, "System (Auto-Approval Policy)", ProcurementPolicy.AutoApprovalPolicyName, now, null);
    }

    /// <summary>A person's approval: the project manager's first step, or the Accountant's final one.</summary>
    public void Approve(int userId, DateTime now, string? note)
    {
        var final = Status == RequisitionStatus.ManagerApproved;
        _status = Flow.Move(Status, final ? RequisitionStatus.Approved : RequisitionStatus.ManagerApproved);
        if (final)
        {
            ApprovedAt = now;
        }

        Record(final ? "AccountantApprove" : "ManagerApprove", userId, null, null, now, note);
    }

    public void Reject(int userId, DateTime now, string reason)
    {
        _status = Flow.Move(Status, RequisitionStatus.Rejected);
        Record("Reject", userId, null, null, now, reason);
    }

    public void Cancel(int userId, DateTime now, string reason)
    {
        _status = Flow.Move(Status, RequisitionStatus.Cancelled);
        Record("Cancel", userId, null, null, now, reason);
    }

    /// <summary>Every approved quantity is on purchase orders (or back to open when an order is cancelled or revised down).</summary>
    public void MarkOrdered(bool fullyOrdered, int userId, DateTime now)
    {
        var target = fullyOrdered ? RequisitionStatus.Ordered : RequisitionStatus.Approved;
        if (Status != target)
        {
            _status = Flow.Move(Status, target);
            Record(fullyOrdered ? "Ordered" : "Reopened", userId, null, null, now, null);
        }
    }
}

/// <summary>Stored by name.</summary>
internal enum RequisitionStatus
{
    Submitted,
    ManagerApproved,
    Approved,
    Ordered,
    Rejected,
    Cancelled,
}

/// <summary>One thing the project needs. With a catalog item, its name, unit and current price come from the catalog.</summary>
internal sealed class RequisitionLine
{
    public int Id { get; set; }

    public int RequisitionId { get; set; }

    public int? CatalogItemId { get; set; }

    /// <summary>The Price Control item this line is bought as, when the requester names one: its orders and invoices are compared with it.</summary>
    public int? PriceItemId { get; set; }

    /// <summary>The project's cost item (بند) the purchase is for.</summary>
    public int? ProjectItemId { get; set; }

    public string Description { get; set; } = "";

    public string Unit { get; set; } = "";

    public decimal Quantity { get; set; }

    /// <summary>The catalog price on the day the PR was made; <c>null</c> without a catalog item or price.</summary>
    public decimal? EstimatedUnitPrice { get; set; }
}

/// <summary>One step of a PR's history. A system step has no user but a label and the policy that made it.</summary>
internal sealed class RequisitionEvent
{
    public long Id { get; set; }

    public int RequisitionId { get; set; }

    public string Action { get; set; } = "";

    /// <summary>The status after the step.</summary>
    public RequisitionStatus Status { get; set; }

    public int? ActorUserId { get; set; }

    /// <summary>For system steps: <c>System (Auto-Approval Policy)</c>.</summary>
    public string? ActorLabel { get; set; }

    public string? PolicyName { get; set; }

    public DateTime At { get; set; }

    public string? Note { get; set; }
}
