namespace AlMotaheda.Modules.Fleet.Data;

/// <summary>
/// One machine or tool in the company's permanent register. There is one row per machine for its whole life: it
/// moves between projects through <see cref="EquipmentAssignment"/> rows and is never copied per project.
/// </summary>
/// <remarks>
/// Where the machine is today is not stored here: it is the assignment without an end date. Rental terms live in
/// <see cref="EquipmentRental"/>, over time, so a change of rate never changes a cost already recorded.
/// </remarks>
internal sealed class Equipment
{
    public int Id { get; set; }

    /// <summary>The company's asset number, upper-case and unique, for example <c>EX-104</c>.</summary>
    public string AssetNo { get; set; } = "";

    public string NameAr { get; set; } = "";
    public string NameEn { get; set; } = "";

    public EquipmentCategory Category { get; set; }

    /// <summary>Free text, for example "Excavator" or "Generator".</summary>
    public string TypeName { get; set; } = "";

    /// <summary>Serial or plate number.</summary>
    public string? SerialNo { get; set; }

    public EquipmentOwnership Ownership { get; set; }

    public EquipmentCondition Condition { get; set; }

    /// <summary>Only a machine <see cref="EquipmentStatus.InService"/> can be sent to a project.</summary>
    public EquipmentStatus Status { get; set; } = EquipmentStatus.InService;

    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; }
    public int CreatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public int? UpdatedBy { get; set; }

    public byte[] RowVersion { get; set; } = [];
}

/// <summary>The prototype's equipment categories. Stored by name.</summary>
internal enum EquipmentCategory
{
    Tools,
    Medium,
    Heavy,
    Survey,
    Safety,
}

internal enum EquipmentOwnership
{
    Owned,

    /// <summary>Rented from a lessor; the terms are in <see cref="EquipmentRental"/>.</summary>
    Rented,
}

internal enum EquipmentCondition
{
    New,
    Good,
    Used,
    Damaged,
}

internal enum EquipmentStatus
{
    /// <summary>Usable: on a project or available for one.</summary>
    InService,

    UnderMaintenance,
    Lost,
    Retired,
}

/// <summary>
/// The rental terms of a rented machine from one lessor, from a start date until the next terms start. One open row
/// (no end date) per machine at most. Terms are history: new terms close the open ones, nothing is overwritten.
/// </summary>
/// <remarks>The rate here is a reference for the cost form. A cost copies its own rate, so changing terms later
/// never changes a recorded cost.</remarks>
internal sealed class EquipmentRental
{
    public int Id { get; set; }

    public int EquipmentId { get; set; }
    public Equipment Equipment { get; set; } = null!;

    /// <summary>The lessor: a supplier of the Projects module (<c>projects.Supplier</c>). Read through <c>ISupplierDirectory</c>.</summary>
    public int SupplierId { get; set; }

    public RateBasis RateBasis { get; set; }

    public decimal Rate { get; set; }

    public DateOnly StartDate { get; set; }

    /// <summary>Empty while these are the current terms.</summary>
    public DateOnly? EndDate { get; set; }

    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; }
    public int CreatedBy { get; set; }
}

/// <summary>What a rental rate is per. Stored by name.</summary>
internal enum RateBasis
{
    Day,
    Month,
    Hour,
}
