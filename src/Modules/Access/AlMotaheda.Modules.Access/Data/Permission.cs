using AlMotaheda.BuildingBlocks.Authorization;

namespace AlMotaheda.Modules.Access.Data;

/// <summary>One action on one module, for example "View" on "projects". Read-only reference data (85 seeded rows).</summary>
internal sealed class Permission
{
    public int Id { get; set; }
    public required string Module { get; set; }
    public PermissionAction Action { get; set; }
    public DateTime CreatedAt { get; set; }
    public int? CreatedBy { get; set; }
}

/// <summary>A permission a role currently grants. Editing a role's permissions replaces its rows.</summary>
internal sealed class RolePermission
{
    public int RoleId { get; set; }
    public int PermissionId { get; set; }
    public DateTime CreatedAt { get; set; }
    public int? CreatedBy { get; set; }

    public Permission Permission { get; set; } = null!;
}
