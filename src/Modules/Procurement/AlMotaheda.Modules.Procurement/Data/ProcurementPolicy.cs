namespace AlMotaheda.Modules.Procurement.Data;

/// <summary>
/// The company's procurement policy: one row, changed only by the Executive, every change in the audit trail.
/// Auto-approval is a policy, not a shortcut: the requisition still passes its approval step, recorded as approved
/// by the system under this policy (docs/modernization/12-procurement-design.md).
/// </summary>
internal sealed class ProcurementPolicy
{
    public const int SingletonId = 1;
    public const string AutoApprovalPolicyName = "Procurement.AutoApproveRequisitions";

    public int Id { get; set; } = SingletonId;

    /// <summary>On (the default): requisitions are approved by the system. Off: Project Manager, then Accountant.</summary>
    public bool AutoApproveRequisitions { get; set; } = true;

    /// <summary>How far an invoice unit price may differ from the PO price and still match, in percent. Default 0.</summary>
    public decimal PriceTolerancePercent { get; set; }

    public DateTime? UpdatedAt { get; set; }
    public int? UpdatedBy { get; set; }

    public byte[] RowVersion { get; set; } = [];
}
