namespace AlMotaheda.Modules.Projects.Data;

/// <summary>
/// A supplier's relationship with a project for one material on one work item ("اتفاق توريد"): for example El Sewedy
/// supplies electrical cables to Electrical Works → Cables. It names the work the supplier is engaged for, and
/// optionally what was agreed (quantity, unit price or value).
/// </summary>
/// <remarks>
/// <para>Adding a supplier to a project is this record, not a supplier copy: the supplier stays one company record
/// used by every project. The same supplier can have several agreements (several materials, several items).</para>
/// <para>The agreement is not a cost and not a liability. Supplies recorded under it are the cost; their payments
/// settle them. Its agreed value only shows what is still to be delivered (a commitment).</para>
/// </remarks>
internal sealed class SupplyAgreement
{
    public int Id { get; set; }

    public int ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    public int SupplierId { get; set; }
    public Supplier Supplier { get; set; } = null!;

    /// <summary>The main item or sub-item the material is for. Same project.</summary>
    public int ProjectItemId { get; set; }
    public ProjectItem ProjectItem { get; set; } = null!;

    /// <summary>What is supplied, as the site calls it.</summary>
    public required string MaterialName { get; set; }

    /// <summary>One of <c>MeasurementUnits</c>. Supplies under the agreement use the same unit.</summary>
    public required string Unit { get; set; }

    /// <summary>Optional: the quantity agreed.</summary>
    public decimal? AgreedQuantity { get; set; }

    /// <summary>Optional: the unit price agreed.</summary>
    public decimal? AgreedUnitPrice { get; set; }

    /// <summary>Optional: the value agreed. Quantity × unit price when both are set, computed by the server.</summary>
    public decimal? AgreedValue { get; set; }

    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }

    public SupplyAgreementStatus Status { get; set; } = SupplyAgreementStatus.Active;

    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; }
    public int CreatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public int? UpdatedBy { get; set; }

    public byte[] RowVersion { get; set; } = [];
}

internal enum SupplyAgreementStatus
{
    /// <summary>New supplies may be recorded under it.</summary>
    Active,

    /// <summary>Finished: its supplies stay; nothing new is recorded under it, and it commits nothing more.</summary>
    Closed,
}
