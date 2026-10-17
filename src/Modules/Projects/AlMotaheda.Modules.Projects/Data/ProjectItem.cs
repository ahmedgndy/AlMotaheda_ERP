namespace AlMotaheda.Modules.Projects.Data;

/// <summary>
/// A project item ("بند"): one major piece of work of one project, such as "الأعمال الخرسانية". Supplier records,
/// subcontract work and internal labor are linked to it, and the items screen shows what it has really cost.
/// </summary>
/// <remarks>
/// <para>An item holds no amounts. Its totals are sums of the source records linked to it: supplier records in this
/// module, labor in Workforce, subcontract work in Subcontractors.</para>
/// <para>An item with linked records is never deleted. It is set to <see cref="ProjectItemStatus.Inactive"/>: its
/// records stay linked and stay in the totals, and no new record can be linked to it.</para>
/// </remarks>
internal sealed class ProjectItem
{
    public int Id { get; set; }

    public int ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    /// <summary>Unique within the project.</summary>
    public required string NameAr { get; set; }

    /// <summary>Unique within the project.</summary>
    public required string NameEn { get; set; }

    /// <summary>The kind of work: one of <c>WorkTypes</c> (Projects.Contracts).</summary>
    public required string Category { get; set; }

    /// <summary>One of <c>MeasurementUnits</c> (Projects.Contracts).</summary>
    public required string Unit { get; set; }

    public string? Description { get; set; }
    public string? Notes { get; set; }

    public ProjectItemStatus Status { get; set; } = ProjectItemStatus.Active;

    /// <summary>The master catalog definition this item was selected from, or empty for an item made on the project only.</summary>
    public int? MasterItemId { get; set; }
    public MasterItem? MasterItem { get; set; }

    /// <summary>The planned components of an item assembled from several costs (docs/cost-flow §2.4).</summary>
    public List<ProjectItemComponent> Components { get; } = [];

    /// <summary>Set, with <see cref="DeactivatedBy"/>, only while the item is inactive.</summary>
    public DateTime? DeactivatedAt { get; set; }
    public int? DeactivatedBy { get; set; }

    public DateTime CreatedAt { get; set; }
    public int CreatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public int? UpdatedBy { get; set; }

    public byte[] RowVersion { get; set; } = [];
}

internal enum ProjectItemStatus
{
    Active,
    Inactive,
}
