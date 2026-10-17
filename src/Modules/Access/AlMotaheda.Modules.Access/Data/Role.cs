using AlMotaheda.Modules.Access.Contracts;

namespace AlMotaheda.Modules.Access.Data;

/// <summary>
/// One of the six fixed roles. Rows are seeded; there is no endpoint to add or delete roles.
/// Role names live in the frontend's translations, keyed by <see cref="Code"/>.
/// </summary>
internal sealed class Role
{
    public int Id { get; set; }

    /// <summary>Stable code, for example <c>executive</c>. See <see cref="RoleCodes"/>.</summary>
    public required string Code { get; set; }

    public DateTime CreatedAt { get; set; }
    public int? CreatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public int? UpdatedBy { get; set; }

    /// <summary>Changes on every update, so two people editing the same role's permissions can't overwrite each other.</summary>
    public byte[] RowVersion { get; set; } = [];

    public List<RolePermission> Permissions { get; } = [];
}

/// <summary>
/// The role codes, as stored in <see cref="Role.Code"/> and sent in access tokens. The values are published to
/// other modules through <see cref="AccessRoles"/>, so both always agree.
/// </summary>
internal static class RoleCodes
{
    public const string Executive = AccessRoles.Executive;
    public const string Manager = AccessRoles.Manager;
    public const string Engineer = AccessRoles.Engineer;
    public const string OfficeAdmin = AccessRoles.OfficeAdmin;
    public const string Accountant = AccessRoles.Accountant;
    public const string Staff = AccessRoles.Staff;
    public const string Hr = AccessRoles.Hr;
    public const string Purchasing = AccessRoles.Purchasing;
    public const string FinancialManager = AccessRoles.FinancialManager;
}
