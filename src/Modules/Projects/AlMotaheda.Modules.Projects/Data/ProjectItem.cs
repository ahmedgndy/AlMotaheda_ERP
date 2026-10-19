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
/// <para><b>Main items and sub-items:</b> an item with no <see cref="ParentItemId"/> is a main item ("الأعمال
/// الخرسانية"); an item with one is a sub-item of it ("أسمنت", "حديد تسليح"). There are two levels only: a sub-item
/// can't have sub-items. A record is linked to exactly one item, main or sub, so a main item's figures "with its
/// sub-items" are its own records plus its sub-items' records, and no record is ever counted twice.</para>
/// </remarks>
internal sealed class ProjectItem
{
    public int Id { get; set; }

    public int ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    /// <summary>
    /// The main item this is a sub-item of; <c>null</c> for a main item. Always an item of the same project: the
    /// foreign key is on (<see cref="ProjectId"/>, <see cref="ParentItemId"/>), so the database itself refuses a
    /// parent from another project.
    /// </summary>
    public int? ParentItemId { get; set; }
    public ProjectItem? ParentItem { get; set; }

    /// <summary>Unique among the items with the same parent (the main items of the project, or one main item's sub-items).</summary>
    public required string NameAr { get; set; }

    /// <summary>Unique among the items with the same parent.</summary>
    public required string NameEn { get; set; }

    /// <summary>The kind of work: one of <c>WorkTypes</c> (Projects.Contracts).</summary>
    public required string Category { get; set; }

    /// <summary>One of <c>MeasurementUnits</c> (Projects.Contracts).</summary>
    public required string Unit { get; set; }

    public string? Description { get; set; }
    public string? Notes { get; set; }

    public ProjectItemStatus Status { get; set; } = ProjectItemStatus.Active;

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
