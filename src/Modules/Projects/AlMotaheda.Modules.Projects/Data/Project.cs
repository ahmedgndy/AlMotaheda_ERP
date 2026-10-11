namespace AlMotaheda.Modules.Projects.Data;

/// <summary>
/// A construction project and its site. Projects are never deleted: a finished project is set to
/// <see cref="ProjectStatus.Completed"/>.
/// </summary>
/// <remarks>
/// The project manager is not a column here. It is the active <see cref="ProjectMember"/> with the
/// <see cref="MemberResponsibility.ProjectManager"/> responsibility, so "who is on this project" has one source.
/// Money (budget, contract value, opening figures) is not stored in this version.
/// </remarks>
internal sealed class Project
{
    public const int DefaultSiteRadiusMeters = 500;

    public int Id { get; set; }

    /// <summary>Short code used on documents, for example <c>BDR-MALL-011</c>. Unique; stored upper-case.</summary>
    public required string Code { get; set; }

    public required string NameAr { get; set; }
    public required string NameEn { get; set; }

    /// <summary>The client as typed on the form. There is no client table in this version.</summary>
    public required string ClientNameEn { get; set; }
    public string? ClientNameAr { get; set; }

    public string? LocationAr { get; set; }
    public string? LocationEn { get; set; }

    /// <summary>The number on the signed client contract.</summary>
    public string? ContractNo { get; set; }

    public string? AddressAr { get; set; }
    public string? AddressEn { get; set; }

    /// <summary>Centre of the site. Both coordinates are set, or neither.</summary>
    public decimal? SiteLatitude { get; set; }
    public decimal? SiteLongitude { get; set; }

    /// <summary>The site area around the centre (geofence), used by vehicle GPS and site attendance.</summary>
    public int SiteRadiusMeters { get; set; } = DefaultSiteRadiusMeters;

    public DateOnly StartDate { get; set; }

    /// <summary>Expected end date. Always after <see cref="StartDate"/>.</summary>
    public DateOnly EndDate { get; set; }

    public ProjectStatus Status { get; set; } = ProjectStatus.Planning;

    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; }
    public int CreatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public int? UpdatedBy { get; set; }

    /// <summary>Changes on every update, so two people editing the same project can't overwrite each other.</summary>
    public byte[] RowVersion { get; set; } = [];

    public List<ProjectMember> Members { get; } = [];
}

/// <summary>The project's stage. Stored by name.</summary>
internal enum ProjectStatus
{
    Planning,
    Active,

    /// <summary>Set by hand. Counted as active.</summary>
    Delayed,
    OnHold,

    /// <summary>Read-only for everyone except an Executive. Other work on the project stops.</summary>
    Completed,
}
