namespace AlMotaheda.Modules.Projects.Data;

/// <summary>
/// One planned component of a project item ("بند"), for items assembled from several costs, such as concrete work
/// made of cement, sand, aggregate, labor and equipment (docs/cost-flow §2.4). A plan, never a cost.
/// </summary>
/// <remarks>
/// Costs stay on their source records, linked to the item as before. The item detail sorts the item's own records
/// into its components, so the components and "other" always add up to the item's cost and nothing is counted twice.
/// </remarks>
internal sealed class ProjectItemComponent
{
    public int Id { get; set; }

    public int ProjectItemId { get; set; }
    public ProjectItem ProjectItem { get; set; } = null!;

    public ComponentKind Kind { get; set; }

    /// <summary>For <see cref="ComponentKind.Material"/> only: the project catalog item (same project).</summary>
    public int? CatalogItemId { get; set; }
    public CatalogItem? CatalogItem { get; set; }

    /// <summary>The quantity planned for the whole item, in the catalog item's unit. Optional.</summary>
    public decimal? PlannedQuantity { get; set; }

    /// <summary>The quantity planned per unit of the item (a consumption ratio). Optional.</summary>
    public decimal? QuantityPerUnit { get; set; }

    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; }
    public int CreatedBy { get; set; }
}

/// <summary>Which source records a component collects. Stored by name.</summary>
internal enum ComponentKind
{
    /// <summary>Approved supplies of one catalog item linked to the item.</summary>
    Material,

    /// <summary>Approved internal labor (Workforce) and allocated staff salaries (HR) on the item.</summary>
    Labor,

    /// <summary>Approved equipment costs (Fleet) on the item.</summary>
    Equipment,

    /// <summary>Approved subcontract work (Subcontractors) on the item.</summary>
    Subcontract,
}
