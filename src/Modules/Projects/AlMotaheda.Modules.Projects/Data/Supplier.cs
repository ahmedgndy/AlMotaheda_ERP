namespace AlMotaheda.Modules.Projects.Data;

/// <summary>
/// A company the business buys from or rents from: materials, equipment rental, services. One row per supplier,
/// used by every project. A subcontractor is not a supplier: subcontract companies and their contracts live in
/// the Subcontractors module.
/// </summary>
internal sealed class Supplier
{
    public int Id { get; set; }

    public required string NameAr { get; set; }

    /// <summary>Unique, so the same supplier isn't entered twice under one name.</summary>
    public required string NameEn { get; set; }

    public SupplierKind Kind { get; set; } = SupplierKind.Materials;

    /// <summary>The registered (legal) company name, when it differs from the display names. Optional.</summary>
    public string? LegalName { get; set; }

    public string? ContactPerson { get; set; }

    public string? Phone { get; set; }

    public string? Email { get; set; }

    public string? Address { get; set; }

    /// <summary>Commercial registration number. Optional.</summary>
    public string? CommercialRegistrationNo { get; set; }
    public string? TaxNumber { get; set; }
    public string? Notes { get; set; }

    /// <summary>An inactive supplier keeps its history but can't be chosen for new records.</summary>
    public SupplierStatus Status { get; set; } = SupplierStatus.Active;

    public DateTime CreatedAt { get; set; }
    public int CreatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public int? UpdatedBy { get; set; }

    public byte[] RowVersion { get; set; } = [];
}

/// <summary>What the supplier mainly provides. Stored by name.</summary>
internal enum SupplierKind
{
    Materials,
    Equipment,
    Services,
    Fuel,
    Other,
}

internal enum SupplierStatus
{
    Active,
    Inactive,
}
